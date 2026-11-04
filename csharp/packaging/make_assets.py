"""Draws the app icon and the MSIX/Store logos (concept B, "Electric frame").

Heavy white capture corners and a yellow scan line on deep blue:
white on blue 8.7:1, yellow on blue 6.1:1. Run with any Python that has Pillow.
"""
from pathlib import Path
from PIL import Image, ImageDraw

here = Path(__file__).resolve().parent
BLUE, WHITE, YELLOW = (0x12, 0x38, 0xC7), (255, 255, 255), (0xFF, 0xD4, 0x00)
SS = 8  # supersampling factor for smooth edges


def corner_path(d, s, k):
    """Four capture corners, drawn on a 64-unit grid scaled by k."""
    w = 5.5 * k
    r = 3 * k

    def L(x1, y1, x2, y2):
        d.line([(x1 * k, y1 * k), (x2 * k, y2 * k)], fill=WHITE, width=round(w))
        for x, y in ((x1, y1), (x2, y2)):
            d.ellipse([x * k - w / 2, y * k - w / 2, x * k + w / 2, y * k + w / 2], fill=WHITE)

    def arc(cx, cy, start):
        box = [cx * k - r, cy * k - r, cx * k + r, cy * k + r]
        d.arc(box, start, start + 90, fill=WHITE, width=round(w))

    # top-left
    L(12, 24, 12, 15); arc(15, 15, 180); L(15, 12, 24, 12)
    # top-right
    L(40, 12, 49, 12); arc(49, 15, 270); L(52, 15, 52, 24)
    # bottom-right
    L(52, 40, 52, 49); arc(49, 49, 0); L(49, 52, 40, 52)
    # bottom-left
    L(24, 52, 15, 52); arc(15, 49, 90); L(12, 49, 12, 40)


def glyph(size, plate=True):
    s = size * SS
    k = s / 64
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    if plate:
        d.rounded_rectangle([2 * k, 2 * k, 62 * k, 62 * k], radius=15 * k, fill=BLUE)
    corner_path(d, s, k)
    d.rounded_rectangle([9 * k, 29 * k, 55 * k, 35 * k], radius=3 * k, fill=YELLOW)
    return im.resize((size, size), Image.LANCZOS)


def tile(w, h, scale=0.62):
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    g = glyph(int(min(w, h) * scale))
    im.alpha_composite(g, ((w - g.width) // 2, (h - g.height) // 2))
    return im


out = here / "Assets"
out.mkdir(exist_ok=True)
tile(50, 50, 1.0).save(out / "StoreLogo.png")
tile(150, 150).save(out / "Square150x150Logo.png")
tile(44, 44, 1.0).save(out / "Square44x44Logo.png")
tile(44, 44, 1.0).save(out / "Square44x44Logo.targetsize-44_altform-unplated.png")
tile(310, 150).save(out / "Wide310x150Logo.png")
tile(310, 310).save(out / "Square310x310Logo.png")
tile(71, 71).save(out / "Square71x71Logo.png")
glyph(512).save(out / "StoreListing-512.png")

ico = here.parent / "src" / "ScanCenter.App" / "Assets" / "ScanCenter.ico"
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [glyph(s) for s in sizes]
frames[-1].save(ico, sizes=[(s, s) for s in sizes], append_images=frames[:-1])
print("assets written to", out, "and", ico)
