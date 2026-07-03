"""
STEP file viewer / comparator.
Bundled via PyInstaller (--console, hidden by C# CreateNoWindow).
Launched by StepDiffWindow.xaml.cs; prints PYVISTA_READY to stdout when
the 3D window is about to open, so the C# loading spinner can be dismissed.
"""

# ── Stream bootstrap ──────────────────────────────────────────────────────────
# In PyInstaller --noconsole builds sys.stdout/stderr are None.  If our C# host
# has redirected the pipe (for PYVISTA_READY signalling), fd 1/2 are valid;
# otherwise fall back to devnull so prints never crash.
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

# ── Imports ───────────────────────────────────────────────────────────────────
import vtk
import pyvista
import build123d

# Must match Palette[] in StepDiffWindow.xaml.cs
PALETTE = [
    (1.000, 0.251, 0.506),  # hot-pink   #FF4081
    (0.000, 0.898, 0.463),  # green      #00E576
    (1.000, 0.427, 0.000),  # orange     #FF6D00
    (0.251, 0.769, 1.000),  # light-blue #40C4FF
    (0.412, 0.941, 0.682),  # teal       #69F0AE
    (1.000, 0.843, 0.251),  # amber      #FFD740
]

pyvista.global_theme.line_width = 3
pyvista.global_theme.point_size = 8
pyvista.global_theme.window_size = (1500, 1000)

# Same font as the rest of the app (App.xaml's global FontFamily="Segoe UI") — only add_text
# supports a custom font FILE (add_legend only takes a built-in family name like 'arial', so the
# overlay/whole-assembly legend keeps the closest built-in font). Falls back to None (VTK's
# default) if this isn't a Windows box with the font installed, rather than failing to render.
SEGOE_UI_PATH = r"C:\Windows\Fonts\segoeui.ttf"
FONT_FILE = SEGOE_UI_PATH if os.path.exists(SEGOE_UI_PATH) else None


# ── Custom interactor style ───────────────────────────────────────────────────

class SWInteractorStyle(vtk.vtkInteractorStyleTrackballCamera):
    """
    Scroll-wheel press-and-drag  →  rotate  (SolidWorks-style).
    Left click, right click      →  no action.
    Scroll wheel (roll)          →  zoom in / out  (unchanged default).
    """

    # Disable left-button rotation
    def OnLeftButtonDown(self):  pass
    def OnLeftButtonUp(self):    pass

    # Disable right-button dolly/rubber-band
    def OnRightButtonDown(self): pass
    def OnRightButtonUp(self):   pass

    # Middle button → rotate (instead of the default pan)
    def OnMiddleButtonDown(self):
        iren = self.GetInteractor()
        self.FindPokedRenderer(*iren.GetEventPosition())
        self.StartRotate()

    def OnMiddleButtonUp(self):
        self.EndRotate()


def _apply_style(pl: pyvista.Plotter) -> None:
    """Set our custom interactor style, trying different pyvista version paths."""
    style = SWInteractorStyle()
    for _set in (
        # pyvista 0.39-0.45 — direct forward via __getattr__
        lambda: pl.iren.SetInteractorStyle(style),
        # pyvista 0.46+ — underlying VTK object via private attribute
        lambda: pl.iren._iren.SetInteractorStyle(style),
        # all versions — go through the render window directly
        lambda: pl.ren_win.GetInteractor().SetInteractorStyle(style),
    ):
        try:
            _set()
            return
        except AttributeError:
            continue
    print("Warning: could not apply custom interactor style", flush=True)


# ── STEP loading helpers ──────────────────────────────────────────────────────

def load(path: str) -> pyvista.PolyData:
    step = build123d.importers.import_step(path)
    points, faces = step.tessellate(tolerance=0.1)
    points = [tuple(p) for p in points]
    print(f"  {len(points)} points, {len(faces)} faces", flush=True)
    return pyvista.PolyData.from_regular_faces(points, faces)


def centroid_align(mesh: pyvista.PolyData) -> pyvista.PolyData:
    c = mesh.center
    return mesh.translate([-c[0], -c[1], -c[2]])


# ── Main ──────────────────────────────────────────────────────────────────────

if len(sys.argv) < 2:
    print("Usage: view_steps.py [--native-align] [--side-by-side] <file1.step> [file2.step ...]", flush=True)
    sys.exit(1)

