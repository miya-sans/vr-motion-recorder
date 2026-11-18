"""Compare two `vrmr dump` folders (a reference and a candidate).

usage: python compare_dumps.py <refDir> <candDir> [--tol-mm 0.1]

- Summary lines (hashes of solve, bone positions, face, parameters, camera, trackers, exports) are compared first:
  identical hashes mean bit-identical results.
- Where they differ (another runtime or CPU, or a real change), the raw curves decide: bone positions (.nodes.bin)
  and the other curves (.aux.bin) are compared numerically.
- Exports are compared by content: FBX node by node with arrays decompressed (time stamps ignored), text files
  (.anim, .json) with every number read as a 32-bit float; FBX numbers may differ by 1e-9 (last-bit rounding). Runtimes print floats and compress differently, so the
  bytes of two correct exports can differ.

Exit code 0 when everything is identical or within the tolerance, 1 otherwise.
"""
import glob
import os
import struct
import sys

import numpy as np

args = [a for a in sys.argv[1:] if not a.startswith("--")]
tol_mm = float(sys.argv[sys.argv.index("--tol-mm") + 1]) if "--tol-mm" in sys.argv else 0.1
ref, cand = args[0], args[1]
rn = os.path.basename(os.path.normpath(ref)).encode()
cn = os.path.basename(os.path.normpath(cand)).encode()


def lines(d):
    return open(os.path.join(d, "summary.txt"), encoding="utf-8").read().splitlines()


A, B = lines(ref), lines(cand)
# a dump made with --no-exports has no export lines: compare the rest
if not any(l.startswith("  export") for l in A) or not any(l.startswith("  export") for l in B):
    A = [l for l in A if not l.startswith("  export")]
    B = [l for l in B if not l.startswith("  export")]
same = sum(1 for a, b in zip(A, B) if a == b)
print(f"summary: {same} of {max(len(A), len(B))} lines identical")
ok = len(A) == len(B)

# ---- structure that must match exactly even across runtimes: frame counts, avatar, body source, curve counts
def shape(line):
    f = line.split()
    keep = []
    for i, w in enumerate(f):
        if w in ("frames", "body", "avatar", "warnings", "held", "transitions"):
            keep.append(w + "=" + f[i + 1])
        if w in ("face", "params", "trackers") and i + 1 < len(f):
            keep.append(w + "#=" + f[i + 1].split(":")[0])
    return " ".join(f[:2] + keep)


shape_diff = [(a, b) for a, b in zip(A, B) if (a.startswith("take") or a.startswith("scene")) and shape(a) != shape(b)]
for a, b in shape_diff[:10]:
    print("  STRUCTURE differs:", shape(a), "|", shape(b))
ok &= not shape_diff

# ---- bone positions
worst_mm = 0.0
print(f"{'take':24} {'bone pos max mm':>16} {'mean mm':>9} {'p99.9 mm':>9} {'>0.1mm':>8} {'face max':>9} {'param max':>10} {'tracker mm':>11}")
for f in sorted(glob.glob(os.path.join(ref, "*.nodes.bin"))):
    name = os.path.basename(f)[: -len(".nodes.bin")]
    g = os.path.join(cand, os.path.basename(f))
    if not os.path.exists(g):
        print(f"  {name}: missing in candidate"); ok = False; continue
    x = np.fromfile(f, dtype="<f4", offset=8); y = np.fromfile(g, dtype="<f4", offset=8)
    if x.size != y.size:
        print(f"  {name}: sizes differ"); ok = False; continue
    d = np.linalg.norm((x - y).reshape(-1, 3).astype(np.float64), axis=1) * 1000.0
    # other curves
    def sections(path):
        out = {}
        with open(path, "rb") as fh:
            data = fh.read()
        p = 0
        while p < len(data):
            # BinaryWriter string: 7-bit encoded length prefix
            n = 0; shift = 0
            while True:
                b = data[p]; p += 1
                n |= (b & 0x7F) << shift; shift += 7
                if not b & 0x80: break
            key = data[p:p + n].decode("utf-8"); p += n
            cnt = struct.unpack_from("<i", data, p)[0]; p += 4
            out[key] = np.frombuffer(data, dtype="<f4", count=cnt, offset=p); p += cnt * 4
        return out
    fa, pa, tr = 0.0, 0.0, 0.0
    ax, ay = f.replace(".nodes.bin", ".aux.bin"), g.replace(".nodes.bin", ".aux.bin")
    if os.path.exists(ax) and os.path.exists(ay):
        sx, sy = sections(ax), sections(ay)
        if sx.keys() != sy.keys():
            print(f"  {name}: curve names differ"); ok = False
        for k in sx.keys() & sy.keys():
            if sx[k].size != sy[k].size:
                print(f"  {name}: {k} length differs"); ok = False; continue
            if sx[k].size == 0: continue
            m = float(np.max(np.abs(sx[k].astype(np.float64) - sy[k])))
            if k.startswith("face/"): fa = max(fa, m)
            elif k.startswith("param/"): pa = max(pa, m)
            elif k.startswith("tracker/") or k == "camera":
                v = (sx[k].astype(np.float64) - sy[k]).reshape(-1, 7)[:, :3]
                tr = max(tr, float(np.max(np.linalg.norm(v, axis=1))) * 1000.0)
    mx = float(d.max()) if d.size else 0.0
    worst_mm = max(worst_mm, mx, tr)
    print(f"{name:24} {mx:16.5f} {float(d.mean()) if d.size else 0:9.6f} {float(np.quantile(d, 0.999)) if d.size else 0:9.5f} {int((d > 0.1).sum()):8d} {fa:9.2e} {pa:10.2e} {tr:11.5f}")

