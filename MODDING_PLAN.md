# Stardew Valley modding plan

- Install: `Stardew Valley/Contents/MacOS` (Steam macOS build, game data `1.6.15.24356`).
- Engine: .NET 6 / MonoGame DesktopGL, managed x64 assembly.
- Anti-cheat / online: none detected; this plan is for local single-player testing.
- Terraria: `Terraria/Terraria.app/Contents/MacOS`, native universal macOS launcher; data version 1.4.5.8 from `changelog.txt`. The bundle also includes legacy Windows PE32/FNA assemblies.
- Community route: SMAPI 4.5.2, with C# mods built using `Pathoschild.Stardew.ModBuildConfig`.
- Chosen route: two real game processes with a localhost state/event bridge, Terraria-to-Stardew video-frame compositing, and Stardew-to-Terraria input forwarding. Terraria runs through tModLoader Host & Play on macOS so its local server continues world simulation while Stardew is foreground. The single-process Stardew weapon is only an optional content prototype.
- Lab plan: copy the save folder, test with a separate SMAPI profile, and use the SMAPI log as the first runtime oracle.
- Unknowns: tModLoader is not installed in this workspace or the detected Steam library. A local .NET 8 SDK is available in `.tools/dotnet`; the SMAPI/tModLoader integrations still need loader setup and runtime verification.
