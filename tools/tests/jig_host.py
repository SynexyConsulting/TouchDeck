"""Builds src/jig_motion.c + jig_paths.c into a DLL with MSVC for the host tests."""
import ctypes
import glob
import os
import subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def vcvars():
    hits = glob.glob(r"C:\Program Files\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat")
    return hits[0] if hits else None


def build(tmp):
    """Returns a ctypes handle, or None when MSVC isn't installed."""
    vc = vcvars()
    if not vc:
        return None
    src = os.path.join(ROOT, "src")
    shim = os.path.join(ROOT, "tools", "tests", "jig_motion_shim.c")
    dll = os.path.join(tmp, "jig.dll")
    bat = os.path.join(tmp, "build.bat")
    with open(bat, "w") as f:
        f.write(f'@call "{vc}" >nul\r\ncd /d "{tmp}"\r\n'
                f'cl /nologo /LD /O2 /I"{src}" "{shim}" "{src}\\jig_motion.c" "{src}\\jig_paths.c" /Fe:"{dll}"\r\n')
    r = subprocess.run(["cmd", "/c", bat], capture_output=True, text=True)
    assert r.returncode == 0, r.stdout + r.stderr
    lib = ctypes.CDLL(dll)
    for fn in ("shim_x", "shim_y"):
        getattr(lib, fn).restype = ctypes.c_float
    for fn in ("shim_step",):
        getattr(lib, fn).argtypes = [ctypes.c_float]
    lib.shim_mouse.argtypes = [ctypes.c_float, ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_float)]
    lib.shim_pick.argtypes = [ctypes.c_int, ctypes.c_uint]
    lib.shim_name.restype = ctypes.c_char
    return lib
