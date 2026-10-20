using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameContent.UI.States;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerrariaBridge;

public sealed class FusionTestAgent : ModSystem
{
    private sealed record Request(string Json, TaskCompletionSource<string> Reply);
    private static readonly ConcurrentQueue<Request> Requests = new();
    private static readonly List<(string Label, Action Click)> Options = new();
    private static readonly FieldInfo? ClickField = typeof(UIElement).GetField("OnLeftClick", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? DownField = typeof(UIElement).GetField("OnLeftMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
    private static long loopTicks;
    private static TcpListener? listener;
    private static bool enabled;

    public override void Load()
    {
        int port = 0;
        enabled = !Main.dedServ && int.TryParse(Environment.GetEnvironmentVariable("SDTV_TEST_AGENT_PORT"), out port) && port is > 0 and < 65536;
        if (!enabled)
            return;

        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "TerrariaFusionTestAgent" }.Start();
        On_Main.DoUpdate += AfterUpdate;
        Mod.Logger.Info($"Fusion test agent listening on 127.0.0.1:{port}");
    }

    public override void Unload()
    {
        On_Main.DoUpdate -= AfterUpdate;
        listener?.Stop();
        listener = null;
        enabled = false;
    }

    private static void AcceptLoop()
    {
        while (listener != null)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { return; }
            client.NoDelay = true;
            _ = Task.Run(() => Serve(client));
        }
    }

