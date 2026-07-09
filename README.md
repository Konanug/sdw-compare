# SolidWorks Part Matcher

A Windows desktop application for CAD data hygiene. It does two related but independent jobs:

1. **Duplicate part detection** — scan folders of `.SLDPRT` and STEP (`.step` / `.stp`) files and
   find when *differently named files represent the same physical part*, group them under a
   canonical name, and export an auditable Excel report. Filenames are never treated as identity;
   conclusions are driven by geometry (and, for SLDPRT, metadata).
2. **Assembly comparison** — diff two revisions of the same STEP **assembly** and report what
   changed: parts added / removed, quantity changes, shape / size changes, and position changes
   within the assembly. Results appear in a filterable grid, a 3D drill-down viewer, and an Excel
   report.

The two features share a UI shell but are separate pipelines. **Duplicate detection uses the
SOLIDWORKS COM API; assembly comparison does not** (it uses a bundled OpenCASCADE geometry kernel).

---

## Requirements

### To run a packaged release

| Feature | Requirement |
|---|---|
| Both | Windows 10 / 11 (x64) |
| Both | No .NET install needed — the runtime is bundled in the release build |
| Duplicate detection | SOLIDWORKS 2024, installed and licensed |
| Assembly comparison | None beyond Windows — the OpenCASCADE geometry tools are bundled (no Python install required) |
| 3D drill-down viewer | Bundled (no Python install required) |

> Assembly comparison and the 3D viewer work **without SOLIDWORKS**. SOLIDWORKS is only needed for
> the SLDPRT/STEP duplicate-detection pipeline.

### To build from source

- Windows x64
- .NET 8 SDK
- SOLIDWORKS 2024 installed (its interop assemblies are referenced at build time)
- Python 3.10+ with `pyvista`, `vtk`, `build123d`, `numpy` — **only** needed to (re)build the
  bundled Python tools, or to run the assembly / viewer features in dev mode without bundling.

---

## Running

### From a release build

Unzip the distributed archive and run `SolidWorksPartMatcher.App.exe`. A launcher window lets you
pick **Duplicate Detection** or **Assembly Comparison**.

### From source (development)

In a fresh PowerShell session `dotnet` may not be on `PATH`:

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet restore
dotnet build SolidWorksPartMatcher.sln
dotnet run --project src/SolidWorksPartMatcher.App
```

In dev mode the assembly-comparison and 3D-viewer features shell out to the Python scripts in
`tools/` (`extract_assembly.py`, `compute_component_volume.py`, `view_steps.py`), so Python and the
packages listed above must be on `PATH`. If they are absent, duplicate detection still works; the
assembly features surface a clear "extractor not found" message rather than guessing.

---

## Building a distributable

The release is a self-contained app `.exe` plus a bundled `viewer/` folder containing the Python
geometry tools (compiled to standalone `.exe`s via PyInstaller — end users need no Python).

```powershell
# 1. Build the bundled Python tools once (produces tools/dist/view_steps/).
#    Requires Python + pyvista + build123d on the build machine.
tools\build_viewer.ps1

