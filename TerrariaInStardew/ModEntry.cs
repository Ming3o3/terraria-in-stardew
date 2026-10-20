using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Weapons;

namespace TerrariaInStardew;

internal sealed class ModEntry : Mod
{
    internal const string WeaponId = "Codex_TerrariaInStardew_MeteorBlade";
    private const string WeaponTexture = "TileSheets/weapons";
    private static ModEntry? instance;
    private BridgeClient? bridge;
    private Harmony? harmony;

    public override void Entry(IModHelper helper)
    {
        StardewItemInteraction.SetTranslations(helper.Translation);
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Input.ButtonReleased += OnButtonReleased;
        helper.Events.Player.Warped += OnWarped;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.ConsoleCommands.Add(
            "terraria_weapon",
            "Give yourself the Terraria in Stardew test weapon.",
            (_, _) => GiveMeteorBlade()
        );
        helper.ConsoleCommands.Add(
            "terraria_enter",
            "Send a portal-entry event to the Terraria process through the local bridge.",
            (_, _) => bridge?.SendPortalEntry()
        );
        helper.ConsoleCommands.Add(
            "terraria_status",
            "Write the current Terraria frame and input bridge diagnostics to the status file.",
            (_, _) => bridge?.WriteDiagnostics()
        );

        bridge = new BridgeClient(Monitor, helper.Input, helper.Translation);
        bridge.Start();
        instance = this;

        var farmerDraw = AccessTools.Method(typeof(Farmer), "draw", new[] { typeof(SpriteBatch) });
        var toolbarType = AccessTools.TypeByName("StardewValley.Menus.Toolbar");
        var toolbarDraw = toolbarType == null ? null : AccessTools.Method(toolbarType, "draw", new[] { typeof(SpriteBatch) });
        if (farmerDraw == null)
        {
            Monitor.Log("Could not find Farmer.draw(SpriteBatch); Stardew's farmer will remain visible in scene fusion.", LogLevel.Error);
        }
        else
        {
            harmony = new Harmony(ModManifest.UniqueID);
            harmony.Patch(farmerDraw, prefix: new HarmonyMethod(typeof(ModEntry), nameof(PrefixFarmerDraw)));
            Monitor.Log("Installed the Farmer.draw scene-fusion patch.", LogLevel.Info);
        }

        if (toolbarDraw == null)
        {
            Monitor.Log("Could not find Toolbar.draw(SpriteBatch); Stardew's toolbar will remain visible in scene fusion.", LogLevel.Error);
        }
        else
        {
            harmony ??= new Harmony(ModManifest.UniqueID);
            harmony.Patch(toolbarDraw, prefix: new HarmonyMethod(typeof(ModEntry), nameof(PrefixToolbarDraw)));
            Monitor.Log("Installed the Toolbar.draw scene-fusion patch.", LogLevel.Info);
        }

        Monitor.Log("Terraria in Stardew loaded. Use 'terraria_weapon' after entering a save.", LogLevel.Info);
        Monitor.Log("Scene hotbar layout: top-left-v1 (16, 16).", LogLevel.Info);
    }

    private static bool PrefixFarmerDraw(Farmer __instance) =>
        !ReferenceEquals(__instance, Game1.player) || instance?.bridge?.SceneFusionEnabled != true;

    private static bool PrefixToolbarDraw() => instance?.bridge?.SceneFusionEnabled != true;

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        bridge?.Pump();
        bridge?.SampleInput();
        if (bridge?.ControlModeEnabled == true)
            bridge.SendRemoteControls();
        if (e.Ticks % 60 == 0)
            bridge?.SendHeartbeat();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        int restored = StardewItemInteraction.RestoreMinedMapTiles(Game1.currentLocation);
        Monitor.Log($"Restored {restored} mined map tile(s) in {Game1.currentLocation.NameOrUniqueName} from the save.", LogLevel.Info);
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (e.IsLocalPlayer)
        {
            int restored = StardewItemInteraction.RestoreMinedMapTiles(e.NewLocation);
            if (restored > 0)
                Monitor.Log($"Restored {restored} mined map tile(s) in {e.NewLocation.NameOrUniqueName}.", LogLevel.Trace);
        }
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == SButton.F8)
        {
            bridge?.ToggleControlMode();
            Helper.Input.Suppress(e.Button);
            return;
        }

        if (e.Button == SButton.F7)
        {
            bridge?.ToggleSceneFusion();
            Helper.Input.Suppress(e.Button);
            return;
        }

        if (bridge?.ControlModeEnabled == true)
        {
            bridge.ButtonPressed(e.Button);
            if (bridge.ShouldSuppressInput(e.Button))
                Helper.Input.Suppress(e.Button);
        }
    }

    private void OnButtonReleased(object? sender, ButtonReleasedEventArgs e)
    {
        if (bridge?.ControlModeEnabled == true)
        {
            bridge.ButtonReleased(e.Button);
            if (bridge.ShouldSuppressInput(e.Button))
                Helper.Input.Suppress(e.Button);
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        bridge?.DrawTerrariaScene(e.SpriteBatch);
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        bridge?.DrawTerrariaView(e.SpriteBatch);
        bridge?.DrawTerrariaSceneHud(e.SpriteBatch);
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Weapons"))
        {
            e.Edit(asset =>
            {
                var weapons = asset.AsDictionary<string, WeaponData>().Data;
                weapons[WeaponId] = new WeaponData
                {
                    Name = "Meteor Blade",
                    DisplayName = "Meteor Blade",
                    Description = "A Stardew-forged blade with Terraria's meteorite spirit.",
                    MinDamage = 18,
                    MaxDamage = 32,
                    Knockback = 1.2f,
                    Speed = 2,
                    Precision = 0,
                    Defense = 2,
                    Type = 3,
                    MineBaseLevel = 40,
                    MineMinLevel = 40,
                    AreaOfEffect = 1,
                    CritChance = 0.08f,
                    CritMultiplier = 3f,
                    CanBeLostOnDeath = false,
                    Texture = WeaponTexture,
                    SpriteIndex = 0,
                    Projectiles = new()
                };
            });
            return;
        }

    }

    private void GiveMeteorBlade()
    {
        if (!Context.IsWorldReady)
        {
            Monitor.Log("Load a save before using terraria_weapon.", LogLevel.Warn);
            return;
        }

        var item = ItemRegistry.Create($"(W){WeaponId}");
        Game1.player.addItemByMenuIfNecessary(item);
        Monitor.Log("Added Meteor Blade to the active player's inventory.", LogLevel.Info);
    }
}