    private static void Serve(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
        using (var writer = new System.IO.StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Requests.Enqueue(new Request(line, reply));
                try { writer.WriteLine(reply.Task.GetAwaiter().GetResult()); }
                catch (System.IO.IOException) { return; }
            }
        }
    }

    private static void AfterUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
    {
        orig(self, ref gameTime);
        loopTicks++;
        while (Requests.TryDequeue(out var request))
        {
            try
            {
                using var document = JsonDocument.Parse(request.Json);
                request.Reply.TrySetResult(Handle(document.RootElement));
            }
            catch (Exception ex)
            {
                request.Reply.TrySetResult(JsonSerializer.Serialize(new { error = ex.GetType().Name + ": " + ex.Message }));
            }
        }
    }

    private static string Handle(JsonElement request)
    {
        string command = request.GetProperty("cmd").GetString() ?? "";
        switch (command)
        {
            case "observe":
                return JsonSerializer.Serialize(Observe());
            case "click":
            {
                int id = request.GetProperty("id").GetInt32();
                if (id < 0 || id >= Options.Count)
                    return JsonSerializer.Serialize(new { error = "invalid option id; observe first" });
                var (label, click) = Options[id];
                click();
                return JsonSerializer.Serialize(new { ok = true, clicked = label });
            }
            case "type":
            {
                if (Main.MenuUI.CurrentState is not UIVirtualKeyboard keyboard)
                    return JsonSerializer.Serialize(new { error = "no active text field" });
                keyboard.Text = request.GetProperty("text").GetString();
                return JsonSerializer.Serialize(new { ok = true, text = keyboard.Text });
            }
            case "control":
                FusionTestAgentState.Move = request.TryGetProperty("move", out var direction) ? Math.Clamp(direction.GetInt32(), -1, 1) : 0;
                FusionTestAgentState.Jump = request.TryGetProperty("jump", out var jumpValue) && jumpValue.GetBoolean();
                FusionTestAgentState.Fire = request.TryGetProperty("fire", out var fireValue) && fireValue.GetBoolean();
                FusionTestAgentState.Aim = request.TryGetProperty("aim", out var aimValue) ? (float)aimValue.GetDouble() : FusionTestAgentState.Aim;
                FusionTestAgentState.Frames = Math.Clamp(request.TryGetProperty("frames", out var frameValue) ? frameValue.GetInt32() : 1, 1, 3600);
                if (request.TryGetProperty("slot", out var slotValue))
                {
                    FusionTestAgentState.Slot = slotValue.GetInt32();
                    FusionTestAgentState.UseFrames = FusionTestAgentState.Frames;
                }
                return "{\"ok\":true}";
            case "spawn_challenge":
                if (!request.TryGetProperty("challenge_id", out var challengeValue) ||
                    challengeValue.ValueKind != JsonValueKind.String)
                    return JsonSerializer.Serialize(new { error = "challenge_id is required" });
                return JsonSerializer.Serialize(new
                {
                    ok = BridgeSystem.StartTestChallenge(challengeValue.GetString() ?? "")
                });
            default:
                return JsonSerializer.Serialize(new { error = "unknown command" });
        }
    }

    private static object Observe()
    {
        Options.Clear();
        if (!Main.gameMenu)
        {
            var player = Main.LocalPlayer;
            var enemies = Main.npc.Take(Main.maxNPCs).Where(n => n.active && !n.friendly && !n.townNPC && n.lifeMax > 5)
                .OrderBy(n => Vector2.DistanceSquared(n.Center, player.Center)).Take(20)
                .Select(n => new { name = n.TypeName, type = n.type, x = n.position.X, y = n.position.Y, life = n.life, life_max = n.lifeMax }).ToArray();
            return new
            {
                screen = "world",
                world = Main.worldName,
                frame = Main.GameUpdateCount,
                loop_tick = loopTicks,
                inactive = ReadInactiveFlag(),
                can_update_gameplay = Main.CanUpdateGameplay,
                paused = ReadBoolField("gamePaused"),
                auto_pause = ReadBoolField("autoPause"),
                inventory = ReadBoolField("playerInventory"),
                options_window = ReadBoolField("ingameOptionsWindow"),
                active = Main.instance.IsActive,
                game_menu = ReadBoolField("gameMenu"),
                net_mode = ReadIntField("netMode"),
                has_focus = ReadBoolField("hasFocus"),
                inactive_sleep_ms = Main.instance.InactiveSleepTime.TotalMilliseconds,
                remote_controls = new
                {
                    enabled = FusionTestAgentState.RemoteEnabled,
                    left = FusionTestAgentState.RemoteLeft,
                    right = FusionTestAgentState.RemoteRight,
                    jump = FusionTestAgentState.RemoteJump,
                    jump_pressed = FusionTestAgentState.RemoteJumpPressedUntil >= Main.GameUpdateCount,
                    fire = FusionTestAgentState.RemoteFire,
                    fire_pressed = FusionTestAgentState.RemoteFirePressedUntil >= Main.GameUpdateCount,
                    alt_fire = FusionTestAgentState.RemoteAltFire,
                    alt_fire_pressed = FusionTestAgentState.RemoteAltFirePressedUntil >= Main.GameUpdateCount,
                    inventory = FusionTestAgentState.RemoteInventory,
                    inventory_pressed = FusionTestAgentState.RemoteInventoryPressedUntil >= Main.GameUpdateCount,
                    map = FusionTestAgentState.RemoteMap,
                    slot = FusionTestAgentState.RemoteSlot,
                    last_update_ms_ago = Environment.TickCount64 - FusionTestAgentState.RemoteLastUpdate
                },
                player_controls = new
                {
                    player.controlLeft,
                    player.controlRight,
                    player.controlJump,
                    player.controlUseItem,
                    player.controlUseTile,
                    player.controlInv,
                    player.selectedItem,
                    map_fullscreen = Main.mapFullscreen,
                    control_apply_calls = FusionTestAgentState.ControlHookCalls,
                    last_control_apply_frame = FusionTestAgentState.LastControlHookFrame,
                    last_remote_right_seen_by_hook = FusionTestAgentState.LastHookRemoteRight,
                    last_control_right_written_by_hook = FusionTestAgentState.LastHookControlRight,
                    last_remote_fire_seen_by_hook = FusionTestAgentState.LastHookRemoteFire,
                    last_control_use_item_written_by_hook = FusionTestAgentState.LastHookControlUseItem
                },
                player = new { x = player.position.X, y = player.position.Y, life = player.statLife, life_max = player.statLifeMax2, selected = player.selectedItem, dead = player.dead },
                enemies
            };
        }

        if (Main.menuMode == 0)
        {
            Options.Add(("Single Player", () =>
            {
                typeof(Main).GetMethod("ClearPendingPlayerSelectCallbacks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
                Main.menuMode = 1;
                typeof(Main).GetMethod("PrepareLoadedModsAndConfigsForSingleplayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
            }));
            return new { screen = "title", options = Labels() };
        }

        var state = Main.MenuUI.CurrentState;
        if (Main.menuMode == 888 && state != null)
        {
            Collect(state);
            return new { screen = state.GetType().Name, text = state is UIVirtualKeyboard keyboard ? keyboard.Text : null, options = Labels() };
        }
        return new { screen = "busy", menu_mode = Main.menuMode, status = Main.statusText };
    }

    private static bool? ReadInactiveFlag()
    {
        object? value = typeof(Main).GetField("gameInactive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        return value is bool inactive ? inactive : null;
    }

    private static bool? ReadBoolField(string name)
    {
        object? value = typeof(Main).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        return value is bool flag ? flag : null;
    }

    private static int? ReadIntField(string name)
    {
        object? value = typeof(Main).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        return value is int number ? number : null;
    }

    private static object[] Labels() => Options.Select((item, id) => (object)new { id, label = item.Label }).ToArray();

    private static void Collect(UIElement element)
    {
        if (ClickField?.GetValue(element) != null || DownField?.GetValue(element) != null)
        {
            string label = Label(element);
            if (!string.IsNullOrWhiteSpace(label) && label != "UIHoverImage")
            {
                var target = element;
                Options.Add((label, () =>
                {
                    var evt = new UIMouseEvent(target, target.GetDimensions().Center());
                    target.LeftMouseDown(evt);
                    target.LeftMouseUp(evt);
                    target.LeftClick(evt);
                }));
            }
        }
        foreach (var child in element.Children.ToArray())
            Collect(child);
    }

    private static string Label(UIElement element)
    {
        string? label = element switch
        {
            UITextPanel<LocalizedText> text => text.Text,
            UITextPanel<string> text => text.Text,
            UITextPanel<object> text => text.Text?.ToString(),
            UIText text => text.Text,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(label))
            return label;
        var parent = element.Parent;
        var labelField = parent?.GetType().GetField("_buttonLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        if (labelField?.GetValue(parent) is UIText buttonLabel)
        {
            var evt = new UIMouseEvent(element, element.GetDimensions().Center());
            element.MouseOver(evt);
            string? hoveredLabel = buttonLabel.Text;
            element.MouseOut(evt);
            if (!string.IsNullOrWhiteSpace(hoveredLabel))
            {
                var worldData = parent?.GetType().GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(parent);
                if (worldData != null && worldData.GetType().Name.Contains("WorldFileData", StringComparison.Ordinal))
                {
                    var name = worldData.GetType().GetProperty("Name")?.GetValue(worldData)?.ToString();
                    return string.IsNullOrWhiteSpace(name) ? hoveredLabel : $"{hoveredLabel}: world \"{name}\"";
                }
                return hoveredLabel;
            }
        }
        var title = element.GetType().GetField("_title", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(element) as UIText;
        if (title != null && !string.IsNullOrWhiteSpace(title.Text))
            return title.Text;
        var textChild = element.Children.OfType<UIText>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
        return textChild?.Text ?? element.GetType().Name;
    }
}

public sealed class FusionTestAgentPlayer : ModPlayer
{
    public override void PreUpdateBuffs()
    {
        if ((!FusionTestAgentEnabled && !FusionTestAgentState.RemoteEnabled) || Player.whoAmI != Main.myPlayer || Main.gameMenu)
            return;
        bool remote = FusionTestAgentState.RemoteEnabled;
        FusionTestAgentState.ControlHookCalls++;
        FusionTestAgentState.LastControlHookFrame = Main.GameUpdateCount;
        FusionTestAgentState.LastHookRemoteRight = remote && FusionTestAgentState.RemoteRight;
        FusionTestAgentState.LastHookRemoteFire = remote && (FusionTestAgentState.RemoteFire || FusionTestAgentState.RemoteFirePressedUntil >= Main.GameUpdateCount);
        Player.controlLeft = remote ? FusionTestAgentState.RemoteLeft : FusionTestAgentMove < 0;
        Player.controlRight = remote ? FusionTestAgentState.RemoteRight : FusionTestAgentMove > 0;
        Player.controlJump = remote
            ? FusionTestAgentState.RemoteJump || FusionTestAgentState.RemoteJumpPressedUntil >= Main.GameUpdateCount
            : FusionTestAgentJump;
        Player.controlUseItem = (remote
            ? FusionTestAgentState.RemoteFire || FusionTestAgentState.RemoteFirePressedUntil >= Main.GameUpdateCount
            : FusionTestAgentFire) || FusionTestAgentUseFrames > 0;
        Player.controlUseTile = remote && (FusionTestAgentState.RemoteAltFire || FusionTestAgentState.RemoteAltFirePressedUntil >= Main.GameUpdateCount);
        Player.controlInv = remote && (FusionTestAgentState.RemoteInventory || FusionTestAgentState.RemoteInventoryPressedUntil >= Main.GameUpdateCount);
        int slot = remote ? FusionTestAgentState.RemoteSlot : FusionTestAgentSlot;
        if (remote && slot >= 0 && slot <= 9)
            Player.selectedItem = slot;
        if (FusionTestAgentUseFrames > 0)
        {
            Player.selectedItem = FusionTestAgentSlot;
            FusionTestAgentUseFrames--;
        }
        if (remote)
        {
            Main.mouseX = (int)(Math.Clamp(FusionTestAgentState.RemoteMouseX, 0f, 1f) * Main.screenWidth);
            Main.mouseY = (int)(Math.Clamp(FusionTestAgentState.RemoteMouseY, 0f, 1f) * Main.screenHeight);
            Main.mouseLeft = FusionTestAgentState.RemoteFire || FusionTestAgentState.RemoteFirePressedUntil >= Main.GameUpdateCount;
            Main.mouseRight = FusionTestAgentState.RemoteAltFire || FusionTestAgentState.RemoteAltFirePressedUntil >= Main.GameUpdateCount;
        }
        else
        {
            var cursor = Player.Center + new Vector2(MathF.Cos(FusionTestAgentAim), MathF.Sin(FusionTestAgentAim)) * 200 - Main.screenPosition;
            Main.mouseX = (int)cursor.X;
            Main.mouseY = (int)cursor.Y;
        }
        if (FusionTestAgentFrames > 0 && --FusionTestAgentFrames == 0)
        {
            FusionTestAgentMove = 0;
            FusionTestAgentJump = FusionTestAgentFire = false;
        }
        if (!remote)
        {
            Main.mouseLeft = FusionTestAgentFire;
            Main.mouseRight = false;
        }
        FusionTestAgentState.LastHookControlRight = Player.controlRight;
        FusionTestAgentState.LastHookControlUseItem = Player.controlUseItem;
    }

    public override bool PreItemCheck()
    {
        if (FusionTestAgentEnabled)
            Player.delayUseItem = false;
        return true;
    }

    private static bool FusionTestAgentEnabled => Environment.GetEnvironmentVariable("SDTV_TEST_AGENT_PORT") != null;
    private static int FusionTestAgentMove { get => FusionTestAgentState.Move; set => FusionTestAgentState.Move = value; }
    private static bool FusionTestAgentJump { get => FusionTestAgentState.Jump; set => FusionTestAgentState.Jump = value; }
    private static bool FusionTestAgentFire { get => FusionTestAgentState.Fire; set => FusionTestAgentState.Fire = value; }
    private static int FusionTestAgentFrames { get => FusionTestAgentState.Frames; set => FusionTestAgentState.Frames = value; }
    private static float FusionTestAgentAim { get => FusionTestAgentState.Aim; set => FusionTestAgentState.Aim = value; }
    private static int FusionTestAgentSlot { get => FusionTestAgentState.Slot; set => FusionTestAgentState.Slot = value; }
    private static int FusionTestAgentUseFrames { get => FusionTestAgentState.UseFrames; set => FusionTestAgentState.UseFrames = value; }
}

internal static class FusionTestAgentState
{
    internal static int Move, Frames, Slot, UseFrames;
    internal static bool Jump, Fire;
    internal static float Aim;
    internal static bool RemoteEnabled, RemoteLeft, RemoteRight, RemoteJump, RemoteFire, RemoteAltFire, RemoteInventory, RemoteMap;
    internal static int RemoteSlot = -1;
    internal static long RemoteJumpPressedUntil, RemoteFirePressedUntil, RemoteAltFirePressedUntil, RemoteInventoryPressedUntil;
    internal static long ControlHookCalls, LastControlHookFrame;
    internal static bool LastHookRemoteRight, LastHookControlRight, LastHookRemoteFire, LastHookControlUseItem;
    internal static float RemoteMouseX = 0.5f, RemoteMouseY = 0.5f;
    internal static long RemoteLastUpdate;
}
