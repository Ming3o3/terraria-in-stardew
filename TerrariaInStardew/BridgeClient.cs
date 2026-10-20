using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace TerrariaInStardew;

internal sealed class BridgeClient : IDisposable
{
    private const string Protocol = "SDTV-TERRARIA-BRIDGE";
    private const int Version = 1;
    private static readonly SButton[] RemoteButtons = CreateRemoteButtons();
    private readonly IMonitor monitor;
    private readonly IInputHelper input;
    private readonly ITranslationHelper translations;
    private readonly HashSet<SButton> heldButtons = new();
    private readonly HashSet<SButton> pressedButtons = new();
    private readonly ConcurrentQueue<string> incoming = new();
    private readonly ConcurrentQueue<byte[]> frames = new();
    private readonly object sendLock = new();
    private readonly int port;
    private readonly int framePort;
    private TcpClient? client;
    private StreamWriter? writer;
    private Thread? receiveThread;
    private volatile bool stopping;
    private long sequence;
    private readonly string generation = Guid.NewGuid().ToString("N");
    private JsonElement? pendingTerrariaPlayer;
    private bool applyTerrariaPlayerOnReturn;
    private string? pendingTerrariaReturnEventId;
    private readonly ConcurrentQueue<(string ChallengeId, string EventId)> pendingChallengeRewards = new();
    private TcpListener? frameListener;
    private Thread? frameThread;
    private volatile bool controlModeEnabled;
    private volatile bool sceneFusionEnabled;
    private long lastControlSendTick;
    private long lastSceneToolUseAt;
    private string lastSceneToolUseKey = "";
    private long lastInputEventAt;
    private string lastInputEventButtons = "";
    private readonly Queue<string> recentInputEvents = new();
    private readonly Queue<string> recentControlStates = new();
    private byte[]? latestFrame;
    private byte[]? lastDecodedFrame;
    private Texture2D? terrariaTexture;
    private SceneFrame? latestSceneFrame;
    private int sceneActorAlphaPixels;
    private string sceneActorAlphaBounds = "none";
    private Vector2 lastHostPose;
    private long lastHostPoseTick;
    private const int FrameWidth = 400;
    private const int FrameHeight = 225;
    private const int SceneHotbarMargin = 16;
    private readonly string statusFile = Environment.GetEnvironmentVariable("SDTV_STARDEW_STATUS")
        ?? Path.Combine(Path.GetTempPath(), "sdtv-stardew-bridge-status.json");
    private long receivedFrameCount;
    private long decodedFrameCount;
    private long controlSendCount;
    private long lastFrameReceivedAt;
    private long lastFrameDecodedAt;
    private long lastNonEmptyControlAt;
    private string lastControlSignature = "";
    private long lastStatusWriteAt;
    private bool hasWrittenStatus;
    private string writtenInputSignature = "";
    private long sceneActionSequence;

    public BridgeClient(IMonitor monitor, IInputHelper input, ITranslationHelper translations)
    {
        this.monitor = monitor;
        this.input = input;
        this.translations = translations;
        port = int.TryParse(Environment.GetEnvironmentVariable("SDTV_BRIDGE_PORT"), out var configured) ? configured : 28741;
        framePort = int.TryParse(Environment.GetEnvironmentVariable("SDTV_FRAME_PORT"), out var frameConfigured) ? frameConfigured : 28751;
    }

    public bool Connected => client?.Connected == true;
    public bool ControlModeEnabled => controlModeEnabled;
    public bool SceneFusionEnabled => sceneFusionEnabled;

    public void Start()
    {
        WriteStatus();
        receiveThread = new Thread(ConnectLoop) { IsBackground = true, Name = "TerrariaInStardewBridge" };
        receiveThread.Start();
        frameListener = new TcpListener(IPAddress.Loopback, framePort);
        frameListener.Start();
        frameThread = new Thread(ReceiveFrames) { IsBackground = true, Name = "TerrariaFrameReceiver" };
        frameThread.Start();
    }

