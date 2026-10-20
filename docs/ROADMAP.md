# Fusion roadmap

## Optional fallback: native Stardew content

1. `Meteor Blade` weapon data and a grant command.
2. A Terraria-style projectile weapon using Stardew's `WeaponProjectile` data.
3. A custom monster implemented as a Stardew `Monster` plus a Harmony/SMAPI spawn hook.
4. A mine-floor event that spawns the guest monster and uses Stardew's own collision, drops and save system.

## Phase 2: original presentation

- Replace the placeholder weapon sprite with an original pixel sprite.
- Add localized names and descriptions.
- Add sound and visual effects generated or authored for this mod.

## Phase 1: two-process bridge

Run Stardew and Terraria separately and exchange a versioned localhost event protocol. Portal entry/return and health snapshots are implemented. A portal entry now spawns an empowered Terraria Blue Slime; defeating it sends a challenge-clear event that grants 5 Coal in Stardew, recorded in the active save by challenge ID. F7 sends selected Terraria item metadata to Stardew, where native tool actions, map-tile mining, monster hits, healing, fishing, selected buffs, home warps, grappling-hook pulls, bombs and selected placeable equivalents run against the Stardew world; successful consumables, fishing bait, and selected ammo are decremented from verified Terraria slots. Mined tiles persist through save reload and location re-entry. Terraria tile walls map to Stardew fence equivalents. Remaining work: detailed fishing timing, accessory effects beyond generic mappings, crafting semantics, and modded items; verify each mapped category in an active game; then continue with boss completion and time/weather mapping. See `docs/BRIDGE_PLAN.md` and `docs/CONTRACT.md`.

## Phase 2: shared progress and world events

- Expand selected-item action mappings and persist cross-game item mappings in bridge-owned state with generation and sequence checks.
- Add game-thread commands for loading/pausing, portal states, item rewards, boss and time events.
- Keep each game's save authoritative; test on separate copied saves/worlds.

## Phase 3: optional same-screen rendering

Only after the event bridge is stable, investigate frame compositing or native geometry transfer. This is platform-specific and much more fragile than the window-handoff route.
