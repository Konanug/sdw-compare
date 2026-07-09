# Contributing

## Getting set up

You need Windows x64, the .NET 8 SDK, and SOLIDWORKS 2024 (its interop assemblies are referenced at
build time). If you plan to touch the assembly-comparison or 3D-viewer code, also install Python
3.10+ with `pyvista`, `vtk`, `build123d`, and `numpy`.

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet restore
dotnet build SolidWorksPartMatcher.sln
dotnet test SolidWorksPartMatcher.sln
```

The unit tests need neither SOLIDWORKS nor Python; tests that exercise the real OpenCASCADE extractor
skip themselves when it is unavailable. Run them before opening a pull request.

## Architecture rules

These are enforced by project references, and breaking them will fail the build:

- `Domain` depends on nothing but the BCL — no COM, WPF, SQLite, or Excel.
- COM objects never leave `SolidWorksPartMatcher.SolidWorks`.
- **All SOLIDWORKS COM access runs on the single dedicated STA thread.** Never call it from
  `Task.Run`, a parallel loop, or a thread-pool thread. Only pure .NET work (hashing, blocking,
  scoring, database reads, workbook preparation) may be parallelised.
- Do not invent SOLIDWORKS API names. Verify every member against the installed interop assemblies
  or the official documentation before using it.

## Matching accuracy

Accuracy matters more than recall, and a wrong merge is worse than a missed one.

- Only byte-identical files and confirmed rigid-body matches may be merged automatically. Everything
  else is flagged for review.
- Never infer that two parts are identical from volume, mass, bounding box, feature count, material,
  or file name alone.
- A failed or unsupported comparison becomes a possible match or an explicit failure — never a match.
- Every conclusion must be explainable from stored evidence, and results must be deterministic.

If a change could weaken matching, say so explicitly in the pull request.

## Style and commits

Formatting is defined by `.editorconfig`; run `dotnet format` before committing. Match the
surrounding code's naming and comment density. Comments should explain constraints the code cannot,
not narrate what the next line does.

Commit subjects use a short type prefix (`feat:`, `fix:`, `docs:`, `chore:`, `refactor:`) and explain
*why* in the body when the reason isn't obvious.

## Adding a fingerprint field

Fingerprints are cached and persisted, so a new field touches several places. Update all of them:
the domain model, the database migration, the extractor, the extractor version, cache invalidation,
scoring and blocking, the Excel export, the test fixtures, and the tests. Bumping the extractor
version is what invalidates stale cached fingerprints.

## Pull requests

Keep changes focused; avoid unrelated refactors. State what you verified, and be explicit about
anything you could not verify locally — SOLIDWORKS behaviour in particular is easy to assume and hard
to check in CI.
