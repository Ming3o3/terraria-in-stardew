# Terraria-side bridge mod

Copy `TerrariaBridge/` into the tModLoader `ModSources` directory and build it from Workshop → Develop Mods. It connects to the Python bridge at `127.0.0.1:28741`.

With the Steam tModLoader install, the source is at `~/Library/Application Support/Terraria/tModLoader/ModSources/TerrariaBridge` and the generated `TerrariaBridge.tmod` is written to that tModLoader profile's `Mods` directory. The repository build can also target the installed loader directly:

```bash
dotnet build TerrariaBridge/TerrariaBridge.csproj -c Release \
  -p:TmlInstallDir="$HOME/Library/Application Support/Steam/steamapps/common/tModLoader"
```

The `/stardew_return` chat command sends the return event used by the window-handoff slice. When an `enter_terraria` event carries a challenge ID, the mod spawns one empowered native Blue Slime near the player; defeating it sends `terraria_challenge_cleared` with the ID and the mapped Stardew reward. The mod applies Stardew's health ratio when the Terraria world is ready, and sends Terraria's health snapshot back on return. It uses Terraria's native world, AI and textures rather than copying assets into the project.

Set `SDTV_BRIDGE_PORT` before launching both games if a different port is needed.
