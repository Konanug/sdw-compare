# PyInstaller spec for the bundled Python tools: the 3D STEP viewer (view_steps.exe) AND the
# real-volume computer (compute_component_volume.exe). Both build into one shared bundle
# (tools/dist/view_steps/) — the volume tool's dependencies (build123d/OCP) are a strict subset
# of the viewer's, so a second bundle would just duplicate ~500 MB.
# Run via:  tools\build_viewer.ps1
#
# console=True keeps each tool's stdout wired so the C# host can read it (the viewer's
# PYVISTA_READY signal; the volume tool's JSON result). C# launches both with
# CreateNoWindow=true, so no terminal window is visible to end users.

import os, site
from PyInstaller.utils.hooks import collect_all, collect_data_files

# ── Locate site-packages ──────────────────────────────────────────────────────
_sp = next(p for p in site.getsitepackages() if os.path.isdir(os.path.join(p, "OCP")))

# ── OCP native DLLs (delvewheel pattern) ─────────────────────────────────────
# OCP/__init__.py adds ../cadquery_ocp_novtk.libs to the DLL search path at
# runtime.  PyInstaller's PE analysis misses these, so we collect them as data
# files, preserving the relative path that OCP's __init__.py expects.
_ocp_libs_dir = os.path.join(_sp, "cadquery_ocp_novtk.libs")
_extra_datas    = []
_extra_binaries = []
if os.path.isdir(_ocp_libs_dir):
    for _f in os.listdir(_ocp_libs_dir):
        _full = os.path.join(_ocp_libs_dir, _f)
        if os.path.isfile(_full):
            _extra_datas.append((_full, "cadquery_ocp_novtk.libs"))

# ── lib3mf.dll (bundled inside the lib3mf Python package) ────────────────────
# lib3mf/__init__.py loads the DLL from its own package directory via
# os.path.dirname(__file__).  Add it as data to keep it co-located with the
# __init__.py in _internal/lib3mf/.
_lib3mf_dll = os.path.join(_sp, "lib3mf", "lib3mf.dll")
if os.path.isfile(_lib3mf_dll):
    _extra_datas.append((_lib3mf_dll, "lib3mf"))

# ── Collect Python packages ───────────────────────────────────────────────────
_d_pv,  _b_pv,  _h_pv  = collect_all("pyvista")
_d_vtk, _b_vtk, _h_vtk = collect_all("vtkmodules")
_d_ocp, _b_ocp, _h_ocp = collect_all("OCP")
_d_b3d, _b_b3d, _h_b3d = collect_all("build123d")
_d_l3m, _b_l3m, _h_l3m = collect_all("lib3mf")
_d_svg, _b_svg, _h_svg = collect_all("ocpsvg")
_d_gor, _b_gor, _h_gor = collect_all("ocp_gordon")
_d_prx, _b_prx, _h_prx = collect_all("cadquery_ocp_proxy")
_d_mpl, _b_mpl, _h_mpl = collect_all("matplotlib")  # pyvista.plotting.colors imports it

a = Analysis(
    ["view_steps.py"],
    pathex=[],
    binaries=_b_pv + _b_vtk + _b_ocp + _b_b3d + _b_l3m + _b_svg + _b_gor + _b_prx + _b_mpl,
    datas=(
        _d_pv + _d_vtk + _d_ocp + _d_b3d + _d_l3m + _d_svg + _d_gor + _d_prx + _d_mpl
        + _extra_datas
    ),
    hiddenimports=(
        _h_pv + _h_vtk + _h_ocp + _h_b3d + _h_l3m + _h_svg + _h_gor + _h_prx + _h_mpl
        + ["vtkmodules.all",
           "vtkmodules.util.misc",
           "vtkmodules.util.numpy_support"]
    ),
    hookspath=[],
    runtime_hooks=[],
    excludes=["tkinter"],   # tkinter not needed; saves ~15 MB
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    noarchive=False,
)

# The volume tool needs only OCP/build123d (no pyvista/vtk), but sharing the single COLLECT
# below de-duplicates everything anyway.
a2 = Analysis(
    ["compute_component_volume.py"],
    pathex=[],
    binaries=_b_ocp + _b_b3d + _b_l3m + _b_svg + _b_gor + _b_prx,
    datas=_d_ocp + _d_b3d + _d_l3m + _d_svg + _d_gor + _d_prx + _extra_datas,
    hiddenimports=_h_ocp + _h_b3d + _h_l3m + _h_svg + _h_gor + _h_prx,
    hookspath=[],
    runtime_hooks=[],
    excludes=["tkinter"],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    noarchive=False,
)

pyz  = PYZ(a.pure)
pyz2 = PYZ(a2.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="view_steps",
    debug=False,
    strip=False,
    upx=False,     # UPX can corrupt VTK DLLs; leave off
    console=True,  # keeps stdout alive so PYVISTA_READY reaches the C# host
    disable_windowed_traceback=False,
    argv_emulation=False,
)

exe2 = EXE(
    pyz2,
    a2.scripts,
    [],
    exclude_binaries=True,
    name="compute_component_volume",
    debug=False,
    strip=False,
    upx=False,
    console=True,  # stdout carries the JSON volume result to the C# host
    disable_windowed_traceback=False,
    argv_emulation=False,
)

coll = COLLECT(
    exe,
    exe2,
    a.binaries,
    a.zipfiles,
    a.datas,
    a2.binaries,
    a2.zipfiles,
    a2.datas,
    strip=False,
    upx=False,
    name="view_steps",
)