    public void Pump()
    {
        while (incoming.TryDequeue(out var line))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                var eventName = root.TryGetProperty("event", out var eventValue) ? eventValue.GetString() : null;
                if (kind == "event" && eventName == "return_stardew" &&
                    root.TryGetProperty("payload", out var payload))
                {
                    var player = payload.TryGetProperty("player", out var eventPlayer)
                        ? eventPlayer
                        : pendingTerrariaPlayer;
                    if (player.HasValue)
                    {
                        pendingTerrariaPlayer = player.Value.Clone();
                        applyTerrariaPlayerOnReturn = true;
                        pendingTerrariaReturnEventId = root.TryGetProperty("event_id", out var returnId) ? returnId.GetString() : null;
                    }
                    else
                        monitor.Log("Terraria return event had no player snapshot.", LogLevel.Warn);
                }
                else if (kind == "event" && eventName == "terraria_challenge_cleared" &&
                         root.TryGetProperty("payload", out var rewardPayload) &&
                         rewardPayload.TryGetProperty("challenge_id", out var challengeId) &&
                         challengeId.ValueKind == JsonValueKind.String)
                {
                    string eventId = root.TryGetProperty("event_id", out var rewardEventId) ? rewardEventId.GetString() ?? "" : "";
                    pendingChallengeRewards.Enqueue((challengeId.GetString()!, eventId));
                }
                else if (kind == "event" && eventName == "scene_item_consumption_result" &&
                         root.TryGetProperty("payload", out var consumptionResult))
                {
                    bool consumed = consumptionResult.TryGetProperty("consumed", out var consumedValue) && consumedValue.GetBoolean();
                    string itemId = ReadString(consumptionResult, "item_id");
                    if (!consumed)
                        monitor.Log($"Terraria could not consume the matching item {itemId} after the Stardew action.", LogLevel.Warn);
                    SendEventReceipt(root.TryGetProperty("event_id", out var resultEventId) ? resultEventId.GetString() : null);
                }
                else if (kind == "peer_snapshot")
                {
                    if (root.TryGetProperty("sender", out var sender) && sender.GetString() == "terraria" &&
                        root.TryGetProperty("payload", out var snapshotPlayer))
                    {
                        pendingTerrariaPlayer = snapshotPlayer.Clone();
                    }
                    monitor.Log("Received a saved Terraria snapshot; it will be applied when a Stardew save is active.", LogLevel.Trace);
                }
                else if (kind == "event_ack" && eventName == "terraria_control")
                {
                    if (root.TryGetProperty("delivered", out var delivered) && !delivered.GetBoolean())
                        monitor.Log("Terraria control message was not delivered because the Terraria mod is disconnected.", LogLevel.Warn);
                }
                else
                {
                    monitor.Log($"Bridge message: {kind ?? "unknown"}{(eventName is null ? "" : $" ({eventName})")}", LogLevel.Trace);
                }
            }
            catch (JsonException)
            {
                monitor.Log("Bridge sent malformed JSON.", LogLevel.Warn);
            }
        }

        if (Context.IsWorldReady && applyTerrariaPlayerOnReturn && pendingTerrariaPlayer.HasValue)
        {
            if (ApplyTerrariaHealth(pendingTerrariaPlayer.Value))
                SendEventReceipt(pendingTerrariaReturnEventId);
            pendingTerrariaPlayer = null;
            applyTerrariaPlayerOnReturn = false;
            pendingTerrariaReturnEventId = null;
            monitor.Log("Applied Terraria vitality at the Stardew return portal.", LogLevel.Info);
        }

        if (Context.IsWorldReady)
        {
            while (pendingChallengeRewards.TryDequeue(out var reward))
            {
                if (GrantChallengeReward(reward.ChallengeId))
                    SendEventReceipt(reward.EventId);
                else
                    pendingChallengeRewards.Enqueue(reward);
                break;
            }
        }
    }

    public void ToggleControlMode()
    {
        if (!Context.IsWorldReady)
        {
            monitor.Log("Load a Stardew save before opening Terraria control mode.", LogLevel.Warn);
            return;
        }

        controlModeEnabled = !controlModeEnabled;
        sceneFusionEnabled = false;
        heldButtons.Clear();
        pressedButtons.Clear();
        if (controlModeEnabled)
        {
            SendPortalEntry();
            SendControl(new { active = true, scene = false });
            monitor.Log("Terraria view opened. WASD moves, Space jumps, mouse attacks, and 1-0 selects hotbar slots. Press F8 to return.", LogLevel.Info);
        }
        else
        {
            SendControl(new { active = false, scene = false });
            SendEvent("return_stardew", new { destination = "stardew:portal", player = PlayerSnapshot() });
            monitor.Log("Returned to Stardew Valley.", LogLevel.Info);
        }
        WriteStatus();
    }

    public void ToggleSceneFusion()
    {
        if (!Context.IsWorldReady)
        {
            monitor.Log("Load a Stardew save before opening scene fusion.", LogLevel.Warn);
            return;
        }

        if (!controlModeEnabled)
        {
            controlModeEnabled = true;
            heldButtons.Clear();
            pressedButtons.Clear();
            SendPortalEntry();
        }

        sceneFusionEnabled = !sceneFusionEnabled;
        lastSceneToolUseKey = "";
        monitor.Log(sceneFusionEnabled
            ? "Scene fusion opened. Stardew remains the world; Terraria's player and items are overlaid. Press F7 for the full Terraria view or F8 to return."
            : "Scene fusion closed. Terraria's full view remains active; press F8 to return to Stardew.", LogLevel.Info);
        WriteStatus();
    }

    public void SendRemoteControls()
    {
        if (!controlModeEnabled || !Context.IsWorldReady)
            return;

        long tick = Game1.ticks;
        if (tick == lastControlSendTick || tick % 2 != 0)
            return;
        lastControlSendTick = tick;

        bool IsDown(SButton button) => heldButtons.Contains(button);
        bool WasPressed(SButton button) => pressedButtons.Contains(button);
        var cursor = input.GetCursorPosition().GetScaledScreenPixels();
        bool left = IsDown(SButton.A);
        bool right = IsDown(SButton.D);
        bool jump = IsDown(SButton.Space) || (!sceneFusionEnabled && IsDown(SButton.W));
        TerrariaItemInfo? selectedItem = HasFreshSceneFrame ? latestSceneFrame?.SelectedItem : null;
        int sceneHotbarSlotUnderCursor = sceneFusionEnabled
            ? GetSceneHotbarSlot((int)cursor.X, (int)cursor.Y)
            : -1;
        int hotbarClickedSlot = sceneFusionEnabled && WasPressed(SButton.MouseLeft)
            ? sceneHotbarSlotUnderCursor
            : -1;
        bool clickedSceneHotbar = hotbarClickedSlot >= 0;
        bool fire = IsDown(SButton.MouseLeft) && !sceneFusionEnabled;
        bool canRepeatTool = selectedItem is not null && selectedItem.AutoReuse &&
            (selectedItem.Pick > 0 || selectedItem.Axe > 0 || selectedItem.Hammer > 0);
        string sceneToolUseKey = selectedItem is null || latestSceneFrame is null
            ? ""
            : $"{latestSceneFrame.SelectedSlot}:{selectedItem.Id}";
        long now = Environment.TickCount64;
        long sceneToolUseInterval = selectedItem is null
            ? 400
            : Math.Max(50, (long)Math.Clamp(selectedItem.UseTime, 1, 120) * 1000 / 60);
        bool repeatSceneToolUse = sceneFusionEnabled && IsDown(SButton.MouseLeft) &&
            !WasPressed(SButton.MouseLeft) && sceneHotbarSlotUnderCursor < 0 && canRepeatTool &&
            sceneToolUseKey == lastSceneToolUseKey && now - lastSceneToolUseAt >= sceneToolUseInterval;
        bool pressedSceneItemUse = sceneFusionEnabled && WasPressed(SButton.MouseLeft) &&
            !clickedSceneHotbar && sceneHotbarSlotUnderCursor < 0;
        if (!sceneFusionEnabled || !IsDown(SButton.MouseLeft) || sceneHotbarSlotUnderCursor >= 0 || !canRepeatTool)
            lastSceneToolUseKey = "";

        if (pressedSceneItemUse || repeatSceneToolUse)
        {
            if (selectedItem is null)
            {
                monitor.Log("Waiting for Terraria item data; no item action was sent.", LogLevel.Trace);
            }
            else if (!StardewItemInteraction.HasMapping(selectedItem))
            {
                string message = translations.Get("scene-item-not-mapped", new { item = selectedItem.Name }).ToString();
                monitor.Log(message, LogLevel.Trace);
                Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            }
            else if ((selectedItem.Consumable || selectedItem.AmmoItem || selectedItem.Thrown ||
                      selectedItem.FishingPole > 0 || selectedItem.Bait > 0 ||
                      (selectedItem.Projectile && !selectedItem.Melee && selectedItem.UsesAmmo)) && !Connected)
            {
                monitor.Log("Terraria is disconnected; this item action needs a server-side inventory update and was not used.", LogLevel.Warn);
            }
            else
            {
                Vector2 worldCursor = new(cursor.X + Game1.viewport.X, cursor.Y + Game1.viewport.Y);
                SceneItemUseResult result = StardewItemInteraction.Use(selectedItem, worldCursor);
                bool repeatedToolUse = repeatSceneToolUse && canRepeatTool;
                if (result.Success || !repeatedToolUse)
                {
                    monitor.Log(result.Message, result.Success ? LogLevel.Info : LogLevel.Trace);
                    Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
                }
                if (canRepeatTool)
                {
                    lastSceneToolUseAt = now;
                    lastSceneToolUseKey = sceneToolUseKey;
                }
                if (result.Success && (result.Consume || result.ConsumeAmmo || result.ConsumeBait))
                {
                    SendEvent("scene_item_consumed", new
                    {
                        action_id = $"{generation}:{Interlocked.Increment(ref sceneActionSequence)}",
                        item_id = selectedItem.Id,
                        slot = latestSceneFrame?.SelectedSlot ?? -1,
                        owner = latestSceneFrame?.Owner ?? -1,
                        consume_item = result.Consume,
                        consume_ammo = result.ConsumeAmmo,
                        ammo_id = result.ConsumeAmmo ? selectedItem.AmmoId : "",
                        ammo_slot = result.ConsumeAmmo ? selectedItem.AmmoSlot : -1,
                        consume_bait = result.ConsumeBait,
                        bait_id = result.ConsumeBait ? selectedItem.FishingBaitId : "",
                        bait_slot = result.ConsumeBait ? selectedItem.FishingBaitSlot : -1
                    });
                }
            }
        }
        int slot = GetSelectedSlot(IsDown);
        int pressedSlot = hotbarClickedSlot >= 0 ? hotbarClickedSlot : GetSelectedSlot(WasPressed);
        bool altFire = IsDown(SButton.MouseRight) && !sceneFusionEnabled;
        bool inventory = IsDown(SButton.E);
        bool map = IsDown(SButton.M);
        var bounds = Game1.player.GetBoundingBox();
        var standing = new Vector2(bounds.Center.X, bounds.Bottom);
        var localStanding = Game1.GlobalToLocal(Game1.viewport, standing);
        var aim = new Vector2(cursor.X - localStanding.X, cursor.Y - localStanding.Y);
        float velocityX = 0, velocityY = 0;
        long tickDelta = Math.Max(1, tick - lastHostPoseTick);
        if (lastHostPoseTick != 0)
        {
            velocityX = (standing.X - lastHostPose.X) / tickDelta * 60f;
            velocityY = (standing.Y - lastHostPose.Y) / tickDelta * 60f;
        }
        lastHostPose = standing;
        lastHostPoseTick = tick;
        foreach (var button in new[] { SButton.W, SButton.A, SButton.S, SButton.D, SButton.E, SButton.M, SButton.Space, SButton.MouseLeft, SButton.MouseRight })
            SuppressUnlessHostMovement(button);
        for (var i = 0; i <= 9; i++)
            input.Suppress((SButton)Enum.Parse(typeof(SButton), $"D{i}"));

        SendControl(new
        {
            active = true,
            left,
            right,
            jump,
            jump_pressed = WasPressed(SButton.Space) || (!sceneFusionEnabled && WasPressed(SButton.W)),
            fire,
            fire_pressed = WasPressed(SButton.MouseLeft) && !sceneFusionEnabled && !clickedSceneHotbar,
            alt_fire = altFire,
            alt_fire_pressed = WasPressed(SButton.MouseRight),
            inventory,
            inventory_pressed = WasPressed(SButton.E),
            map,
            map_pressed = WasPressed(SButton.M),
            slot,
            slot_pressed = pressedSlot,
            mouse_x = cursor.X / Math.Max(1, Game1.uiViewport.Width),
            mouse_y = cursor.Y / Math.Max(1, Game1.uiViewport.Height),
            scene = sceneFusionEnabled,
            host_x = standing.X,
            host_y = standing.Y,
            host_vx = velocityX,
            host_vy = velocityY,
            aim_x = aim.X,
            aim_y = aim.Y,
            pose_sequence = tick,
            host_location = Game1.currentLocation.NameOrUniqueName
        });
        pressedButtons.Clear();
        controlSendCount++;
        bool hasInput = left || right || jump || fire || altFire || inventory || map || slot >= 0;
        string signature = $"{left},{right},{jump},{fire},{altFire},{inventory},{map},{slot}";
        if (hasInput)
            lastNonEmptyControlAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (signature != lastControlSignature)
        {
            lastControlSignature = signature;
            AddRecent(recentControlStates, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{signature}");
            WriteStatus();
        }
    }

    public void SampleInput()
    {
        if (!controlModeEnabled)
            return;

        var keyboardState = Keyboard.GetState();
        var mouseState = Mouse.GetState();
        bool changed = false;
        foreach (var button in RemoteButtons)
        {
            bool isDown = IsPhysicallyDown(button, keyboardState, mouseState);
            bool wasDown = heldButtons.Contains(button);
            if (isDown)
            {
                heldButtons.Add(button);
                if (!wasDown)
                    pressedButtons.Add(button);
            }
            else
            {
                heldButtons.Remove(button);
            }

            changed |= isDown != wasDown;
        }

        if (changed)
            RecordInputEvent($"sampled:{string.Join(',', heldButtons)}");
    }

    private static bool IsPhysicallyDown(SButton button, KeyboardState keyboard, MouseState mouse)
    {
        if (button == SButton.MouseLeft)
            return mouse.LeftButton == ButtonState.Pressed;
        if (button == SButton.MouseRight)
            return mouse.RightButton == ButtonState.Pressed;
        return Enum.TryParse(button.ToString(), true, out Keys key) && keyboard.IsKeyDown(key);
    }

    public void ButtonPressed(SButton button)
    {
        if (controlModeEnabled)
        {
            pressedButtons.Add(button);
            RecordInputEvent($"pressed:{button}");
        }
    }

    public bool ShouldSuppressInput(SButton button) => !sceneFusionEnabled || !IsHostMovementButton(button);

    private static bool IsHostMovementButton(SButton button) =>
        button is SButton.W or SButton.A or SButton.S or SButton.D;

    private void SuppressUnlessHostMovement(SButton button)
    {
        if (ShouldSuppressInput(button))
            input.Suppress(button);
    }

    public void ButtonReleased(SButton button)
    {
        heldButtons.Remove(button);
        if (controlModeEnabled)
            RecordInputEvent($"released:{button}");
    }

    private void RecordInputEvent(string buttons)
    {
        lastInputEventAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lastInputEventButtons = buttons;
        AddRecent(recentInputEvents, $"{lastInputEventAt}:{buttons}");
        WriteStatus();
    }

    private static void AddRecent(Queue<string> history, string value)
    {
        history.Enqueue(value);
        while (history.Count > 24)
            history.Dequeue();
    }

    private static int GetSelectedSlot(Func<SButton, bool> isDown)
    {
        for (var i = 0; i <= 9; i++)
        {
            if (isDown((SButton)Enum.Parse(typeof(SButton), $"D{i}")))
                return i == 0 ? 9 : i - 1;
        }
        return -1;
    }

    private int GetSceneHotbarSlot(int x, int y)
    {
        if (!HasFreshSceneFrame || latestSceneFrame is null)
            return -1;

        int width = Math.Min((int)(latestSceneFrame.Width * 1.4f), (int)(Game1.uiViewport.Width * 0.72f));
        int height = (int)((latestSceneFrame.Height - latestSceneFrame.ActorHeight) * (width / (float)latestSceneFrame.Width));
        int localX = x - SceneHotbarMargin;
        int localY = y - SceneHotbarMargin;
        int itemSlotsWidth = (int)(width * (400f / latestSceneFrame.Width));
        if (localX < 0 || localX >= itemSlotsWidth || localY < 0 || localY >= height)
            return -1;

        int slot = (int)(localX / (itemSlotsWidth / 10f));
        return slot is >= 0 and < 10 ? slot : -1;
    }

    private bool HasFreshSceneFrame => latestSceneFrame is not null &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastFrameDecodedAt < 1000;

    private static SButton[] CreateRemoteButtons()
    {
        var buttons = new List<SButton>
        {
            SButton.W, SButton.A, SButton.S, SButton.D, SButton.E, SButton.M,
            SButton.Space, SButton.MouseLeft, SButton.MouseRight
        };
        for (var i = 0; i <= 9; i++)
            buttons.Add((SButton)Enum.Parse(typeof(SButton), $"D{i}"));
        return buttons.ToArray();
    }

    public void DrawTerrariaView(SpriteBatch spriteBatch)
    {
        // F7 is the scene-fusion view: leave Stardew's world visible and draw
        // only the Terraria actor/projectiles in DrawTerrariaScene. The full
        // Terraria frame belongs exclusively to the F8 diagnostic view.
        if (!controlModeEnabled || sceneFusionEnabled)
            return;

        DrainFrames(spriteBatch.GraphicsDevice);
        if (sceneFusionEnabled)
            return;

        var bounds = new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
        if (terrariaTexture != null && !terrariaTexture.IsDisposed)
            spriteBatch.Draw(terrariaTexture, bounds, Color.White);
        else
            spriteBatch.Draw(Game1.staminaRect, bounds, new Color(12, 16, 20));

        string status = terrariaTexture == null ? "Connecting to Terraria..." : "Terraria | F8: return to Stardew";
        spriteBatch.DrawString(Game1.dialogueFont, status, new Vector2(16, 16), Color.White);
    }

    public void DrawTerrariaScene(SpriteBatch spriteBatch)
    {
        if (!controlModeEnabled || !sceneFusionEnabled)
            return;
        DrainFrames(spriteBatch.GraphicsDevice);
        if (latestSceneFrame == null || terrariaTexture == null || terrariaTexture.IsDisposed)
            return;

        var frame = latestSceneFrame;
        var hostBounds = Game1.player.GetBoundingBox();
        Vector2 foot = Game1.GlobalToLocal(Game1.viewport, new Vector2(hostBounds.Center.X, hostBounds.Bottom));
        const float scale = 2f;
        var actorSource = new Rectangle(0, 0, frame.Width, frame.ActorHeight);
        var actorDestination = new Rectangle(
            (int)(foot.X - frame.AnchorX * scale),
            (int)(foot.Y - frame.AnchorY * scale),
            (int)(frame.Width * scale),
            (int)(frame.ActorHeight * scale));
        spriteBatch.Draw(terrariaTexture, actorDestination, actorSource, Color.White);

    }

    public void DrawTerrariaSceneHud(SpriteBatch spriteBatch)
    {
        if (!controlModeEnabled || !sceneFusionEnabled || latestSceneFrame == null ||
            terrariaTexture == null || terrariaTexture.IsDisposed)
            return;

        var frame = latestSceneFrame;
        if (frame.Height <= frame.ActorHeight)
            return;

        var hotbarSource = new Rectangle(0, frame.ActorHeight, frame.Width, frame.Height - frame.ActorHeight);
        var hotbarWidth = Math.Min((int)(frame.Width * 1.4f), (int)(Game1.uiViewport.Width * 0.72f));
        var hotbarHeight = (int)(hotbarSource.Height * (hotbarWidth / (float)frame.Width));
        var hotbarDestination = new Rectangle(SceneHotbarMargin, SceneHotbarMargin, hotbarWidth, hotbarHeight);
        spriteBatch.Draw(terrariaTexture, hotbarDestination, hotbarSource, Color.White);
    }

    private void DrainFrames(GraphicsDevice graphicsDevice)
    {
        while (frames.TryDequeue(out var frame))
            latestFrame = frame;
        if (latestFrame == null || ReferenceEquals(latestFrame, lastDecodedFrame))
            return;

        var decoded = DecodeFrame(latestFrame);
        if (decoded == null)
            return;
        lastDecodedFrame = latestFrame;
        decodedFrameCount++;
        lastFrameDecodedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EnsureTexture(graphicsDevice, decoded.Width, decoded.Height);
        terrariaTexture!.SetData(decoded.Pixels);
        latestSceneFrame = decoded.Width == FrameWidth && decoded.Height == FrameHeight ? null : decoded;
        MeasureSceneActorAlpha(decoded);
        if (decodedFrameCount == 1 || decodedFrameCount % 30 == 0)
            WriteStatus();
    }

    private void MeasureSceneActorAlpha(SceneFrame frame)
    {
        sceneActorAlphaPixels = 0;
        int minX = frame.Width, minY = frame.ActorHeight, maxX = -1, maxY = -1;
        for (int y = 0; y < frame.ActorHeight; y++)
        {
            int row = y * frame.Width;
            for (int x = 0; x < frame.Width; x++)
            {
                if (frame.Pixels[row + x].A == 0)
                    continue;
                sceneActorAlphaPixels++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        sceneActorAlphaBounds = sceneActorAlphaPixels == 0 ? "empty" : $"{minX},{minY}-{maxX},{maxY}";
    }

    private void EnsureTexture(GraphicsDevice graphicsDevice, int width, int height)
    {
        if (terrariaTexture != null && !terrariaTexture.IsDisposed && terrariaTexture.GraphicsDevice == graphicsDevice &&
            terrariaTexture.Width == width && terrariaTexture.Height == height)
            return;
        terrariaTexture?.Dispose();
        terrariaTexture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color);
    }

    private SceneFrame? DecodeFrame(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            var raw = output.ToArray();
            int width = FrameWidth, height = FrameHeight, actorHeight = FrameHeight, anchorX = 0, anchorY = 0, offset = 0;
            if (raw.Length >= 12 && raw[0] == 83 && raw[1] == 68 && raw[2] == 84 && raw[3] == 76)
            {
                int version = BitConverter.ToInt32(raw, 4);
                int metadataLength = BitConverter.ToInt32(raw, 8);
                if (version != 1 || metadataLength < 2 || metadataLength > raw.Length - 12)
                    return null;
                using var metadata = JsonDocument.Parse(raw.AsMemory(12, metadataLength));
                var root = metadata.RootElement;
                width = root.GetProperty("width").GetInt32();
                height = root.GetProperty("height").GetInt32();
                actorHeight = root.GetProperty("actor_height").GetInt32();
                anchorX = root.GetProperty("anchor_x").GetInt32();
                anchorY = root.GetProperty("anchor_y").GetInt32();
                TerrariaItemInfo? selectedItem = null;
                int selectedSlot = root.TryGetProperty("selected", out var selectedValue) && selectedValue.TryGetInt32(out int parsedSelected)
                    ? parsedSelected
                    : -1;
                int owner = root.TryGetProperty("owner", out var ownerValue) && ownerValue.TryGetInt32(out int parsedOwner)
                    ? parsedOwner
                    : 0;
                if (root.TryGetProperty("selected_item", out var itemValue) && itemValue.ValueKind == JsonValueKind.Object)
                {
                    selectedItem = new TerrariaItemInfo(
                        ReadString(itemValue, "id"),
                        ReadString(itemValue, "name"),
                        ReadInt(itemValue, "stack"),
                        ReadInt(itemValue, "use_time", 24),
                        itemValue.TryGetProperty("auto_reuse", out var autoReuse) && autoReuse.ValueKind == JsonValueKind.True,
                        ReadInt(itemValue, "pick"),
                        ReadInt(itemValue, "axe"),
                        ReadInt(itemValue, "hammer"),
                        ReadInt(itemValue, "defense"),
                        ReadInt(itemValue, "damage"),
                        itemValue.TryGetProperty("melee", out var melee) && melee.ValueKind == JsonValueKind.True,
                        itemValue.TryGetProperty("projectile", out var projectile) && projectile.ValueKind == JsonValueKind.True,
                        itemValue.TryGetProperty("thrown", out var thrown) && thrown.ValueKind == JsonValueKind.True,
                        itemValue.TryGetProperty("magic", out var magic) && magic.ValueKind == JsonValueKind.True,
                        itemValue.TryGetProperty("summon", out var summon) && summon.ValueKind == JsonValueKind.True,
                        itemValue.TryGetProperty("uses_ammo", out var usesAmmo) && usesAmmo.ValueKind == JsonValueKind.True,
                        itemValue.TryGetProperty("ammo_item", out var ammoItem) && ammoItem.ValueKind == JsonValueKind.True,
                        ReadString(itemValue, "ammo_id"),
                        ReadInt(itemValue, "ammo_slot", -1),
                        ReadInt(itemValue, "mana"),
                        ReadInt(itemValue, "heal_life"),
                        ReadInt(itemValue, "heal_mana"),
                        ReadString(itemValue, "buff_id"),
                        ReadInt(itemValue, "buff_time"),
                        ReadInt(itemValue, "create_tile", -1),
                        ReadInt(itemValue, "create_wall", -1),
                        ReadInt(itemValue, "fishing_pole"),
                        ReadInt(itemValue, "bait"),
                        itemValue.TryGetProperty("accessory", out var accessory) && accessory.ValueKind == JsonValueKind.True,
                        ReadInt(itemValue, "mount_type", -1),
                        itemValue.TryGetProperty("consumable", out var consumable) && consumable.ValueKind == JsonValueKind.True,
                        ReadString(itemValue, "fishing_bait_id"),
                        ReadInt(itemValue, "fishing_bait_slot", -1),
                        itemValue.TryGetProperty("grappling_hook", out var grapplingHook) && grapplingHook.ValueKind == JsonValueKind.True
                    );
                }
                offset = 12 + metadataLength;
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || actorHeight <= 0 || actorHeight > height ||
                    anchorX < 0 || anchorX >= width || anchorY < 0 || anchorY > actorHeight ||
                    (long)width * height * 4 != raw.Length - offset)
                    return null;
                var sceneColors = new Color[width * height];
                MemoryMarshal.Cast<byte, Color>(raw.AsSpan(offset)).CopyTo(sceneColors);
                return new SceneFrame(sceneColors, width, height, actorHeight, anchorX, anchorY, selectedItem, selectedSlot, owner);
            }
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || actorHeight <= 0 || actorHeight > height ||
                anchorX < 0 || anchorX >= width || anchorY < 0 || anchorY > actorHeight ||
                (long)width * height * 4 != raw.Length - offset)
                return null;
            var colors = new Color[width * height];
            MemoryMarshal.Cast<byte, Color>(raw.AsSpan(offset)).CopyTo(colors);
            return new SceneFrame(colors, width, height, actorHeight, anchorX, anchorY, null, -1, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            monitor.Log($"Dropped an invalid Terraria frame: {ex.Message}", LogLevel.Trace);
            return null;
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int ReadInt(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out int result) ? result : fallback;

    private void ReceiveFrames()
    {
        while (!stopping)
        {
            try
            {
                using var socket = frameListener!.AcceptTcpClient();
                using var stream = socket.GetStream();
                var header = new byte[4];
                while (!stopping && ReadExactly(stream, header))
                {
                    int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    if (length is <= 0 or > 2_000_000)
                        break;
                    var frame = new byte[length];
                    if (!ReadExactly(stream, frame))
                        break;
                    while (frames.Count > 1)
                        frames.TryDequeue(out _);
                    frames.Enqueue(frame);
                    receivedFrameCount++;
                    lastFrameReceivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            catch (SocketException ex) when (!stopping)
            {
                monitor.Log($"Terraria video stream unavailable: {ex.Message}", LogLevel.Trace);
                Thread.Sleep(1000);
            }
            catch (IOException ex) when (!stopping)
            {
                monitor.Log($"Terraria video stream ended: {ex.Message}", LogLevel.Trace);
            }
            catch (ObjectDisposedException) when (stopping)
            {
                return;
            }
            catch (SocketException) when (stopping)
            {
                return;
            }
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    private void SendControl(object payload)
    {
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = "stardew",
            generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event",
            @event = "terraria_control",
            payload
        });
    }

    public void SendPortalEntry()
    {
        var player = PlayerSnapshot();
        var challengeId = Guid.NewGuid().ToString("N");
        SendSnapshot(player);
        SendEvent("enter_terraria", new { destination = "terraria:surface_spawn", challenge_id = challengeId, player });
    }

    public void SendHeartbeat()
    {
        Send(new { protocol = Protocol, version = Version, sender = "stardew", generation, kind = "heartbeat" });
    }

    private object PlayerSnapshot() => new
    {
        name = Game1.player.Name,
        money = Game1.player.Money,
        health = Game1.player.health,
        max_health = Game1.player.maxHealth,
        x = Game1.player.Position.X,
        y = Game1.player.Position.Y,
        day = Game1.dayOfMonth,
        season = Game1.currentSeason,
        time = Game1.timeOfDay
    };

    private bool ApplyTerrariaHealth(JsonElement player)
    {
        if (!Context.IsWorldReady ||
            !player.TryGetProperty("life", out var lifeValue) || !lifeValue.TryGetInt32(out var life) ||
            !player.TryGetProperty("life_max", out var maxValue) || !maxValue.TryGetInt32(out var maxLife) ||
            maxLife <= 0)
        {
            monitor.Log("Terraria return snapshot has no valid life values; Stardew health was left unchanged.", LogLevel.Warn);
            return false;
        }

        var ratio = Math.Clamp(life / (double)maxLife, 0d, 1d);
        Game1.player.health = Math.Clamp((int)Math.Round(ratio * Game1.player.maxHealth), 1, Game1.player.maxHealth);
        return true;
    }

    private bool GrantChallengeReward(string challengeId)
    {
        if (string.IsNullOrWhiteSpace(challengeId))
            return false;

        string key = $"Codex.TerrariaInStardew.Rewarded.{challengeId}";
        if (Game1.player.modData.ContainsKey(key))
            return true;

        Game1.player.modData[key] = "1";
        var coal = ItemRegistry.Create("(O)382");
        coal.Stack = 5;
        Game1.player.addItemByMenuIfNecessary(coal);
        monitor.Log("Terraria challenge cleared: added 5 Coal to the Stardew save.", LogLevel.Info);
        return true;
    }

    private void SendEventReceipt(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return;
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = "stardew",
            generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event_receipt",
            event_id = eventId
        });
    }

    private void SendEvent(string eventName, object payload)
    {
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = "stardew",
            generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "event",
            @event = eventName,
            payload
        });
    }

    private void SendSnapshot(object payload)
    {
        Send(new
        {
            protocol = Protocol,
            version = Version,
            sender = "stardew",
            generation,
            sequence = Interlocked.Increment(ref sequence),
            kind = "snapshot",
            payload
        });
    }

    private void ConnectLoop()
    {
        while (!stopping)
        {
            try
            {
                client = new TcpClient();
                client.Connect("127.0.0.1", port);
                writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                Send(new { protocol = Protocol, version = Version, sender = "stardew", generation, kind = "hello" });
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                while (!stopping && client.Connected)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;
                    incoming.Enqueue(line);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                monitor.Log($"Bridge unavailable: {ex.Message}", LogLevel.Trace);
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

    private void Send(object message)
    {
        lock (sendLock)
        {
            if (writer is null) return;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(message));
            }
            catch (IOException)
            {
                monitor.Log("Bridge connection closed while sending.", LogLevel.Trace);
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        client?.Close();
        frameListener?.Stop();
        terrariaTexture?.Dispose();
        WriteStatus();
    }

    public void WriteDiagnostics() => WriteStatus();

    private void WriteStatus()
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string inputSignature = $"{string.Join(',', heldButtons.OrderBy(button => button.ToString()))}|{string.Join(',', pressedButtons.OrderBy(button => button.ToString()))}|{lastInputEventAt}";
            if (hasWrittenStatus && now - lastStatusWriteAt < 1000 && lastControlSignature == writtenControlSignature &&
                controlModeEnabled == writtenControlMode && inputSignature == writtenInputSignature)
                return;
            var status = new
            {
                updated_at = now,
                process_id = Environment.ProcessId,
                plugin_build = typeof(BridgeClient).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                scene_hotbar_layout = "top-left-v1",
                scene_hotbar_x = SceneHotbarMargin,
                scene_hotbar_y = SceneHotbarMargin,
                control_mode = controlModeEnabled,
                scene_fusion = sceneFusionEnabled,
                received_frame_count = receivedFrameCount,
                decoded_frame_count = decodedFrameCount,
                last_frame_received_at = lastFrameReceivedAt,
                last_frame_decoded_at = lastFrameDecodedAt,
                control_send_count = controlSendCount,
                last_input_event_at = lastInputEventAt,
                last_input_event_buttons = lastInputEventButtons,
                recent_input_events = recentInputEvents.ToArray(),
                recent_control_states = recentControlStates.ToArray(),
                held_buttons = heldButtons.OrderBy(button => button.ToString()).Select(button => button.ToString()).ToArray(),
                pending_pressed_buttons = pressedButtons.OrderBy(button => button.ToString()).Select(button => button.ToString()).ToArray(),
                last_non_empty_control_at = lastNonEmptyControlAt,
                latest_frame_ready = latestFrame != null,
                latest_scene_frame_ready = latestSceneFrame != null,
                scene_frame_width = latestSceneFrame?.Width ?? 0,
                scene_frame_height = latestSceneFrame?.Height ?? 0,
                scene_actor_alpha_pixels = sceneActorAlphaPixels,
                scene_actor_alpha_bounds = sceneActorAlphaBounds,
                host_player_type = Context.IsWorldReady ? Game1.player.GetType().FullName : null,
                host_visibility_member = "Harmony prefix: StardewValley.Farmer.draw(SpriteBatch)",
                host_visibility_applied = sceneFusionEnabled,
                last_control_signature = lastControlSignature,
                status_file = statusFile
            };
            string temporary = statusFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(status));
            File.Move(temporary, statusFile, true);
            lastStatusWriteAt = now;
            hasWrittenStatus = true;
            writtenControlSignature = lastControlSignature;
            writtenControlMode = controlModeEnabled;
            writtenInputSignature = inputSignature;
        }
        catch (IOException)
        {
            // Diagnostics must never interrupt rendering or input forwarding.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics must never interrupt rendering or input forwarding.
        }
    }

    private string writtenControlSignature = "";
    private bool writtenControlMode;

    private sealed record SceneFrame(
        Color[] Pixels,
        int Width,
        int Height,
        int ActorHeight,
        int AnchorX,
        int AnchorY,
        TerrariaItemInfo? SelectedItem,
        int SelectedSlot,
        int Owner
    );
}