# 2. Produce the self-contained app + zip.
.\publish.ps1                 # or:  .\publish.ps1 -Version 1.1.0
```

`publish.ps1` emits `publish/SolidWorksPartMatcher-v<version>/` and a matching `.zip`. Distribute
the zip; recipients unzip and run `SolidWorksPartMatcher.App.exe`.

**Single build produces two Python tools.** `build_viewer.ps1` / `view_steps.spec` bundle *both*
`view_steps.exe` (3D viewer) and `compute_component_volume.exe` (accurate assembly volumes) into
one shared folder — they share the OpenCASCADE runtime. If you skip step 1, `publish.ps1` still
produces a working app, but the 3D viewer is unavailable and assembly volumes fall back to a
coarser estimate.

See **[Packaging notes](#packaging-notes)** below for what is and isn't a single file.

---

## How it works (brief)

Duplicate detection runs a staged pipeline — hashing → metadata skim → fingerprint extraction →
candidate blocking → body-coincidence check → detailed comparison (only when needed) → union-find
clustering → Excel export. It never compares every file against every other, and **only exact
matches (identical bytes or confirmed rigid-body coincidence) auto-merge** — uncertain pairs are
flagged for review, never silently merged. Fingerprints are cached by file hash + extractor version,
so repeat scans skip re-opening unchanged parts in SOLIDWORKS.

For STEP parts, the true (OpenCASCADE) volume is measured and combined with orientation-invariant
shape signals — face count, surface-type mix, and a tolerance-aware surface match — in a small
evidence vote. When enough signals agree, the pair is **flagged for review** rather than merged, so
near-identical STEP exports aren't missed just because a radius differs in its last decimal. Every
group can explain itself: the **"Why was this matched?"** item in a group's ⋮ menu shows a
plain-language reason.

Assembly comparison extracts real geometry per component from each STEP file (via the OpenCASCADE
kernel), matches components by name with a geometric fallback for renames, and classifies each as
Unchanged / Modified / Added / Removed. Position changes are detected by composing each occurrence's
global placement through the assembly tree and comparing the two revisions.

**Project layout:** `src/…App` (WPF UI) · `…Domain` (pure models/scoring) · `…Application`
(interfaces, orchestration) · `…SolidWorks` (COM automation) · `…Infrastructure` (SQLite, hashing,
STEP parsing) · `…Excel` (report generation). The Domain project references no COM, WPF, SQLite, or
Excel — all boundaries are enforced by project references.

---

## Tests

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet test SolidWorksPartMatcher.sln
```

Unit tests are pure .NET and require neither SOLIDWORKS nor Python. Tests that exercise the real
OpenCASCADE extractor skip automatically when Python / build123d is unavailable.

---

## Limitations

- **Windows only.** WPF UI, SOLIDWORKS COM, and the packaging scripts are Windows-specific.
- **Duplicate detection needs SOLIDWORKS 2024, licensed.** There is no SOLIDWORKS-free fallback for
  the SLDPRT pipeline.
- **Not a single `.exe`.** The app is one self-contained exe, but the 3D viewer and accurate
  assembly volumes rely on a bundled `viewer/` folder (a full Python/VTK runtime, ~400–600 MB). See
  Packaging notes.
- **Scope (by design):** no drawings, PDM, cloud, automatic file renaming, or AI-driven identity
  decisions. AI (optional) may *suggest* names but never decides whether two parts are the same.
- The assembly position tolerance is a conservative default and may need tuning for a given data
  set.

---

## Packaging notes

The app itself publishes as a **single self-contained `.exe`** (no .NET runtime required on the
target). It is **not** a single all-in-one file, because two capabilities depend on a native
geometry kernel that cannot be merged into the managed exe:

- **3D drill-down viewer** — `viewer/view_steps.exe`
- **Accurate assembly volumes** — `viewer/compute_component_volume.exe` and `extract_assembly`

These are shipped as a sibling `viewer/` folder produced by PyInstaller. The distributable is
therefore *one folder* (or its zip), not one file. This is a deliberate trade-off: bundling a real
CAD kernel (OpenCASCADE) is what makes assembly geometry correct, and it is large.

**Machine portability:** the release has no hardcoded user paths and needs no environment setup on
the target machine — unzip and run. The only build-time assumption is that the build machine has the
.NET SDK (defaults to `C:\Program Files\dotnet`; override in `publish.ps1` if yours differs) and, for
step 1, a working Python + build123d environment.

**If a true single file is required later:** the managed app can already be single-file; the blocker
is the Python/OpenCASCADE runtime. Options would be (a) ship the viewer folder as today, (b) wrap the
whole distributable in an installer that lays down both, or (c) port the geometry extraction to a
native C++/.NET OpenCASCADE binding to eliminate Python. None are needed for the current release.
