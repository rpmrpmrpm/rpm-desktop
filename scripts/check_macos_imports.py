"""
Checks that every Mach-O file in an app bundle or folder only needs what an older macOS has.

The minimum macOS stamped into a binary (minos) says what it was built for, not what it needs. What
stops a binary on an older macOS is a system library it links that is not there, or a function,
variable or Objective-C class it imports that the library does not export yet. This reads both from
each file (otool -L, nm -u -m) and looks them up in the .tbd export lists of that macOS's SDK.

Objective-C messages are not imports, so a method newer than the SDK is not found here; only classes
are. Weak imports are listed apart: they are allowed to be missing, and the code has to check them.

  python3 -I scripts/check_macos_imports.py --sdk MacOSX12.3.sdk --arch x86_64 path/to/rpm.app
"""
import argparse
import re
import subprocess
import sys
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("--sdk", required=True, type=Path)
p.add_argument("--arch", required=True, choices=["x86_64", "arm64"])
p.add_argument("paths", nargs="+", type=Path)
a = p.parse_args()

TARGET = f"{a.arch}-macos"


def tbd_documents(text):
    return [doc for doc in re.split(r"^--- ", text, flags=re.M) if "install-name:" in doc]


def bracket_lists(section):
    """Every `key: [ a, b, ... ]` in a block, with lists that run over several lines."""
    for found in re.finditer(r"([a-z-]+):\s*\[(.*?)\]", section, flags=re.S):
        yield found[1], [item.strip().strip("'\"") for item in found[2].replace("\n", " ").split(",") if item.strip()]


def load_sdk(sdk):
    """Install names and exported symbols for the architecture."""
    names, symbols = set(), set()
    for tbd in list(sdk.glob("usr/lib/**/*.tbd")) + list(sdk.glob("System/Library/**/*.tbd")):
        for doc in tbd_documents(tbd.read_text(encoding="utf-8", errors="replace")):
            name = re.search(r"install-name:\s*'?([^'\n]+)'?", doc)[1].strip()
            names.add(name)
            for block in re.split(r"^\s*- (?=targets:|archs:)", doc, flags=re.M)[1:]:
                lists = dict(bracket_lists(block))
                targets = lists.get("targets") or [f"{arch}-macos" for arch in lists.get("archs", [])]
                if TARGET not in targets:
                    continue
                for key, items in lists.items():
                    if key in ("symbols", "weak-symbols", "thread-local-symbols"):
                        symbols.update(items)
                    elif key == "objc-classes":
                        for c in items:
                            symbols.update({f"_OBJC_CLASS_$_{c}", f"_OBJC_METACLASS_$_{c}"})
                    elif key == "objc-eh-types":
                        symbols.update(f"_OBJC_EHTYPE_$_{c}" for c in items)
                    elif key == "objc-ivars":
                        symbols.update(f"_OBJC_IVAR_$_{c}" for c in items)
    return names, symbols


def macho_files(paths):
    for path in paths:
        for f in [path] if path.is_file() else sorted(x for x in path.rglob("*") if x.is_file() and not x.is_symlink()):
            with open(f, "rb") as head:
                magic = head.read(4)
            if magic in (b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe"):
                yield f


def run(*args):
    out = subprocess.run(args, capture_output=True, text=True)
    return out.stdout if out.returncode == 0 else ""


names, exported = load_sdk(a.sdk)
print(f"{a.sdk.name}: {len(names)} libraries, {len(exported)} symbols for {TARGET}")
bad = checked = 0
for f in macho_files(a.paths):
    if a.arch not in run("lipo", "-archs", str(f)).split():
        continue
    checked += 1
    missing_libs, missing, weak = [], [], []
    # nm names the library an import comes from by its short name: libSystem for
    # /usr/lib/libSystem.B.dylib, CoreAudio for the framework. Imports from the app's own libraries
    # (@rpath, @loader_path) are checked in those files.
    system = set()
    for line in run("otool", "-arch", a.arch, "-L", str(f)).splitlines()[1:]:
        lib = line.strip().split(" (compatibility")[0]
        if not lib.startswith(("/usr/lib/", "/System/")):
            continue
        system.add(lib.rsplit("/", 1)[1].split(".")[0])
        if lib not in names:
            missing_libs.append(lib)
    for line in run("nm", "-arch", a.arch, "-u", "-m", str(f)).splitlines():
        found = re.match(r"\s*\(undefined\) (weak )?external (\S+) \(from (\S+)\)", line)
        if found and found[3] in system and found[2] not in exported:
            (weak if found[1] else missing).append(f"{found[2]} ({found[3]})")
    if missing_libs or missing or weak:
        print(f"\n{f}")
        for lib in missing_libs:
            print(f"  library not in the SDK: {lib}")
        for s in missing:
            print(f"  not exported: {s}")
        for s in weak:
            print(f"  weak, not exported: {s}")
        bad += bool(missing_libs or missing)
print(f"\n{checked} file(s) checked: " + (f"{bad} need something the SDK does not have" if bad else "each finds what it needs"))
sys.exit(1 if bad else 0)
