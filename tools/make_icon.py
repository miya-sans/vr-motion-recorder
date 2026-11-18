"""Draws the VR Motion Recorder app icon: a motion-capture skeleton mid-stride with tracker dots, speed lines and a
record dot, on a dark rounded tile in the app's theme colours. The design is original to this project and MIT
licensed like the rest of the repository.

Writes vrmr.ico (256 to 16 px, PNG entries; the small sizes use a simplified, bolder drawing), vrmr-1024.png and
vrmr-512.png. Needs Pillow.

usage: python tools/make_icon.py [outdir] [--preview]
  outdir     where to write (default: assets/icon next to this script's repository)
  --preview  also write preview-sheet.png: every size on a dark and a light background
"""
import io, os, struct, sys
from PIL import Image, ImageDraw, ImageFilter

BG_TOP, BG_BOT = (34, 37, 52), (15, 16, 21)
BORDER = (58, 62, 78)
FRONT, MID, BACK = (140, 192, 255), (110, 168, 255), (70, 118, 205)
TRACKER, TRACKER_RIM = (236, 240, 248), (18, 19, 23)
REC = (240, 72, 72)

J = dict(head=(585, 252), neck=(565, 348), chest=(548, 438), pelvis=(518, 592),
         e1=(668, 458), h1=(766, 402), e2=(442, 300), h2=(362, 205),
         k1=(652, 708), f1=(724, 862), k2=(452, 742), f2=(326, 822))


def tile(S):
    """Rounded tile with a vertical gradient, 1024-unit coordinates scaled to S."""
    k = S / 1024
    grad = Image.new("RGB", (1, 256))
    for y in range(256):
        t = y / 255
        grad.putpixel((0, y), tuple(int(BG_TOP[i] * (1 - t) + BG_BOT[i] * t) for i in range(3)))
    grad = grad.resize((S, S))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40 * k, 40 * k, 984 * k, 984 * k], radius=210 * k, fill=255)
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    im.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([40 * k, 40 * k, 984 * k, 984 * k], radius=210 * k, outline=BORDER, width=max(1, int(10 * k)))
    return im


def line(d, a, b, w, col, k):
    a = (a[0] * k, a[1] * k); b = (b[0] * k, b[1] * k); r = w * k / 2
    d.line([a, b], fill=col, width=max(1, int(round(w * k))))
    for p in (a, b):
        d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=col)


def dot(d, p, r, col, k, rim=None, rimw=0):
    x, y = p[0] * k, p[1] * k
    if rim:
        R = (r + rimw) * k
        d.ellipse([x - R, y - R, x + R, y + R], fill=rim)
    d.ellipse([x - r * k, y - r * k, x + r * k, y + r * k], fill=col)


def render(S, detail):
    """detail 2: full (>= 48 px); 1: no joints, bolder (24-32 px); 0: silhouette + record dot (16 px)."""
    SS = S * 4 if S < 512 else S  # supersample
    k = SS / 1024
    im = tile(SS)
    d = ImageDraw.Draw(im)
    bone = {2: 50, 1: 74, 0: 96}[detail]
    if detail == 2:  # speed lines behind the figure
        for y, x0, x1, a in ((430, 150, 300, 110), (560, 120, 290, 80), (690, 170, 300, 55)):
            ov = Image.new("RGBA", im.size, (0, 0, 0, 0))
            line(ImageDraw.Draw(ov), (x0, y), (x1, y), 30, MID + (a,), k)
            im.alpha_composite(ov)
        d = ImageDraw.Draw(im)
    # back limbs first (depth), then spine, then front limbs
    for a, b in (("neck", "e2"), ("e2", "h2"), ("pelvis", "k2"), ("k2", "f2")):
        line(d, J[a], J[b], bone, BACK, k)
    for a, b in (("neck", "chest"), ("chest", "pelvis")):
        line(d, J[a], J[b], bone * 1.15, MID, k)
    for a, b in (("neck", "e1"), ("e1", "h1"), ("pelvis", "k1"), ("k1", "f1")):
        line(d, J[a], J[b], bone, FRONT, k)
    hr = {2: 72, 1: 84, 0: 96}[detail]
    dot(d, J["head"], hr, FRONT, k)
    if detail >= 1:  # trackers: hands, feet, hips (white dots with a dark rim)
        tr = 30 if detail == 2 else 40
        for j in ("h1", "h2", "f1", "f2", "pelvis"):
            dot(d, J[j], tr, TRACKER, k, rim=TRACKER_RIM, rimw=12 if detail == 2 else 14)
    if detail == 2:
        for j in ("e1", "e2", "k1", "k2"):
            dot(d, J[j], 15, TRACKER_RIM, k)
    # record dot (top right) with a soft halo
    rc, rr = (812, 212), {2: 66, 1: 92, 0: 120}[detail]
    if detail == 0:
        rc = (790, 232)
    halo = Image.new("RGBA", im.size, (0, 0, 0, 0))
    dot(ImageDraw.Draw(halo), rc, rr + 34, REC + (90,), k)
    im.alpha_composite(halo.filter(ImageFilter.GaussianBlur(18 * k)))
    d = ImageDraw.Draw(im)
    dot(d, rc, rr + 10, BG_BOT, k)
    dot(d, rc, rr, REC, k)
    return im.resize((S, S), Image.LANCZOS) if SS != S else im


def write_ico(path, images):
    """ICO with PNG-compressed entries (Windows Vista+), one per size."""
    blobs = []
    for im in images:
        b = io.BytesIO(); im.save(b, "PNG"); blobs.append((im.size[0], b.getvalue()))
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(blobs)))
    off = 6 + 16 * len(blobs)
    for s, data in blobs:
        out.write(struct.pack("<BBBBHHII", s if s < 256 else 0, s if s < 256 else 0, 0, 0, 1, 32, len(data), off))
        off += len(data)
    for _, data in blobs:
        out.write(data)
    open(path, "wb").write(out.getvalue())


def preview_sheet(sizes, imgs):
    """Every icon size at 1x on dark and light, and the four smallest enlarged without smoothing."""
    W = sum(s for s in sizes) + 20 * len(sizes) + 20
    sheet = Image.new("RGBA", (max(W, 1100), 760), (245, 246, 248, 255))
    sd = ImageDraw.Draw(sheet)
    sd.rectangle([0, 0, sheet.size[0], 300], fill=(30, 31, 36, 255))
    x = 20
    for s, im in zip(sizes, imgs):
        sheet.alpha_composite(im, (x, 20)); sheet.alpha_composite(im, (x, 320)); x += s + 20
    x = 20
    for s, im in zip(sizes[-4:], imgs[-4:]):
        big = im.resize((s * 6, s * 6), Image.NEAREST)
        sheet.alpha_composite(big, (x, 600 - s * 3 + 40 if s * 6 > 200 else 560)); x += s * 6 + 30
    return sheet


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out = args[0] if args else os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "icon")
    os.makedirs(out, exist_ok=True)
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    imgs = [render(s, 2 if s >= 48 else 1 if s >= 24 else 0) for s in sizes]
    write_ico(os.path.join(out, "vrmr.ico"), imgs)
    render(1024, 2).save(os.path.join(out, "vrmr-1024.png"))
    render(512, 2).save(os.path.join(out, "vrmr-512.png"))
    if "--preview" in sys.argv:
        preview_sheet(sizes, imgs).save(os.path.join(out, "preview-sheet.png"))
    print("wrote", out, [im.size[0] for im in imgs])
