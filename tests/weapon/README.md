# Weapon client regression tests

Run from PowerShell without opening Unity:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/weapon/run.ps1
```

An alternative installed Roslyn compiler can be supplied with `-CompilerPath`.
The runner compiles the **actual** `Assets/Scripts/WeaponControl.cs` and
`NetworkProtocol.cs`; the weapon algorithm is not duplicated in test code.
Compiler artifacts go into a uniquely named system temporary directory and are
removed after the run. No Unity assets, scene objects, prefabs, or AudioSources
are written or changed.

Coverage includes server-owned counts, one shoot request per accepted local
shot, rejected-shot effects, snapshot readiness, short-R cancel/reload ordering,
the current two-second hold, no client-side online refill, monotonically
displayed action progress, stale action acknowledgements/life generations,
reset and online-to-offline transitions, menu/death input suppression, and
offline shooting/automatic reload/manual reload/resupply.

Held-fire regressions use CityNew's serialized `0.1s` interval: offline shots
and online requests repeat at that cadence, mouse release stops them, and
reload/death interrupt them. Online cases also vary the Inspector interval
and the authoritative `shotInterval` snapshot field independently, preventing
either a stale `0.3s` constant or a local Inspector override from controlling
multiplayer cadence.

## Boundaries

`UnityStubs.cs` substitutes only engine facilities and collaborating components:
input, elapsed time, component lookup, math, projectile/audio/recoil effects, and
the NetworkClient call boundary. Test fixtures invoke the production private
Unity callbacks using reflection. These tests do **not** validate actual TCP
delivery, server processing, Unity component update order, HUD rendering,
physics, or whether another production component independently sends a shot.
Keep the full Unity/C# build and server/in-game integration checks as well.