# --native-align: skip per-file centroid re-centering and overlay files in their own shared
# coordinate system instead. Centroid-align (default) is right when files have no assumed
# common origin (e.g. two unrelated parts). It's wrong for two versions of the SAME assembly:
# they already share a consistent native coordinate system, and re-centering each to its own
# bounding-box center discards that alignment and replaces it with an artificial one based on
# each version's own overall bounding box — which shifts independently as parts are added,
# removed, or resized between versions, producing a misleading relative offset that isn't
# present in the original coordinates.
#
# --side-by-side: render each file in its own viewport (divided by a visible border) instead of
# overlaying them in one — there's no reliable landmark-based (hole-to-hole, edge-to-edge)
# automatic alignment available from tessellated STEP geometry alone, so instead of guessing an
# overlay position each part is shown true-to-itself, transparent, at its own true scale.
# Deliberately does NOT call pl.link_views(): each viewport keeps its own independent camera, so
# rotating/panning/zooming while the cursor is over one part's viewport only affects that
# viewport (VTK's vtkRenderWindowInteractor already routes each mouse event to whichever
# viewport the cursor is over via FindPokedRenderer — this is standard multi-viewport behavior,
# not something this file has to implement; link_views() would be the thing that broke it).
args = sys.argv[1:]
native_align  = "--native-align" in args
side_by_side  = "--side-by-side" in args
paths = [a for a in args if a not in ("--native-align", "--side-by-side")]

if len(paths) == 1:
    name = os.path.basename(paths[0])
    print(f"\nLoading {name} ...", flush=True)
    mesh = load(paths[0])
    pl = pyvista.Plotter(title=name)
    pl.add_mesh(mesh, color=(0.70, 0.80, 1.0))
    _apply_style(pl)
    print("PYVISTA_READY", flush=True)
    pl.show()

elif side_by_side and len(paths) == 2:
    names = [os.path.basename(p) for p in paths]
    title = "3D Part Comparison (side by side) — " + ", ".join(names)
    pl = pyvista.Plotter(title=title, shape=(1, 2), border=True, border_color="black", border_width=2)

    meshes = []
    for i, path in enumerate(paths):
        name = os.path.basename(path)
        print(f"\nLoading {name} ...", flush=True)
        mesh = load(path)
        mesh = centroid_align(mesh)  # each viewport is independent — center each shape in its own
        meshes.append(mesh)

        pl.subplot(0, i)
        pl.set_background("white")
        pl.add_mesh(mesh, color=PALETTE[i % len(PALETTE)], opacity=0.75)
        pl.add_text(name, position="upper_edge", font_size=10, color="black", font_file=FONT_FILE)
        # Small XYZ triad, one per viewport, that tracks each viewport's own camera as the user
        # orbits it — lets the user compare orientation between the two parts even though each
        # viewport's camera moves independently.
        pl.add_axes()
        _apply_style(pl)

    # Same reference frame size for both viewports (the larger of the two parts' bounding-box
    # diagonals) rather than each viewport auto-fitting to its own object independently — the
    # default auto-fit behavior would make a genuinely smaller part LOOK the same size as a
    # bigger one, defeating true relative-scale comparison.
    reference_radius = max(m.length for m in meshes) / 2.0
    matched_bounds = (-reference_radius, reference_radius) * 3

    def reset_matched_zoom(*_args) -> None:
        for i in range(len(meshes)):
            pl.subplot(0, i)
            pl.renderer.reset_camera(bounds=matched_bounds)
            # Without this, the next render/screenshot pass re-triggers VTK's automatic
            # per-object camera fit and silently discards the matched bounds set above —
            # camera_set=True marks this camera as deliberately configured already.
            pl.renderer.camera_set = True
        pl.render()

    reset_matched_zoom()  # apply as the initial view too, not just on demand

    # add_checkbox_button_widget is the only genuinely clickable on-screen widget pyvista
    # exposes without custom VTK work — used here purely as a momentary trigger (the on/off
    # state itself is irrelevant, only the click matters), labeled via adjacent add_text.
    pl.subplot(0, 0)
    pl.add_checkbox_button_widget(
        reset_matched_zoom, value=False, position=(10, 10), size=30,
        color_on="#1F4E79", color_off="#1F4E79")
    pl.add_text("Reset Zoom", position=(46, 14), font_size=9,
                color="black", font_file=FONT_FILE)

    # No pl.link_views() here — each viewport's camera stays independent, so dragging/rotating
    # over one part's viewport never affects the other.
    print("PYVISTA_READY", flush=True)
    pl.show()

else:
    names = [os.path.basename(p) for p in paths]
    title = "3D Part Comparison — " + ", ".join(names)
    pl = pyvista.Plotter(title=title)

    for i, path in enumerate(paths):
        name = os.path.basename(path)
        print(f"\nLoading {name} ...", flush=True)
        mesh = load(path)
        if not native_align:
            mesh = centroid_align(mesh)
        color = PALETTE[i % len(PALETTE)]
        pl.add_mesh(mesh, color=color, opacity=0.75, label=name)

    pl.add_legend(bcolor=(0.15, 0.15, 0.15), border=True)
    _apply_style(pl)
    print("PYVISTA_READY", flush=True)
    pl.show()
