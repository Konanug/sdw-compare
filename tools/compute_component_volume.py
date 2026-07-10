"""
Minimal batch volume computation for assembly-comparison components.
Bundled via PyInstaller (see tools/build_viewer.ps1); dev fallback is `python` on PATH.
Launched by OcctVolumeRefiner (C#) to refine the heuristic 55%-of-bounding-box volume estimate
that StepGeometryEstimator.EstimateVolume falls back to for non-cylinder shapes.

Deliberately does NOT parse assembly structure, placement, or instance identity — it only knows
how to load a single-part STEP snippet (already sliced out by the existing
StepComponentSnippetWriter, unmodified) and report that one part's real volume. This is the
whole point: no XCAF walk, no assembly tree, nothing that could regress instance detection.

Usage:
  compute_component_volume.py <manifest.json>
    manifest.json: {"paths": ["<snippet1.step>", "<snippet2.step>", ...]}

Output (stdout, one JSON document): {"<path>": <volumeM3 or null>, ...}
A null value means that specific snippet failed to parse/compute — the C# caller falls back to
the existing heuristic volume for that one component. A per-file try/except means one bad
snippet never takes down the whole batch.
"""

import sys, io, os

def _bootstrap_streams() -> None:
    for fd, name in ((1, "stdout"), (2, "stderr")):
        if getattr(sys, name) is None:
            try:
                setattr(sys, name,
                        io.TextIOWrapper(io.FileIO(fd, "w"),
                                         encoding="utf-8", line_buffering=True))
            except OSError:
                setattr(sys, name, open(os.devnull, "w"))

_bootstrap_streams()

import json

MM3_TO_M3 = 1e-9  # OCCT normalizes to mm on import regardless of the file's authored unit.
MM2_TO_M2 = 1e-6  # OCCT surface areas come out in mm².
MM_TO_M = 1e-3    # OCCT lengths (bounding-box sizes) come out in mm.


def log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def compute_one(path: str, with_bbox: bool = False):
    from build123d import import_step
    from OCP.BRepGProp import BRepGProp
    from OCP.GProp import GProp_GProps

    # Deliberately NOT build123d's high-level Shape.volume: found empirically (real Test6 data,
    # CE26209H01) that it silently returned 0.0 for a specific component's snippet — which had a
    # real, plausible volume close to its previous revision (2945.65 mm³ vs. A's 2885.78 mm³, an
    # ordinary small change) but got reported as a nonsensical "-100% volume, vanished" delta.
    # The failing snippet happened to resolve as a bare TopoDS_Shell rather than a TopoDS_Solid;
    # build123d's Shape.volume handles that case by first calling ShapeFix_Solid.SolidFromShell()
    # and computing mass on the result, and that repair step appears to fail silently for
    # whatever specific (not fully root-caused) defect this one shell had. Note this is NOT a
    # blanket "any Shell breaks Shape.volume" rule — a clean synthetic Shell (no defect) computed
    # correctly through Shape.volume in testing. Calling OCCT's BRepGProp.VolumeProperties_s
    # directly on the raw shape sidesteps the repair step entirely and integrates the boundary
    # faces directly (the divergence theorem needs a closed boundary, not a TopoDS_Solid wrapper)
    # — confirmed to give the exact right answer both for known-volume fixtures (box, cylinder)
    # and for the real CE26209H01 snippets on both sides, with no observed regression.
    shape = import_step(path).wrapped
    props = GProp_GProps()
    BRepGProp.VolumeProperties_s(shape, props)
    volume_m3 = props.Mass() * MM3_TO_M3

    # Default output stays a bare volume for backward compatibility (StepPartVolumeRefiner and any
    # older caller/bundle). Only when the caller passes --with-bbox do we return the richer object.
    if not with_bbox:
        return volume_m3

    result = {"volume_m3": volume_m3, "area_m2": None, "bbox_m": None}

    # Real surface area (mm² → m²). Best-effort: a failure here must not lose the volume.
    try:
        aprops = GProp_GProps()
        BRepGProp.SurfaceProperties_s(shape, aprops)
        result["area_m2"] = aprops.Mass() * MM2_TO_M2
    except Exception as ex:
        log(f"Surface area failed for '{path}': {ex!r}")

    # Tight, rotation-invariant oriented bounding box (OBB). The raw P21 point cloud is unusable for
    # embedded assembly components — construction points (LINE/placement origins) are scattered
    # across the whole assembly, giving a physically impossible box (a 3.7 cm³ part measured ~35 m).
    # The box must therefore come from the same CAD kernel that gives the volume. Sorted ascending,
    # in metres. Best-effort, same as area.
    try:
        from OCP.Bnd import Bnd_OBB
        from OCP.BRepBndLib import BRepBndLib
        obb = Bnd_OBB()
        BRepBndLib.AddOBB_s(shape, obb)
        result["bbox_m"] = sorted(
            2.0 * half * MM_TO_M for half in (obb.XHSize(), obb.YHSize(), obb.ZHSize()))
    except Exception as ex:
        log(f"Bounding box failed for '{path}': {ex!r}")

    return result


def main(argv: list[str]) -> int:
    with_bbox = "--with-bbox" in argv
    args = [a for a in argv if a != "--with-bbox"]
    if len(args) != 1:
        log("Usage: compute_component_volume.py [--with-bbox] <manifest.json>")
        return 2

    with open(args[0], "r", encoding="utf-8") as f:
        manifest = json.load(f)
    paths = manifest["paths"]

    results: dict[str, object] = {}
    for path in paths:
        try:
            results[path] = compute_one(path, with_bbox)
        except Exception as ex:
            log(f"Failed to compute volume for '{path}': {ex!r}")
            results[path] = None

    ok = sum(1 for v in results.values() if v is not None)
    log(f"Computed {ok}/{len(paths)} components ({'with bbox' if with_bbox else 'volume only'}).")
    json.dump(results, sys.stdout)
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
