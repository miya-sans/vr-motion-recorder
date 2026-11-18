"""Packages the integrations for a release. Needs only Python 3.8+, no Blender and no Unity.

usage: python integrations/package.py <outdir>

Writes into <outdir>:
  vrmr_bridge-<version>.zip             Blender extension (4.2+): drag it into Blender, or Install from Disk
  vrmr_bridge.py                        the same add-on as one file (legacy "Install from Disk")
  com.vrmr.motion-bridge-<version>.tgz  Unity package: Package Manager > Add package from tarball

The zip and the tarball are built with fixed timestamps and a fixed file order, so the same sources give the
same bytes.
"""
import gzip
import io
import json
import os
import re
import sys
import tarfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
BLENDER = os.path.join(HERE, "blender")
UNITY = os.path.join(HERE, "unity", "com.vrmr.motion-bridge")
FIXED_TIME = (2020, 1, 1, 0, 0, 0)


def read(path):
    with open(path, "rb") as f:
        return f.read()


def blender_versions():
    """The add-on's version as the manifest and as bl_info state it."""
    manifest = read(os.path.join(BLENDER, "blender_manifest.toml")).decode("utf-8")
    m = re.search(r'^version\s*=\s*"([^"]+)"', manifest, re.M)
    script = read(os.path.join(BLENDER, "vrmr_bridge.py")).decode("utf-8")
    b = re.search(r'"version":\s*\((\d+),\s*(\d+),\s*(\d+)\)', script)
    if not m or not b:
        raise SystemExit("version not found in blender_manifest.toml or vrmr_bridge.py")
    return m.group(1), ".".join(b.groups())


def build_blender(out):
    version, bl_info_version = blender_versions()
    if version != bl_info_version:
        raise SystemExit("blender_manifest.toml says %s but bl_info says %s" % (version, bl_info_version))
    script = read(os.path.join(BLENDER, "vrmr_bridge.py"))
    files = [
        ("__init__.py", script),
        ("blender_manifest.toml", read(os.path.join(BLENDER, "blender_manifest.toml"))),
        ("LICENSE", read(os.path.join(REPO, "LICENSE"))),
    ]
    path = os.path.join(out, "vrmr_bridge-%s.zip" % version)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in files:
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, data)
    with open(os.path.join(out, "vrmr_bridge.py"), "wb") as f:
        f.write(script)
    return path


def build_unity(out):
    package = json.loads(read(os.path.join(UNITY, "package.json")).decode("utf-8"))
    names = []
    for folder, dirs, files in os.walk(UNITY):
        dirs.sort()
        for name in sorted(files):
            full = os.path.join(folder, name)
            names.append((os.path.relpath(full, UNITY).replace(os.sep, "/"), full))
    missing = [n for n, _ in names if not n.endswith(".meta") and not os.path.exists(os.path.join(UNITY, n + ".meta"))]
    folders = {os.path.dirname(n) for n, _ in names if os.path.dirname(n)}
    missing += [d for d in sorted(folders) if not os.path.exists(os.path.join(UNITY, d + ".meta"))]
    if missing:
        raise SystemExit("Unity package files without a .meta: " + ", ".join(missing))
    path = os.path.join(out, "%s-%s.tgz" % (package["name"], package["version"]))
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for name, full in names:
            data = read(full)
            info = tarfile.TarInfo("package/" + name)  # Unity expects everything under package/
            info.size = len(data)
            info.mode = 0o644
            info.mtime = 1577836800  # 2020-01-01
            tar.addfile(info, io.BytesIO(data))
    with open(path, "wb") as f:
        with gzip.GzipFile(fileobj=f, mode="wb", mtime=0, filename="") as gz:
            gz.write(raw.getvalue())
    return path


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    out = os.path.abspath(sys.argv[1])
    os.makedirs(out, exist_ok=True)
    for path in (build_blender(out), os.path.join(out, "vrmr_bridge.py"), build_unity(out)):
        print("%9d  %s" % (os.path.getsize(path), os.path.basename(path)))


if __name__ == "__main__":
    main()
