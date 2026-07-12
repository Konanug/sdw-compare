# Changelog

All notable changes to this project are documented here.
Format loosely follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [1.2.2] — 2026-07-11

Duplicate detection. Two fixes to how parts are classified — one of which was presenting a
mirrored part as a confirmed match — plus recognition of engraved STEP parts.

### Fixed

- **A mirrored part could be reported as a confirmed match.** Stage 3.5 (the face-signature
  comparison) overwrote the classification unconditionally, without the guards the later stages
  carry. A face descriptor records a surface's type, axis and radius but **not its position**, and
  the axis direction is deliberately sign-normalised — so a part that is left/right-handed purely
  because of where its holes sit produces an *identical* face signature to its mirror. Stage 3.5
  therefore called it an exact match, and the stages that exist to settle handedness are skipped for
  a mirror, so nothing corrected it. Mirrored pairs are now correctly held as
  `MirrorOrHandedVariant` for review, never auto-confirmed.

- **Engraved SOLIDWORKS parts were being hidden.** The engraving check correctly identified them by
  suppressing the engraving feature and comparing the base geometry — and then Stage 3.5 saw the
  different face count (which *is* the engraving) and overwrote the verdict with "distinct", which
  is filtered out of the results. Engraved SLDPRT pairs are surfaced again.

- **Engraved pairs could be dropped before they were ever compared.** Candidate pairs are bucketed
  by quantised volume, and the volume bucket was an exact match with no tolerance. The bucket is
  100 mm³ wide and a typical text engraving removes 5–100 mm³, so a plain part and its engraved twin
  frequently landed in different buckets and were never compared at all. Neighbouring volume buckets
  are now included.

### Added

- **Engraved STEP parts are now detected.** A STEP file has no feature tree, so the engraving check
  above cannot run on one — and an engraving adds hundreds of tiny faces, which every other check
  reads as "a different part". Engravings are now recognised from the geometry itself: the bounding
  box is unchanged, the volume has barely moved, there are far more faces, the surface area has gone
  *up* (letter walls are new surface), and every one of the plain part's surfaces is still present.
  Such a pair is grouped as an **engraving variant, for review** — never as a confirmed match.

  This requires the OpenCASCADE tool to be present (it ships with the app). Without it, part volume
  and surface area are estimated *from the bounding box*, so two different parts of the same size
  would appear identical on every measurement — the check refuses to run rather than risk a false
  match.

### Changed

- **STEP parts are now measured by OpenCASCADE**, not estimated. Volume, surface area and a tight,
  rotation-invariant bounding box now all come from the CAD kernel — the same change made for
  assembly comparison in 1.2.1, applied to duplicate detection. Part matching gets more accurate as
  a result.

- **STEP edge and vertex counts are now real** (they were previously recorded as zero, which made
  two-thirds of the topology comparison a constant). An identical part exported twice scores exactly
  as before; only parts that genuinely differ now score lower.

### Upgrade note

The first scan after upgrading re-reads every STEP file, because the extra measurements above
invalidate cached STEP fingerprints. This is fast — it does **not** re-open anything in SOLIDWORKS.
SLDPRT fingerprints are unaffected and stay cached.

## [1.2.1] — 2026-07-10

### Fixed

- **Assembly comparison — real component bounding box and surface area (OpenCASCADE).** The
  per-component bounding box and surface area are now measured by OCCT — the same kernel that
  already provides the volume — as a tight, rotation-invariant oriented bounding box, instead of
  the raw STEP point cloud. For embedded assembly components the point cloud scatters construction
  and placement points across the whole assembly, which produced physically impossible boxes (a
  3.7 cm³ part measured ~35 m and a surface area of ~19,000,000 cm²). Bounding box was already
  excluded from the change classification, but it feeds the rename-matching similarity, so this also
  makes matching more reliable.
- **Assembly comparison — no more false renames.** A geometry-fallback rename (two differently-named
  components paired purely by shape) now also requires the parts' actual surfaces to agree
  (orientation-invariant face-signature agreement, robust to rotation). The coarse similarity score
  alone was pairing nowhere-near-identical parts — a flat gasket with an L-bracket — as a "rename".
  Such pairs now correctly report as Removed + Added.
