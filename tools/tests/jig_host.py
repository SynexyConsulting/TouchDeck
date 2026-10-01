"""Builds the firmware's C into a shared library for the host tests: MSVC on Windows,
the system C compiler (`cc`, or $CC) on macOS and Linux."""
import ctypes
import glob
import os
import shlex
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
WINDOWS = sys.platform == "win32"
NO_COMPILER = "MSVC not installed" if WINDOWS else "no C compiler (cc / $CC)"


def vcvars():
    hits = glob.glob(r"C:\Program Files\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat")
    return hits[0] if hits else None


def cc():
    """The POSIX compiler command as an argv list, or None."""
    cmd = shlex.split(os.environ.get("CC") or "cc")
    return cmd if cmd and shutil.which(cmd[0]) else None


def compiler_available():
    return bool(vcvars()) if WINDOWS else cc() is not None


def lib_ext():
    return "dll" if WINDOWS else ("dylib" if sys.platform == "darwin" else "so")


def build_shared(tmp, name, sources, include_dirs=(), defines=(), msvc_exports=()):
    """Compiles sources into tmp/<name>.<dll|dylib|so> and returns its path, or None when
    there's no compiler. msvc_exports: functions to export with /EXPORT (sources without
    __declspec(dllexport)); on POSIX every non-static function is exported anyway."""
    out = os.path.join(tmp, f"{name}.{lib_ext()}")
    if WINDOWS:
        vc = vcvars()
        if not vc:
            return None
        inc = " ".join(f'/I"{d}"' for d in include_dirs)
        dfs = " ".join(f"/D{d}" for d in defines)
        src = " ".join(f'"{s}"' for s in sources)
        link = (" /link " + " ".join(f"/EXPORT:{e}" for e in msvc_exports)) if msvc_exports else ""
        bat = os.path.join(tmp, f"build_{name}.bat")
        with open(bat, "w") as f:
            f.write(f'@call "{vc}" >nul\r\ncd /d "{tmp}"\r\n'
                    f'cl /nologo /LD /O2 {inc} {dfs} {src} /Fe:"{out}"{link}\r\n')
        r = subprocess.run(["cmd", "/c", bat], capture_output=True, text=True)
    else:
        c = cc()
        if not c:
            return None
        shared = ["-dynamiclib"] if sys.platform == "darwin" else ["-shared"]
        # -ffp-contract=off: clang fuses a*b+c into one FMA by default on arm64, which rounds
        # differently from MSVC and the boards (see hostui/build.sh).
        args = c + ["-std=c11", "-O2", "-fPIC", "-ffp-contract=off"] + shared
        args += [f"-I{d}" for d in include_dirs] + [f"-D{d}" for d in defines]
        args += ["-o", out] + list(sources) + ["-lm"]
        r = subprocess.run(args, capture_output=True, text=True, cwd=tmp)
    assert r.returncode == 0, r.stdout + r.stderr
    return out


def build(tmp):
    """src/jig_motion.c + jig_paths.c as a ctypes handle, or None when there's no compiler."""
    src = os.path.join(ROOT, "src")
    shim = os.path.join(ROOT, "tools", "tests", "jig_motion_shim.c")
    path = build_shared(tmp, "jig", [shim, os.path.join(src, "jig_motion.c"), os.path.join(src, "jig_paths.c")], [src])
    if not path:
        return None
    lib = ctypes.CDLL(path)
    for fn in ("shim_x", "shim_y"):
        getattr(lib, fn).restype = ctypes.c_float
    for fn in ("shim_step",):
        getattr(lib, fn).argtypes = [ctypes.c_float]
    lib.shim_mouse.argtypes = [ctypes.c_float, ctypes.POINTER(ctypes.c_float), ctypes.POINTER(ctypes.c_float)]
    lib.shim_pick.argtypes = [ctypes.c_int, ctypes.c_uint]
    lib.shim_name.restype = ctypes.c_char
    return lib
