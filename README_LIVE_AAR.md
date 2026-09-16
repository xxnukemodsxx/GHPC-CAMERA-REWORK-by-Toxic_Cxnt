# Camera Rework — Live AAR integration build

This branch is an integration/build branch for a single-DLL Camera Rework + Live AAR build.

## Live AAR goals

- Keep Camera Rework as the primary MelonLoader assembly.
- Capture GHPC's own `GHPC.Weapons.LiveRound.ReportShotStory()` output through a postfix so the mod does not replace or alter the game's damage calculation.
- Top-left overlay by default, with top-right/bottom-left/bottom-right options.
- Player-shot filtering, penetration/ricochet status, spall/fragment indicators, damage/module details when exposed by GHPC, shot-story text, history and review mode.
- Companion AAR settings panel when Camera Rework's settings screen is open.
- Session logs in `UserData\CameraRework\AAR\`.
- Reflection-tolerant GHPC binding: missing telemetry should disable only that telemetry source, not the camera mod.

## Hotkeys

- `F6`: show/hide Live AAR.
- `F7`: review mode.

Both are also stored in `UserData\CameraRework\AAR.cfg`.

## Build strategy

The saved Camera Rework 2.29.0 DLL is the primary assembly. `CameraRework.AAR` targets .NET Framework 3.5 and has no compile-time GHPC/Unity/Harmony dependency; it resolves those APIs at runtime. ILRepack merges the helper into the camera assembly, then a Mono.Cecil verifier/injector inserts guarded calls into Camera Rework's existing lifecycle methods.

This branch is deliberately separate from `main` until the DLL is runtime-tested in GHPC.
