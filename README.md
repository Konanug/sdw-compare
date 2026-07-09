# SolidWorks Part Matcher

A Windows desktop tool for CAD data hygiene. It answers two questions:

1. **Which of these part files are actually the same part?** Scan folders of `.SLDPRT` and STEP
   files, find parts that are identical despite having different names, group them, and export an
   auditable Excel report.
2. **What changed between two revisions of this assembly?** Compare two STEP assemblies and report
   parts added, removed, re-quantified, reshaped, or moved.

The two features share a UI shell but are independent pipelines. Duplicate detection drives the
SOLIDWORKS COM API; assembly comparison does not touch SOLIDWORKS at all.

## Requirements

To run a packaged release:

| | |
|---|---|
| Both features | Windows 10 or 11, x64. No .NET install needed — the runtime is bundled. |
| Duplicate detection | SOLIDWORKS 2024, installed and licensed. |
| Assembly comparison, 3D viewer | Nothing extra. The OpenCASCADE geometry tools are bundled; no Python install required. |

To build from source you also need the .NET 8 SDK, SOLIDWORKS 2024 (its interop assemblies are
referenced at build time), and — only if you want to rebuild the bundled Python tools — Python 3.10+
with `pyvista`, `vtk`, `build123d`, and `numpy`.

## Running it

Unzip a release and run `SolidWorksPartMatcher.App.exe`. A launcher lets you pick Duplicate Detection
or Assembly Comparison.

From source:

```powershell
# dotnet may not be on PATH in a fresh shell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"

dotnet restore
dotnet build SolidWorksPartMatcher.sln
dotnet run --project src/SolidWorksPartMatcher.App
```

In a source checkout the assembly and 3D-viewer features shell out to the Python scripts under
`tools/`, so Python and the packages above must be on `PATH`. If they aren't, duplicate detection
still works and the assembly features report that the extractor is missing rather than guessing.

## Building a release

A release is one self-contained `.exe` plus a `viewer/` folder holding the bundled Python and
OpenCASCADE tools, so end users install nothing.

```powershell
tools\build_viewer.ps1   # once; needs Python + pyvista + build123d
.\publish.ps1            # or: .\publish.ps1 -Version 1.2.0
```

This writes `publish\SolidWorksPartMatcher-v<version>\` and a matching `.zip`. Distribute the zip.

`build_viewer.ps1` produces **both** bundled tools — `view_steps.exe` (3D viewer) and
`compute_component_volume.exe` (real geometry volumes) — in one PyInstaller bundle, since they share
the same OpenCASCADE runtime. Skipping it still yields a working app, but the 3D viewer is
unavailable and volumes fall back to a coarser estimate.

## How it works

Duplicate detection runs a staged pipeline: hash, skim metadata, extract a geometry fingerprint,
block candidates into buckets, check body coincidence, and only then run a detailed comparison if the
result is still ambiguous. It never compares every file against every other. **Only byte-identical
files and confirmed rigid-body matches are merged automatically** — anything less certain is flagged
for review rather than silently grouped. Fingerprints are cached by file hash and extractor version,
so re-scanning an unchanged folder doesn't reopen anything in SOLIDWORKS.

Two SOLIDWORKS-specific differences get reported rather than swept up:

- A part whose hole was cut with the **Hole Wizard** is not the same as one cut with a plain cut
  extrude, even when the solids coincide. Such a pair is surfaced for review with each file named,
  never merged.
- **Engraved text** is called out per part, so a pair that differs only by an engraving is obvious.

STEP parts have no feature tree, so they're compared on geometry alone. Their true volume is measured
with OpenCASCADE and weighed against face count, surface-type mix, and a tolerance-aware surface
match. When enough of those agree, the pair is flagged for review — so two near-identical STEP exports
aren't missed just because a radius differs in its last decimal.

Every group explains itself: **Match Details** in a group's ⋮ menu shows why the parts were grouped,
what differs, and the values behind the verdict.

Assembly comparison extracts real per-component geometry from each STEP file, matches components by
name with a geometric fallback for renames, and classifies each as unchanged, modified, added, or
removed. Position changes are found by composing each occurrence's placement through the assembly
tree and comparing revisions.

## Project layout

```
src/
  SolidWorksPartMatcher.App             WPF UI
  SolidWorksPartMatcher.Domain          models, scoring, clustering  (no COM/WPF/SQLite)
  SolidWorksPartMatcher.Application     interfaces, orchestration
  SolidWorksPartMatcher.SolidWorks      COM automation
  SolidWorksPartMatcher.Infrastructure  SQLite, hashing, STEP parsing
  SolidWorksPartMatcher.Excel           report generation
tests/                                  unit, integration, golden
tools/                                  Python geometry tools + packaging scripts
```

Project references enforce the boundaries: `Domain` depends on nothing but the BCL.

## Tests

```powershell
dotnet test SolidWorksPartMatcher.sln
```

Unit tests need neither SOLIDWORKS nor Python. Tests that exercise the real OpenCASCADE extractor
skip themselves when it isn't installed.

## Limitations

- Windows only. The UI, the COM automation, and the packaging scripts are all Windows-specific.
- Duplicate detection requires a licensed SOLIDWORKS 2024. There is no fallback for `.SLDPRT`.
- A release is a folder, not a single file. The 3D viewer and accurate volumes need a bundled
  OpenCASCADE runtime (a few hundred MB) that cannot be linked into a managed executable.
- Out of scope by design: drawings, PDM, cloud, automatic file renaming, and AI-driven identity
  decisions. Nothing renames your source files.
- Matching tolerances are conservative defaults and may need tuning for a given data set.

## License

See [LICENSE](LICENSE). Note that SOLIDWORKS interop and the bundled Python/OpenCASCADE runtime carry
their own third-party licenses.
