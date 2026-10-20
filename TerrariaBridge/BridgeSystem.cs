using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaBridge;

public sealed class BridgeSystem : ModSystem
{
    private const string Protocol = "SDTV-TERRARIA-BRIDGE";
    private const int Version = 1;
    private static string Sender => Main.dedServ ? "terraria_server" : "terraria";
    private static readonly ConcurrentQueue<string> Incoming = new();
    private static readonly ConcurrentQueue<string> RemoteControls = new();
    private static readonly ConcurrentQueue<string> Status = new();
    private static readonly Queue<(string ActionId, string EventId, string SenderGeneration, int Slot, int Owner, string ItemId, bool ConsumeItem, bool ConsumeAmmo, int AmmoSlot, string AmmoId, bool ConsumeBait, int BaitSlot, string BaitId)> PendingSceneItemConsumptions = new();
    private static readonly object SendLock = new();
    private static TcpClient? client;
    private static StreamWriter? writer;
    private static Thread? receiveThread;
    private static volatile bool stopping;
    private static long sequence;
    private static readonly string Generation = Guid.NewGuid().ToString("N");
    private static JsonElement? pendingStardewPlayer;
    private static string? pendingChallengeId;
    private static string? pendingChallengeEventId;
    private static string? activeChallengeId;
    private static int challengeNpcIndex = -1;
    private static int port;
    private static int framePort;
    private static TcpClient? frameClient;
    private static Stream? frameStream;
    private static long lastFrameAt;
    private static long lastFrameConnectAttempt;
    private static bool autoPauseOverride;
    private static bool savedAutoPause;
    // On macOS, the inactive client loop can set this private flag after the
    // window loses focus.  Clearing only autoPause/gamePaused is too late for
    // the gameplay gate, so keep a cached reflection handle and clear it
    // before each original DoUpdate call while Stardew owns the controls.
    private static readonly FieldInfo? GameInactiveField =
        typeof(Main).GetField("gameInactive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? CanUpdateGameplayField =
        typeof(Main).GetField("<CanUpdateGameplay>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);

    public override void Load()
    {
        port = int.TryParse(Environment.GetEnvironmentVariable("SDTV_BRIDGE_PORT"), out var configured) ? configured : 28741;
        framePort = int.TryParse(Environment.GetEnvironmentVariable("SDTV_FRAME_PORT"), out var frameConfigured) ? frameConfigured : 28751;
        stopping = false;
        receiveThread = new Thread(ConnectLoop) { IsBackground = true, Name = "TerrariaBridge" };
        receiveThread.Start();
        On_Main.DoUpdate += BeforeUpdate;
        On_Main.Draw += AfterDraw;
    }

    public override void Unload()
    {
        stopping = true;
        client?.Close();
        frameClient?.Close();
        On_Main.DoUpdate -= BeforeUpdate;
        On_Main.Draw -= AfterDraw;
        SceneFusion.Dispose();
    }

    private static void BeforeUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
    {
        // World ModSystem hooks stop running when a single-player client is
        // inactive. Consume Stardew control packets at the outer client loop so
        // the first packet can disable auto-pause and restart gameplay updates.
        while (RemoteControls.TryDequeue(out var line))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("payload", out var payload))
                    ApplyRemoteControls(payload);
            }
            catch (JsonException)
            {
                Main.NewText("Terraria bridge received malformed control JSON.", Microsoft.Xna.Framework.Color.OrangeRed);
            }
        }

        if (FusionTestAgentState.RemoteEnabled &&
            Environment.TickCount64 - FusionTestAgentState.RemoteLastUpdate > 500)
            ResetRemoteControls();

        // Stardew is the active window during fusion. Terraria's single-player
        // AutoPause would otherwise stop the full world update as soon as its
        // window loses focus, so remote direction, mouse use, and item input
        // would never reach the player hooks. Keep the user's setting and
        // restore it as soon as remote control ends.
        if (!Main.dedServ && FusionTestAgentState.RemoteEnabled)
        {
            if (!autoPauseOverride)
            {
                savedAutoPause = Main.autoPause;
                autoPauseOverride = true;
            }
            Main.autoPause = false;
            Main.gamePaused = false;
            CanUpdateGameplayField?.SetValue(null, true);
            GameInactiveField?.SetValue(null, false);
            Main.instance.InactiveSleepTime = TimeSpan.Zero;
        }
        else if (autoPauseOverride)
        {
            Main.autoPause = savedAutoPause;
            autoPauseOverride = false;
        }

