# Terraria in Stardew Valley

This mod adds a Terraria-inspired weapon and a two-process Terraria scene bridge to Stardew Valley.

## Build

Install .NET SDK 8, Stardew Valley and SMAPI, then build from the repository root:

```bash
dotnet build TerrariaInStardew/TerrariaInStardew.csproj -c Release
```

`Pathoschild.Stardew.ModBuildConfig` detects the installed game, provides its assembly references (including xTile), and copies the built mod into the installed SMAPI `Mods` folder. For a custom install, pass `-p:GamePath=/path/to/game`. The mod has been built and loaded in a live SMAPI session.

## Scene interaction

In F7 scene fusion, the Terraria actor and hotbar remain visual overlays, while Stardew Valley executes mapped item actions against the Stardew world. Item metadata is read from the live Terraria player frame. Pickaxes and hammers first call Stardew's native tool action for rocks, ore nodes, crops and resource clumps. If that action changes nothing, they can remove an eligible piece of furniture into the Stardew inventory, then mine one map tile under the cursor, including indoor floors and the Back, Buildings, Front and custom map layers; each removed map tile drops one Stone. Furniture removal follows Stardew's own occupancy/removal checks; items on or inside furniture must be taken out before it can be picked up. Mined map tiles persist in location mod data and are removed again when the save is loaded or the farmer re-enters that location. Tiles with Action, TouchAction, Warp, TreeStump, NoMine or NoMining properties, or a character, farm animal, building, warp, object, resource clump, furniture or terrain feature on the tile are protected. Axes continue to use Stardew's native tree and stump actions. Melee, thrown, ranged, magic, summon, and standalone ammo items make an immediate hit against a Stardew monster under the cursor; consumable thrown and ammo items are consumed only after a successful hit. Healing items restore Stardew health or stamina, armor defense maps to a temporary Stardew defense buff, mapped buff potions apply a Stardew buff, Terraria fishing poles and bait use Stardew's native fish selection on water tiles, and a fishing pole consumes the first available Terraria bait stack after a successful catch. Grappling hooks pull the farmer up to three Stardew tiles toward the cursor within a twelve-tile range, stopping at map collisions; the original hook projectile and latch animation aren't reproduced. Terraria Torch, Work Bench, Chest, Campfire, Furnace, Wood, and Stone items place selected Stardew equivalents. Other tile items map to a Stardew stone floor; wall items map to wood, hardwood, or stone fences. Bombs, sticky/bouncy bombs, Dynamite, and Grenades map to Stardew bombs. Common accessories (boots, wings, balloons, shields, magnets, luck accessories, and emblems) map to short Stardew buffs, other accessory items receive a generic defense buff, and mounts map to a short speed buff. Magic Mirrors, Ice Mirrors, Recall Potions, Return Potions, Cell Phones and Shellphones map to a Stardew warp to the farmer's home. Placeable matching uses Terraria's stable item ID where available, so it works across game languages. Summon weapons map to an immediate cursor-target hit with a Stardew stamina cost; persistent minion behavior is not reproduced. Projectile travel and Terraria ammo-free bonuses aren't reproduced; when a successful ranged hit uses ammo, the Terraria server verifies and consumes the chosen ammo stack. Consumable item use is sent back to Terraria and consumed from the matching hotbar slot by the Terraria server only after Stardew reports a successful action.

F7 hotbar clicks and number keys select Terraria slots. The left mouse button activates a mapped item in Stardew. Hold it to repeat pickaxe, axe, or hammer actions when the selected Terraria item has auto-reuse enabled; repeat timing follows its Terraria use time. Repeated misses are suppressed while the button stays held. Single-use actions such as potions and mirrors still fire once per click. Unsupported Terraria items are intercepted in scene mode and report that no Stardew mapping exists; they do not silently act in Terraria's separate world. The F8 full Terraria view retains native Terraria item behavior.

Target failures and unmapped-item notices use SMAPI translations. In Chinese, tool feedback explains the six-tile reach and reports protected targets. Native tool changes are compared across nearby map layers, furniture, and large terrain features so a successful native hit won't also remove the floor tile underneath.

The mapped effects are adaptations, not Terraria's original tile rules: Stardew owns reach, collision, object placement and saves. Potion life and mana recovery map to Stardew health and stamina. Arbitrary block layouts, crafting, detailed fishing rod timing, accessory passive effects outside the common set, and modded item behavior still need explicit mappings. The cross-game item consumption action is deduplicated in the Terraria player's mod data. Mined-tile persistence and grappling-hook movement have been verified in-game; other mapped categories still need representative target checks.

## Test

After SMAPI loads the mod and a save is active, run this in the SMAPI console:

```text
terraria_weapon
```

The command grants `Meteor Blade`. Its values are intentionally modest and can be tuned after the first real combat pass.
