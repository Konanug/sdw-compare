# Changelog

All notable changes to this project are documented here.
Format loosely follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [1.1.0] — 2026-07-09

### Added

- **STEP geometric-evidence vote.** STEP parts are now measured with the real OpenCASCADE volume and
  compared using orientation-invariant signals — volume (within 5%), face count, surface-type mix,
  and a tolerance-aware surface-signature match. When at least 3 of the 4 agree, the pair is
  **flagged for review** (never auto-merged), so near-identical STEP exports are no longer missed
  because a radius differs in its last decimal. Thresholds are configurable via `StepMatchTolerances`.
- **Real STEP volume.** `StepPartVolumeRefiner` batches STEP part files through the bundled OCCT tool
  to replace the previous bounding-box volume estimate. Falls back to the estimate when the tool is
  unavailable — no regression.
- **"Why was this matched?"** — every match group's ⋮ menu now opens a popup explaining, in plain
  language, why its parts were grouped (exact copy, mirror image, close-but-review, etc.).

### Changed

- STEP extractor version bumped to `101` / `step-p21-2`, which invalidates cached estimate-based
  STEP fingerprints so they are re-measured with real volume.
- The ⋮ group-actions button is now the rightmost column on every row, with **Open 3D View** moved to
  its left, so the ⋮ buttons align in a single column.

## [1.0.0] — 2026-07-09

### Added

- **Duplicate part detection** across `.SLDPRT` and STEP files: SHA-256 hashing, fingerprint
  extraction, candidate blocking, body-coincidence checking, union-find clustering, and an auditable
  Excel report. Only byte-identical or confirmed rigid-body matches auto-merge.
- **STEP assembly comparison**: added/removed parts, quantity changes, real (OCCT) volume changes,
  and position changes, with a 3D drill-down viewer and an Excel report mirroring the in-app grid.
- **Assembly position tracking** via globally-composed occurrence placements and within-tolerance
  bipartite matching (a coarse per-product boolean, deliberately not per-instance).
- Self-contained Windows release: single app `.exe` plus a bundled `viewer/` folder (Python/OCCT
  runtime) — no .NET or Python install required on the target machine.

### Fixed

- **Fingerprint cache restored.** `GetFingerprintAsync` was a stub, so every scan re-opened every
  part in SOLIDWORKS. Repeat scans now reuse geometry keyed on SHA-256 + configuration + extractor
  version (with a lookup index), which also removes a source of run-to-run variance.
- **Mirror-detection consistency.** Mirror features are now recognised by any feature type whose name
  contains "mirror" (e.g. `MirrorPattern`), not just a fixed three-name set.
- **Deterministic candidate generation.** The bucket blocker iterated an unordered dictionary; it now
  iterates ordinal-sorted and sorts results by pair id (same pairs, stable ordering).
- **Transient COM failures** in the SOLIDWORKS comparison stages are retried once before falling back
  to the coarse classification, reducing "match present, then missing" between scans.
- Folder list: folders can be removed (and **Remove All**) after a scan, results are invalidated when
  the folder set changes, and the same folder may be added more than once.