        orig(self, ref gameTime);

        // The native update can recalculate the inactive state while it runs.
        // Clear it once more so the next frame and the player hooks see an
        // active gameplay loop even though Stardew is the front window.
        if (!Main.dedServ && FusionTestAgentState.RemoteEnabled)
        {
            GameInactiveField?.SetValue(null, false);
            Main.gamePaused = false;
            CanUpdateGameplayField?.SetValue(null, true);
        }
    }

    public override void PostUpdateEverything()
    {
        while (Incoming.TryDequeue(out var line))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                if (kind == "event" && root.TryGetProperty("event", out var eventValue))
                {
                    var eventName = eventValue.GetString();
                    if (eventName == "terraria_control" && root.TryGetProperty("payload", out var controlPayload))
                    {
                        ApplyRemoteControls(controlPayload);
                    }
                    else if (eventName == "enter_terraria" && root.TryGetProperty("payload", out var entryPayload) &&
                        entryPayload.TryGetProperty("player", out var entryPlayer))
                    {
                        pendingStardewPlayer = entryPlayer.Clone();
                        if (entryPayload.TryGetProperty("challenge_id", out var challengeId) &&
                            challengeId.ValueKind == JsonValueKind.String)
                        {
                            pendingChallengeId = challengeId.GetString();
                            pendingChallengeEventId = root.TryGetProperty("event_id", out var eventId) ? eventId.GetString() : null;
                        }
                    }
                    else if (eventName == "scene_item_consumed" && root.TryGetProperty("payload", out var consumptionPayload))
                    {
                        string actionId = consumptionPayload.TryGetProperty("action_id", out var actionIdValue)
                            ? actionIdValue.GetString() ?? ""
                            : "";
                        string itemId = consumptionPayload.TryGetProperty("item_id", out var itemIdValue)
                            ? itemIdValue.GetString() ?? ""
                            : "";
                        int slot = consumptionPayload.TryGetProperty("slot", out var slotValue) && slotValue.TryGetInt32(out int parsedSlot)
                            ? parsedSlot
                            : -1;
                        int owner = consumptionPayload.TryGetProperty("owner", out var ownerValue) && ownerValue.TryGetInt32(out int parsedOwner)
                            ? parsedOwner
                            : 0;
                        string eventId = root.TryGetProperty("event_id", out var consumptionEventId)
                            ? consumptionEventId.GetString() ?? ""
                            : "";
                        string senderGeneration = root.TryGetProperty("generation", out var generationValue)
                            ? generationValue.GetString() ?? ""
                            : "";
                        bool consumeItem = !consumptionPayload.TryGetProperty("consume_item", out var consumeItemValue) || consumeItemValue.GetBoolean();
                        bool consumeAmmo = consumptionPayload.TryGetProperty("consume_ammo", out var consumeAmmoValue) && consumeAmmoValue.GetBoolean();
                        int ammoSlot = consumptionPayload.TryGetProperty("ammo_slot", out var ammoSlotValue) && ammoSlotValue.TryGetInt32(out int parsedAmmoSlot)
                            ? parsedAmmoSlot
                            : -1;
                        string ammoId = consumptionPayload.TryGetProperty("ammo_id", out var ammoIdValue)
                            ? ammoIdValue.GetString() ?? ""
                            : "";
                        bool consumeBait = consumptionPayload.TryGetProperty("consume_bait", out var consumeBaitValue) && consumeBaitValue.GetBoolean();
                        int baitSlot = consumptionPayload.TryGetProperty("bait_slot", out var baitSlotValue) && baitSlotValue.TryGetInt32(out int parsedBaitSlot)
                            ? parsedBaitSlot
                            : -1;
                        string baitId = consumptionPayload.TryGetProperty("bait_id", out var baitIdValue)
                            ? baitIdValue.GetString() ?? ""
                            : "";
                        PendingSceneItemConsumptions.Enqueue((actionId, eventId, senderGeneration, slot, owner, itemId, consumeItem, consumeAmmo, ammoSlot, ammoId, consumeBait, baitSlot, baitId));
                    }
                    if (eventName is not ("terraria_control" or "scene_item_consumed"))
                        Main.NewText($"Bridge event: {eventValue.GetString()}", Microsoft.Xna.Framework.Color.LightGreen);
                }
                else if (kind == "peer_snapshot" && root.TryGetProperty("payload", out var snapshot))
                {
                    pendingStardewPlayer = snapshot.Clone();
                }
            }
            catch (JsonException)
            {
                Main.NewText("Terraria bridge received malformed JSON.", Microsoft.Xna.Framework.Color.OrangeRed);
            }
        }

        while (Status.TryDequeue(out var status))
            Main.NewText(status, Microsoft.Xna.Framework.Color.Gray);

        if (!Main.gameMenu)
        {
            while (PendingSceneItemConsumptions.TryDequeue(out var consumption))
            {
                ConsumeSceneItem(consumption);
            }
        }

        if (FusionTestAgentState.RemoteEnabled &&
            Environment.TickCount64 - FusionTestAgentState.RemoteLastUpdate > 500)
        {
            ResetRemoteControls();
        }

        if (!Main.gameMenu && pendingStardewPlayer.HasValue)
        {
            ApplyStardewHealth(pendingStardewPlayer.Value);
            pendingStardewPlayer = null;
        }

        if (StartChallengeIfReady())
        {
            SendEventReceipt(pendingChallengeEventId);
            pendingChallengeEventId = null;
        }

        if (Main.GameUpdateCount % 60 == 0)
            SendHeartbeat();
    }

    private static void ApplyRemoteControls(JsonElement payload)
    {
        SceneFusion.ReadControls(payload);
        FusionTestAgentState.RemoteEnabled = payload.TryGetProperty("active", out var active) && active.GetBoolean();
        bool sceneFusion = payload.TryGetProperty("scene", out var sceneMode) && sceneMode.GetBoolean();
        bool remoteLeft = payload.TryGetProperty("left", out var left) && left.GetBoolean();
        bool remoteRight = payload.TryGetProperty("right", out var right) && right.GetBoolean();
        bool remoteJump = payload.TryGetProperty("jump", out var jump) && jump.GetBoolean();
        FusionTestAgentState.RemoteLeft = !sceneFusion && remoteLeft;
        FusionTestAgentState.RemoteRight = !sceneFusion && remoteRight;
        FusionTestAgentState.RemoteJump = !sceneFusion && remoteJump;
        if (payload.TryGetProperty("jump_pressed", out var jumpPressed) && jumpPressed.GetBoolean())
            FusionTestAgentState.RemoteJumpPressedUntil = Main.GameUpdateCount + 3;
        FusionTestAgentState.RemoteFire = payload.TryGetProperty("fire", out var fire) && fire.GetBoolean();
        if (payload.TryGetProperty("fire_pressed", out var firePressed) && firePressed.GetBoolean())
            FusionTestAgentState.RemoteFirePressedUntil = Main.GameUpdateCount + 3;
        FusionTestAgentState.RemoteAltFire = payload.TryGetProperty("alt_fire", out var altFire) && altFire.GetBoolean();
        if (payload.TryGetProperty("alt_fire_pressed", out var altFirePressed) && altFirePressed.GetBoolean())
            FusionTestAgentState.RemoteAltFirePressedUntil = Main.GameUpdateCount + 3;
        FusionTestAgentState.RemoteInventory = payload.TryGetProperty("inventory", out var inventory) && inventory.GetBoolean();
        if (payload.TryGetProperty("inventory_pressed", out var inventoryPressed) && inventoryPressed.GetBoolean())
            FusionTestAgentState.RemoteInventoryPressedUntil = Main.GameUpdateCount + 3;
        bool map = payload.TryGetProperty("map", out var mapValue) && mapValue.GetBoolean();
        bool mapPressed = payload.TryGetProperty("map_pressed", out var mapPressedValue) && mapPressedValue.GetBoolean();
        if (mapPressed || (map && !FusionTestAgentState.RemoteMap))
            Main.mapFullscreen = !Main.mapFullscreen;
        FusionTestAgentState.RemoteMap = map;
        FusionTestAgentState.RemoteSlot = payload.TryGetProperty("slot", out var slot) ? slot.GetInt32() : -1;
        if (payload.TryGetProperty("slot_pressed", out var slotPressed) && slotPressed.GetInt32() is >= 0 and <= 9)
            Main.LocalPlayer.selectedItem = slotPressed.GetInt32();
        FusionTestAgentState.RemoteMouseX = payload.TryGetProperty("mouse_x", out var mouseX) ? (float)mouseX.GetDouble() : 0.5f;
        FusionTestAgentState.RemoteMouseY = payload.TryGetProperty("mouse_y", out var mouseY) ? (float)mouseY.GetDouble() : 0.5f;
        FusionTestAgentState.RemoteLastUpdate = Environment.TickCount64;
    }

    private static void ConsumeSceneItem((string ActionId, string EventId, string SenderGeneration, int Slot, int Owner, string ItemId, bool ConsumeItem, bool ConsumeAmmo, int AmmoSlot, string AmmoId, bool ConsumeBait, int BaitSlot, string BaitId) request)
    {
        bool validOwner = request.Owner >= 0 && request.Owner < Main.maxPlayers && Main.player[request.Owner].active;
        bool consumed = validOwner && Main.player[request.Owner].GetModPlayer<SceneFusionPlayer>()
            .TryConsumeSceneItem(request.ActionId, request.Slot, request.ItemId, request.ConsumeItem, request.ConsumeAmmo, request.AmmoSlot, request.AmmoId, request.ConsumeBait, request.BaitSlot, request.BaitId);
        if (consumed && Main.dedServ)
        {
            if (request.ConsumeItem)
                NetMessage.SendData(MessageID.SyncEquipment, number: request.Owner, number2: request.Slot);
            if (request.ConsumeAmmo)
                NetMessage.SendData(MessageID.SyncEquipment, number: request.Owner, number2: request.AmmoSlot);
            if (request.ConsumeBait)
                NetMessage.SendData(MessageID.SyncEquipment, number: request.Owner, number2: request.BaitSlot);
        }
        else if (!consumed && !Main.dedServ)
            Main.NewText("A Stardew item action could not match its Terraria hotbar item; no Terraria item was consumed.", Microsoft.Xna.Framework.Color.Orange);

        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = Sender,
            generation = Generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event",
            @event = "scene_item_consumption_result",
            payload = new { action_id = request.ActionId, item_id = request.ItemId, consumed, ammo_slot = request.AmmoSlot, bait_slot = request.BaitSlot }
        });
        SendEventReceipt(request.EventId);
    }

    private static void SendEventReceipt(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return;
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = Sender,
            generation = Generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event_receipt",
            event_id = eventId
        });
    }

    private static void ResetRemoteControls()
    {
        SceneFusion.End();
        FusionTestAgentState.RemoteEnabled = false;
        FusionTestAgentState.RemoteLeft = false;
        FusionTestAgentState.RemoteRight = false;
        FusionTestAgentState.RemoteJump = false;
        FusionTestAgentState.RemoteJumpPressedUntil = 0;
        FusionTestAgentState.RemoteFire = false;
        FusionTestAgentState.RemoteFirePressedUntil = 0;
        FusionTestAgentState.RemoteAltFire = false;
        FusionTestAgentState.RemoteAltFirePressedUntil = 0;
        FusionTestAgentState.RemoteInventory = false;
        FusionTestAgentState.RemoteInventoryPressedUntil = 0;
        FusionTestAgentState.RemoteMap = false;
        FusionTestAgentState.RemoteSlot = -1;
    }

    private static void AfterDraw(On_Main.orig_Draw orig, Main self, GameTime gameTime)
    {
        orig(self, gameTime);
        long now = Environment.TickCount64;
        if (!Main.dedServ && now - lastFrameAt >= (SceneFusion.Enabled ? 50 : 100))
        {
            lastFrameAt = now;
            SendFrame();
        }
    }

    private static void SendFrame()
    {
        try
        {
            byte[]? raw = SceneFusion.Enabled ? SceneFusion.Capture() : CaptureFullFrame();
            if (raw == null)
                return;
            using var compressed = new MemoryStream();
            using (var deflater = new DeflateStream(compressed, CompressionLevel.Fastest, true))
                deflater.Write(raw, 0, raw.Length);
            var payload = compressed.ToArray();
            if (payload.Length > 2_000_000)
                return;

            if (frameClient == null || !frameClient.Connected)
            {
                if (Environment.TickCount64 - lastFrameConnectAttempt < 1000)
                    return;
                lastFrameConnectAttempt = Environment.TickCount64;
                frameClient?.Close();
                frameClient = new TcpClient();
                frameClient.Connect("127.0.0.1", framePort);
                frameStream = frameClient.GetStream();
            }

            var header = new byte[]
            {
                (byte)(payload.Length >> 24), (byte)(payload.Length >> 16),
                (byte)(payload.Length >> 8), (byte)payload.Length
            };
            frameStream!.Write(header, 0, header.Length);
            frameStream.Write(payload, 0, payload.Length);
            frameStream.Flush();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            frameClient?.Close();
            frameClient = null;
            frameStream = null;
        }
    }

    private static byte[] CaptureFullFrame()
    {
        var graphics = Main.instance.GraphicsDevice;
        int sourceWidth = graphics.PresentationParameters.BackBufferWidth;
        int sourceHeight = graphics.PresentationParameters.BackBufferHeight;
        var source = new Color[sourceWidth * sourceHeight];
        graphics.GetBackBufferData(source);
        var scaled = new Color[FrameWidth * FrameHeight];
        for (int y = 0; y < FrameHeight; y++)
            for (int x = 0; x < FrameWidth; x++)
                scaled[y * FrameWidth + x] = source[(y * sourceHeight / FrameHeight) * sourceWidth + x * sourceWidth / FrameWidth];
        return MemoryMarshal.AsBytes(scaled.AsSpan()).ToArray();
    }

    private const int FrameWidth = 400;
    private const int FrameHeight = 225;

    public static void SendReturnToStardew()
    {
        var player = PlayerSnapshot();
        SendSnapshot(player);
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = Sender,
            generation = Generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event",
            @event = "return_stardew",
            payload = new { destination = "stardew:portal", player }
        });
    }

    private static bool StartChallengeIfReady()
    {
        if (string.IsNullOrWhiteSpace(pendingChallengeId))
            return false;
        if (pendingChallengeId == activeChallengeId)
            return true;
        if (!string.IsNullOrEmpty(activeChallengeId) || Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer ||
            !Main.LocalPlayer.active)
            return false;

        Vector2 spawn = Main.LocalPlayer.Bottom + new Vector2(112f, -24f);
        int index = NPC.NewNPC(new EntitySource_Misc("StardewBridgeChallenge"), (int)spawn.X, (int)spawn.Y, NPCID.BlueSlime);
        if (index >= 0 && index < Main.maxNPCs)
        {
            activeChallengeId = pendingChallengeId;
            pendingChallengeId = null;
            challengeNpcIndex = index;
            Main.npc[index].lifeMax = 120;
            Main.npc[index].life = 120;
            Main.npc[index].damage = 18;
            Main.npc[index].defense = 4;
            Main.npc[index].value = Item.buyPrice(silver: 50);
            Main.NewText("Stardew rift challenge: defeat the empowered slime to earn Coal back home.", Color.LightGreen);
            return true;
        }
        else
        {
            challengeNpcIndex = -1;
            Main.NewText("The Stardew rift challenge could not spawn in this world.", Color.OrangeRed);
            return false;
        }
    }

    internal static bool StartTestChallenge(string challengeId)
    {
        if (string.IsNullOrWhiteSpace(challengeId) || Main.gameMenu || Main.netMode != NetmodeID.SinglePlayer ||
            !Main.LocalPlayer.active || !string.IsNullOrEmpty(activeChallengeId))
            return false;

        activeChallengeId = challengeId;
        // Give the test-only agent a native melee weapon so the combat loop can be exercised reliably.
        Main.LocalPlayer.inventory[0].SetDefaults(ItemID.NightsEdge);
        Main.LocalPlayer.selectedItem = 0;
        Vector2 spawn = Main.LocalPlayer.Bottom + new Vector2(112f, -24f);
        int index = NPC.NewNPC(new EntitySource_Misc("StardewBridgeTestChallenge"), (int)spawn.X, (int)spawn.Y, NPCID.BlueSlime);
        if (index < 0 || index >= Main.maxNPCs)
        {
            activeChallengeId = null;
            challengeNpcIndex = -1;
            return false;
        }

        challengeNpcIndex = index;
        Main.npc[index].lifeMax = 120;
        Main.npc[index].life = 120;
        Main.npc[index].damage = 18;
        Main.npc[index].defense = 4;
        Main.npc[index].value = Item.buyPrice(silver: 50);
        Main.NewText("Stardew rift challenge: defeat the empowered slime to earn Coal back home.", Color.LightGreen);
        return true;
    }

    internal static void NotifyNpcKilled(NPC npc)
    {
        if (challengeNpcIndex < 0 || string.IsNullOrEmpty(activeChallengeId) || npc.whoAmI != challengeNpcIndex)
            return;

        string completedId = activeChallengeId;
        challengeNpcIndex = -1;
        activeChallengeId = null;
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = Sender,
            generation = Generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event",
            @event = "terraria_challenge_cleared",
            payload = new { challenge_id = completedId, reward = "stardew:coal", quantity = 5 }
        });
    }

    private static object PlayerSnapshot() => new
    {
        name = Main.LocalPlayer.name,
        life = Main.LocalPlayer.statLife,
        life_max = Main.LocalPlayer.statLifeMax2,
        x = Main.LocalPlayer.position.X,
        y = Main.LocalPlayer.position.Y
    };

    private static void SendSnapshot(object payload)
    {
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = Sender,
            generation = Generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "snapshot",
            payload
        });
    }

    private static void ApplyStardewHealth(JsonElement player)
    {
        if (!player.TryGetProperty("health", out var healthValue) || !healthValue.TryGetInt32(out var health) ||
            !player.TryGetProperty("max_health", out var maxValue) || !maxValue.TryGetInt32(out var maxHealth) ||
            maxHealth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(health / (double)maxHealth, 0d, 1d);
        Main.LocalPlayer.statLife = Math.Clamp((int)Math.Round(ratio * Main.LocalPlayer.statLifeMax2), 1, Main.LocalPlayer.statLifeMax2);
        Main.NewText("Stardew vitality synchronized through the bridge.", Color.LightGreen);
    }

    private static void SendHeartbeat()
    {
        Send(new { protocol = Protocol, version = Version, sender = Sender, generation = Generation, kind = "heartbeat" });
    }

    private static void ConnectLoop()
    {
        while (!stopping)
        {
            try
            {
                client = new TcpClient();
                client.Connect("127.0.0.1", port);
                writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                Send(new { protocol = Protocol, version = Version, sender = Sender, generation = Generation, kind = "hello" });
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                while (!stopping && client.Connected)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;
                    if (IsRemoteControlMessage(line))
                        RemoteControls.Enqueue(line);
                    else
                        Incoming.Enqueue(line);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                if (!stopping)
                    Status.Enqueue($"Terraria bridge unavailable: {ex.Message}");
            }
            finally
            {
                writer = null;
                client?.Close();
                client = null;
            }

            if (!stopping)
                Thread.Sleep(1000);
        }
    }

    private static bool IsRemoteControlMessage(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("kind", out var kind) && kind.GetString() == "event" &&
                root.TryGetProperty("event", out var eventName) && eventName.GetString() == "terraria_control";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Send(object message)
    {
        lock (SendLock)
        {
            if (writer is null) return;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(message));
            }
            catch (IOException)
            {
                // The receive loop will reconnect on the next pass.
            }
        }
    }
}

public sealed class BridgeGlobalNpc : GlobalNPC
{
    public override void OnKill(NPC npc)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            BridgeSystem.NotifyNpcKilled(npc);
    }
}

public sealed class ReturnToStardewCommand : ModCommand
{
    public override string Command => "stardew_return";
    public override CommandType Type => CommandType.Chat;
    public override string Description => "Send a return portal event to Stardew Valley.";
    public override string Usage => "/stardew_return";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        BridgeSystem.SendReturnToStardew();
        caller.Reply("Return event sent to Stardew Valley.", Microsoft.Xna.Framework.Color.LightGreen);
    }
}