- **Assembly comparison — non-solid parts no longer read as "−100%".** A component with no measurable
  OCCT volume (a non-solid shell/surface in the STEP file) is no longer reported as a
  `Modified, −100%`; it is compared on shape and reported Unchanged when the shell matches, with a
  clear note that the volume was not comparable.
- **Assembly comparison — a 0% volume no longer shows a change tick.** A volume delta that rounds to
  0.00% at the reported precision is treated as no change, so a part is never flagged with a Volume
  change tick while its shown delta reads "0%" (tiny sub-0.005% wobbles between two exports are
  noise, not a revision).

### Changed

- **3D viewer text.** The side-by-side and overlay labels and the overlay legend now use the app's
  Segoe UI font throughout (the legend previously used a mismatched built-in VTK font on a dark
  background box), are smaller and corner-anchored so long names stay inside the viewport, and the
  legend is drawn as coloured, shadowed labels with no background panel.
- Bundled tool: `compute_component_volume.py` gained a `--with-bbox` mode returning the OCCT bounding
  box and surface area alongside the volume. **Rebuild the viewer bundle** (`tools\build_viewer.ps1`)
  when producing a release so `compute_component_volume.exe` understands it.

## [1.2.0] — 2026-07-10

### Changed

- **Renamed to Tytle 3D Model Comparator.** The executable is now `Tytle3DModelComparator.exe`, and
  the window titles, assembly metadata, installer, and packaging scripts follow. The projects and
  namespaces keep their `SolidWorksPartMatcher.*` names, which are internal identifiers only.
- The scan database and logs moved from `%LOCALAPPDATA%\SolidWorksPartMatcher` to
  `%LOCALAPPDATA%\Tytle3DModelComparator`. Existing data is not migrated; a first run after upgrading
  starts with an empty database. The old folder can be deleted.

### Added

- **Windows installer.** `installer\build_installer.ps1` wraps a publish folder into a single
  `Tytle3DModelComparator-Setup-v<version>.exe` (requires Inno Setup 6.3+). It installs per-user with no
  admin prompt, adds a Start-menu shortcut and an uninstaller, and keeps the bundled Python and
  OpenCASCADE runtime out of sight in the install directory. Requires no changes to the application:
  the installed layout matches the zip layout, which is what the tool locator already expects.

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
- **Hole-specification reporting (SOLIDWORKS parts).** When two parts have the same shape but one
  cuts its hole with the **Hole Wizard** and the other with a **plain cut extrude**, this is now
  reported and named per file instead of being silently discarded. Previously such a pair was
  classified `Distinct`, which the UI hides. It is now surfaced for review (never auto-merged, since
  the two are different engineering specifications). When the shapes genuinely differ, the pair still
  stays `Distinct`.
- **Engraving reporting.** Match details now state which part carries engraved text features and how
  many, and which has none.
- **"Why was this matched?"** — every match group's ⋮ menu now opens a popup explaining, in plain
  language, why its parts were grouped (exact copy, mirror image, close-but-review, etc.).

### Changed

- STEP extractor version bumped to `101` / `step-p21-2`, which invalidates cached estimate-based
  STEP fingerprints so they are re-measured with real volume.
- The ⋮ group-actions button is now the rightmost column on every row, with **Open 3D View** moved to
  its left, so the ⋮ buttons align in a single column.
- **"Why was this matched?" and "View Details" are merged into one "Match Details" dialog**: the
  plain-language explanation, then the hole-specification and engraving call-outs (naming each file),
  then the essential per-part values (faces, volume, hole type, engraving), then the raw technical
  evidence. SOLIDWORKS parts keep the existing feature+geometry pipeline — the multi-signal evidence
  vote remains STEP-only.
- Hole-specification conflicts no longer block the body-coincidence check, so the report can state
  whether the holes actually sit in the same positions rather than guessing from coarse metrics.

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