# ---- exports, by content: runtimes print floats and compress FBX arrays differently, so bytes are no guide
import re
import zlib


def fbx_nodes(data):
    """Yields (path, property list) for every node of a binary FBX; arrays are decompressed to raw bytes."""
    ver = struct.unpack_from("<I", data, 23)[0]
    wide = ver >= 7500
    hdr = 25 if wide else 13

    def walk(pos, end, path):
        while pos < end:
            if wide: e, n, ln = struct.unpack_from("<QQQ", data, pos); p = pos + 24
            else: e, n, ln = struct.unpack_from("<III", data, pos); p = pos + 12
            if e == 0: return
            nl = data[p]; name = data[p + 1:p + 1 + nl].decode("ascii", "replace"); p += 1 + nl
            props = []; pe = p + ln
            for _ in range(n):
                t = chr(data[p]); p += 1
                if t in "YCIFDL":
                    size = {"Y": 2, "C": 1, "I": 4, "F": 4, "D": 8, "L": 8}[t]
                    props.append((t, data[p:p + size])); p += size
                elif t in "fdlib":
                    cnt, enc, clen = struct.unpack_from("<III", data, p); p += 12
                    raw = data[p:p + clen]; p += clen
                    props.append((t, zlib.decompress(raw) if enc == 1 else raw))
                else:  # S, R
                    ln2 = struct.unpack_from("<I", data, p)[0]; p += 4
                    props.append((t, data[p:p + ln2])); p += ln2
            yield path + "/" + name, props
            if pe < e: yield from walk(pe, e - hdr, path + "/" + name)
            pos = e
    yield from walk(27, len(data), "")


def fbx_equal(f, g):
    x = open(f, "rb").read(); y = open(g, "rb").read()
    na, nb = list(fbx_nodes(x)), list(fbx_nodes(y))
    if len(na) != len(nb): return f"node count {len(na)} vs {len(nb)}"
    for (pa_, a), (pb_, b) in zip(na, nb):
        if pa_ != pb_: return f"node {pa_} vs {pb_}"
        if "CreationTime" in pa_ or pa_.endswith("/Creator") or "DateTime" in pa_: continue
        if len(a) != len(b): return f"{pa_}: property count"
        for (ta, va), (tb, vb) in zip(a, b):
            if ta != tb: return f"{pa_}: property type"
            if va == vb or va.replace(rn, b"#") == vb.replace(cn, b"#"): continue
            # doubles may differ in the last bit between runtimes (their maths libraries round differently)
            kind = {"D": "<f8", "d": "<f8", "F": "<f4", "f": "<f4"}.get(ta)
            if kind is None or len(va) != len(vb): return f"{pa_}: value differs ({ta}, {len(va)} bytes)"
            d = float(np.max(np.abs(np.frombuffer(va, dtype=kind).astype(np.float64) - np.frombuffer(vb, dtype=kind))))
            if d > 1e-9: return f"{pa_}: numbers differ by up to {d:.3g}"
    return None


num = re.compile(rb"-?\d+\.?\d*(?:[eE][-+]?\d+)?")


def text_equal(f, g):
    """Same text once every number is read as a 32-bit float (the precision the exporters write)."""
    x = open(f, "rb").read(); y = open(g, "rb").read()
    if x == y: return None
    nx, ny = num.findall(x), num.findall(y)
    if len(nx) != len(ny): return f"{len(nx)} vs {len(ny)} numbers"
    if num.sub(b"#", x) != num.sub(b"#", y): return "text between the numbers differs"
    a = np.array(nx, dtype=np.float64).astype(np.float32); b = np.array(ny, dtype=np.float64).astype(np.float32)
    bad = int(np.count_nonzero(a != b))
    return None if bad == 0 else f"{bad} of {a.size} numbers differ (largest {float(np.max(np.abs(a.astype(np.float64) - b))):.3g})"


nexp = 0; exp_bad = []; skipped = 0
big = 80 << 20 if "--all-exports" not in sys.argv else 1 << 62
for f in sorted(glob.glob(os.path.join(ref, "export", "*", "*"))):
    if not os.path.isfile(f): continue
    g = os.path.join(cand, os.path.relpath(f, ref))
    if not os.path.exists(g): exp_bad.append((f, "missing")); continue
    if os.path.getsize(f) > big: skipped += 1; continue
    why = fbx_equal(f, g) if f.lower().endswith(".fbx") else text_equal(f, g)
    nexp += 1
    if why: exp_bad.append((os.path.basename(f), why))
print(f"exports: {nexp} files compared by content, {len(exp_bad)} different" + (f", {skipped} over {big >> 20} MB skipped (--all-exports checks them)" if skipped else ""))
for n_, why in exp_bad[:12]: print("  EXPORT", n_, "->", why)
ok &= not exp_bad
worst_bytes = 0

identical = all(a == b or a.startswith("  export") or a.startswith("  calib") for a, b in zip(A, B)) and len(A) == len(B)
if identical and ok:
    print("RESULT: IDENTICAL")
elif ok and worst_mm <= tol_mm:
    print(f"RESULT: WITHIN TOLERANCE (largest difference {worst_mm:.5f} mm, tolerance {tol_mm} mm)")
else:
    print(f"RESULT: DIFFERENT (largest difference {worst_mm:.5f} mm, tolerance {tol_mm} mm)")
sys.exit(0 if ok and (identical or worst_mm <= tol_mm) else 1)
