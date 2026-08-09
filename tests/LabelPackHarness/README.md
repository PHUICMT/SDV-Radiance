# LabelPackHarness

Runs `LabelPacks` and `LabelStore` for real, over a Mods tree it builds in the temp folder, without
starting the game.

```powershell
dotnet run --project tests\LabelPackHarness --nologo -v q
```

Exit code 0 means every check passed. Pass `-p:GamePath=...` if Stardew Valley is not in the default
Steam location.

It exists because label loading is the one part of this mod that decides what the water shader is
told, and it was the one part with no way to check a change short of launching the game and looking.
The two checks that matter are that a pack cannot paint art its own mod does not supply, and that a
player with no packs installed gets byte-identical behaviour to before packs existed.

It compiles `src/LabelPacks.cs` and `src/LabelStore.cs` into itself rather than referencing the built
mod, so it can reach `internal` types without the mod carrying an `InternalsVisibleTo` for the sake
of a test. `IMonitor` is generated with `DispatchProxy` because one of its members takes an
interpolated string handler from an internal namespace, which cannot be named from out here.

What this cannot tell you: whether the log line and the `radiance_report` line read well in a real
session. That needs a real pack in a real mod folder and the game running.

That was done once, on 8 August 2026, with two packs planted in real mod folders: one written by the
labelling tool into the folder of the mod whose art it painted, and one hand-written to paint two
vanilla sheets it does not own. The log said, in order:

```
[WARN  SDV-Radiance] Mods/00_Frameworks/[CP] HxW Tilesheets/radiance-labels.json paints "spring_beach", which is not art that mod supplies. Ignored.
[WARN  SDV-Radiance] Mods/00_Frameworks/[CP] HxW Tilesheets/radiance-labels.json paints "spring_town", which is not art that mod supplies. Ignored.
[INFO  SDV-Radiance] Water labels loaded: 310 sheets, 27229 tiles, including 2 pack(s) from other mods.
```

The bundled labels alone are 307 sheets and 27,220 tiles, so the three sheets and nine tiles the tool
exported arrived and the two sheets the greedy pack asked for did not. Both packs were then removed.

Still not checked in a live session: the `label sources:` line, because it only prints in answer to the
`radiance_report` console command. Its content is asserted here instead.
