# Stardew Valley <-> Terraria bridge

This is the executable coordinator for the two-process route. It listens only on `127.0.0.1`, optionally launches both games, validates a versioned JSON Lines protocol, and relays snapshots/events. The mods also stream Terraria frames into Stardew and forward Stardew controls back to Terraria.

## What "two processes" means here

The bridge coordinates one SMAPI Stardew process and one tModLoader Terraria client process. Both stay alive at the same time; Stardew is the main window, draws the latest Terraria video frame as a full-screen overlay, and forwards keyboard/mouse controls over localhost. In Host & Play mode, tModLoader also starts a local server child process so Terraria's world keeps advancing when its client window loses focus. Use tModLoader's `Multiplayer -> Host & Play` route on macOS; single-player can pause its world while inactive.

This is a two-game-process fusion with same-window video compositing and input forwarding. Terraria still owns its world simulation, collision, AI and save; the displayed image is a compressed 400x225 stream at about 10 FPS rather than native Terraria rendering. The local Host & Play server is an additional child process, not a third game client.

## Prerequisites

Install compatible loaders before trying to launch the real games:

- Stardew Valley must start through SMAPI, and `TerrariaInStardew` must be in the SMAPI `Mods` directory.
- Terraria must start through tModLoader, and `TerrariaBridge` must be built into its `ModSources`/`Mods` workflow.
- The vanilla game executables in this workspace are not enough: they do not load either bridge mod.

The bridge listens on `127.0.0.1:28741` by default. The helper also accepts `28741` as a port alias when `SDTV_BRIDGE_PORT` is changed, which lets a Steam-launched client using the default port join a bridge whose primary port is different. Set `SDTV_BRIDGE_PORT` consistently in all three processes when possible.

## Run with already-running games

Use this when each loader is started from Steam or its own launcher:

```bash
python3 bridge/bridge.py --no-launch
```

Start this command first, then start SMAPI Stardew and tModLoader Terraria. Both mod clients should report a successful bridge connection before the portal commands are used.

## Run and launch both games

The command strings are intentionally explicit because each installation may expose a different native launcher or tModLoader entrypoint. Both commands are required when the bridge launches games; omitting one fails early instead of silently running only one process:

```bash
python3 bridge/bridge.py \
  --stardew-cmd '/absolute/path/to/StardewModdingAPI' \
  --terraria-cmd '/absolute/path/to/tModLoader'
```

The bridge starts both commands and keeps both game processes alive concurrently. If either launched game exits, it ends the bridge session and sends `SIGTERM` to the other launched game. On macOS, use the native launcher supplied by each loader. Each side must connect with protocol version 1.

It also flushes a queued portal event when the second mod finally connects, then performs the corresponding focus handoff. `--stardew-process` and `--terraria-process` set the macOS process names used for focus handoff; adjust them if the loader uses a different name.

For a repeatable launch, set the two exact loader commands and run the repository helper:

```bash
export SDTV_COMMAND='/absolute/path/to/StardewModdingAPI'
export TERRARIA_COMMAND='/absolute/path/to/tModLoader'
./run_dual_bridge.sh
```

Pass bridge options after the helper command. For example, `./run_dual_bridge.sh --no-focus` keeps the operating-system focus where it is instead of moving focus on portal events.

To use a custom primary port while retaining the default-port alias, set:

```bash
export SDTV_BRIDGE_PORT=28744
export SDTV_BRIDGE_PORT_ALIAS=28741
./run_dual_bridge.sh --no-focus
```

For the Steam macOS layout in this workspace, the command values can be the repository launch helpers:

```bash
export SDTV_COMMAND="$PWD/launch_stardew_smapi.sh"
export TERRARIA_COMMAND="$PWD/launch_tmodloader.sh"
./run_dual_bridge.sh
```

`launch_stardew_smapi.sh` executes the installed SMAPI binary. `launch_tmodloader.sh` enters the Steam tModLoader directory and executes its native `ScriptCaller.sh`, which keeps the real tModLoader process attached to the bridge.

The helper refuses to run when either variable is missing. Do not point these variables at the vanilla Stardew or Terraria executables, because those processes do not load the bridge mods.

## First playable slice

1. Start the bridge.
2. Load a Stardew save through SMAPI and a Terraria world through tModLoader.
3. In the SMAPI console run `terraria_enter`.
4. The portal event creates one empowered Blue Slime in the Terraria world. Defeat it to send a challenge-clear event back through the bridge.
5. In Terraria chat run `/stardew_return`.
6. Stardew grants 5 Coal for that challenge ID and stores the claim in the active player's save, so reconnecting or replaying the event won't grant it twice.

The current mods implement frame transport, Stardew-side compositing and input forwarding, health exchange, and the first combat-to-reward loop. The challenge uses Terraria's native Blue Slime AI and a fixed stat increase. They do not copy Terraria world data into Stardew or continuously synchronize player coordinates; Terraria remains authoritative for its own world.

The SMAPI mod writes runtime diagnostics to the OS temporary directory as `sdtv-stardew-bridge-status.json`. Check `control_mode`, `received_frame_count`, `decoded_frame_count`, `control_send_count`, and `last_non_empty_control_at` to distinguish an active frame/input bridge from a connected socket alone.

## Smoke test

Run this against a separate, isolated bridge instance, not the bridge currently connected to your games. The test connects simulated Stardew and Terraria clients and takes over those peer identities. In one terminal, start an isolated instance with a fresh state file:

```bash
python3 bridge/bridge.py --no-launch --no-focus --port 28749 --state-file /tmp/sdtt-protocol-test-state.json
```

In another terminal, run:

```bash
SDTV_BRIDGE_PORT=28749 SDTV_BRIDGE_STATE=/tmp/sdtt-protocol-test-state.json python3 bridge/test_protocol.py
```

It uses two fake clients, so it verifies the transport and routing without launching either game. Keep the test port and state file separate from the active gameplay bridge.
