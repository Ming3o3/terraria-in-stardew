# Bridge contract: Stardew Valley ↔ Terraria

Route: live state exchange, Terraria frame compositing inside Stardew, and Stardew keyboard/mouse forwarding. Both game clients run concurrently. Terraria must use tModLoader Host & Play on macOS so its local server keeps the world simulation active while Stardew is foreground.

Host: Stardew Valley 1.6.15.24356, SMAPI 4.5.2, macOS x64.

Guest: Terraria 1.4.5.8 data, tModLoader version to be pinned after installation. The local app bundle contains a native macOS universal launcher and legacy Windows PE32/FNA assemblies; use the native launcher.

## Ownership

| System | Owner | Hand-over trigger | How it comes back |
|---|---|---|---|
| Stardew farm/world | Stardew | never | never |
| Terraria world/terrain/NPC AI | Terraria | portal entry | portal return |
| Player input | Stardew while Terraria view is open; otherwise focused game | F8 opens/closes the Terraria control overlay | F8 returns control to Stardew |
| Camera/UI | Stardew renderer for full-screen and scene overlays; Terraria owns its world camera | F8 opens/closes full-screen view; F7 toggles scene fusion | F8 returns to Stardew |
| Cross-game progress | bridge | every committed event | next handshake resends snapshot |
| Saves | each game | never shared directly | bridge stores only a small mapping file |

## Coordinate policy

The first version has no continuous coordinate mapping. A portal carries a logical destination ID such as `terraria:surface_spawn`; the Terraria world decides the actual spawn tile. A future same-screen bridge must add measured units, axes and camera pose before copying positions.

## Channels

| Name | Kind | Direction | Rate | Full/late policy |
|---|---|---|---|---|
| handshake | JSON request/response | both | once per process start | reject incompatible versions |
| snapshot | JSON object | both | on portal entry/return | latest snapshot wins |
| event | bounded queue | both | discrete | reject after sequence gap; resend snapshot |
| heartbeat | small message | both | 2 Hz | mark peer unavailable after 3 missed beats |

Terraria sends frames over `SDTV_FRAME_PORT`. Full-screen mode remains a compressed 400x225 RGBA image. Scene mode sends a compressed transparent RGBA actor layer with `SDTL` magic, version, JSON dimensions/anchor metadata, player owner and selected-item/ammo/bait metadata. Stardew draws that actor layer at the Stardew farmer's feet, followed by the Terraria hotbar. Stardew movement and collision remain authoritative for the host character. In scene mode, mapped item actions target the Stardew world; the Terraria client supplies item identity and stats, and the Terraria server verifies the selected item plus any chosen ammo or fishing-bait slot before atomically decrementing successful consumables/ammo/bait. F8 full-screen mode retains Terraria-native item actions. Scene control packets include host position, velocity, aim vector, location and pose sequence; Stardew movement keys are left unsuppressed while Terraria receives jump inputs.

## Protocol

Magic: `SDTV-TERRARIA-BRIDGE`

Version: `1`

Transport: localhost TCP, UTF-8 JSON Lines.

Every message contains `protocol`, `sender`, `generation`, `sequence`, and `kind`. A changed `generation` means that one side restarted and must receive a fresh snapshot.

## Lifecycle

- Start: bridge first, then both game plugins.
- Pause/menu/load: the focused game sends `state=paused`; the other side stops accepting gameplay events.
- Portal entry: commit Stardew snapshot, send `enter`, and apply the Stardew health ratio in Terraria.
- Challenge: `enter` includes a unique challenge ID; Terraria spawns one empowered Blue Slime for that ID and reports `terraria_challenge_cleared` on defeat; Stardew grants the mapped Coal reward once and persists the claim in player `modData`.
- Portal return: commit Terraria snapshot, send `return`, and apply the Terraria health ratio in Stardew.
- Crash/disconnect: disable the portal and keep each game's local save intact.
- Uninstall: remove both plugins and the bridge mapping file; no game assets are deleted.

The bridge persists the latest snapshot for each side in `bridge-state.json` (or `SDTV_BRIDGE_STATE`). A reconnecting peer receives the opposite side's latest snapshot before queued events. This is a small cross-game state cache, not a save-file conversion.

## Not covered by version 1

- Shared real-time collision or player physics.
- General inventory transfer remains out of scope; F7 synchronizes only successful mapped consumable/ammo use by slot and item identity.
- Terraria projectile travel, ammo bonuses, arbitrary block layouts, crafting, detailed fishing timing, accessories, buffs and modded item behavior require explicit Stardew mappings. Supported wall items use Stardew fence equivalents.
- Direct save-file conversion.
- Multiplayer or official online services.
