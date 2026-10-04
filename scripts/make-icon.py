"""
Draws the Head Tracking icon from shapes, once per size, and writes:
    src/HeadTracking.App/Assets/HeadTracking.ico   (16 to 256 px, one drawing per size)
    src/HeadTracking.App/Assets/HeadTracking.png   (256 px, for the app's own sidebar)
    docs/icon.png                                  (256 px, for the README)

Why per size: shrinking one 256 px picture to 16-32 px turns its 12 px lines into blurred
sub-pixel smears. Each size is drawn at 8x and averaged down, with line widths that never fall
under about 1.6 px, ticks kept clear of the person, and the highlight arc only where it reads.

    python scripts/make-icon.py        (needs Pillow)
"""
import io
import os
import struct

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BG = (28, 30, 33, 255)
GOLD = (214, 170, 92, 255)
SHINE = (240, 240, 240, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPER = 8


def render(size):
    n = size * SUPER
    u = n / 256.0                      # design units (a 256 grid) to supersampled pixels
    c = n / 2.0
    img = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Rounded square.
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=round((46 if size >= 32 else 40) * u), fill=BG)

    # Line width in output pixels: the design's 12 units, but never thinner than reads.
    minimum = 1.6 if size <= 16 else 1.8 if size <= 24 else 2.0 if size <= 48 else 0
    stroke = max(12 * size / 256.0, minimum) * SUPER
    ring = (96 if size <= 20 else 90 if size <= 24 else 88) * u   # ring centre line: bigger when tiny, more room inside
    ro, ri = ring + stroke / 2, ring - stroke / 2

    # Ring (an annulus: gold disc, background disc on top).
    d.ellipse([c - ro, c - ro, c + ro, c + ro], fill=GOLD)
    d.ellipse([c - ri, c - ri, c + ri, c + ri], fill=BG)

    # Highlight arc on the ring's upper left, where it reads (48 px and up).
    if size >= 48:
        shine = Image.new('RGBA', (n, n), (0, 0, 0, 0))
        sd = ImageDraw.Draw(shine)
        sd.pieslice([c - ro - 2, c - ro - 2, c + ro + 2, c + ro + 2], 205, 245, fill=SHINE)
        sd.ellipse([c - ri, c - ri, c + ri, c + ri], fill=(0, 0, 0, 0))
        img.alpha_composite(shine)

    # Crosshair ticks: outside the ring only, so nothing cuts through the person.
    t_in = ro - stroke * 0.25
    t_out = min(c - (14 if size >= 32 else 4) * u, ro + (20 if size >= 32 else 14) * u + stroke * 0.5)
    hw = stroke / 2
    d.rectangle([c - hw, c - t_out, c + hw, c - t_in], fill=GOLD)   # top
    d.rectangle([c - hw, c + t_in, c + hw, c + t_out], fill=GOLD)   # bottom
    d.rectangle([c - t_out, c - hw, c - t_in, c + hw], fill=GOLD)   # left
    d.rectangle([c + t_in, c - hw, c + t_out, c + hw], fill=GOLD)   # right

    # Person: head and shoulders, centred, kept a clear gap inside the ring.
    person = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    pd = ImageDraw.Draw(person)
    tiny = size <= 20
    head_r = (31 if size >= 32 else 40 if tiny else 34) * u
    head_y = c - (24 if size >= 32 else 22 if tiny else 22) * u
    pd.ellipse([c - head_r, head_y - head_r, c + head_r, head_y + head_r], fill=GOLD)
    gap_neck = max(6 * u, stroke * 0.6)
    sh_top = head_y + head_r + gap_neck
    sh_rx = (62 if size >= 32 else 78 if tiny else 66) * u
    sh_ry = (58 if size >= 32 else 66 if tiny else 60) * u
    pd.ellipse([c - sh_rx, sh_top, c + sh_rx, sh_top + 2 * sh_ry], fill=GOLD)
    clip = Image.new('L', (n, n), 0)
    gap = max(8 * u, stroke * (0.6 if tiny else 0.75))
    cr = ri - gap
    ImageDraw.Draw(clip).ellipse([c - cr, c - cr, c + cr, c + cr], fill=255)
    person.putalpha(Image.composite(person.getchannel('A'), Image.new('L', (n, n), 0), clip))
    img.alpha_composite(person)

    return img.resize((size, size), Image.BOX)


def write_ico(path, images):
    """An .ico with one PNG-compressed entry per size (Windows Vista and later read these at any size)."""
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format='PNG', optimize=True)
        blobs.append(buf.getvalue())
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    for im, blob in zip(images, blobs):
        w, h = im.size
        entries += struct.pack('<BBBBHHII', w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    with open(path, 'wb') as f:
        f.write(header + entries + b''.join(blobs))


def main():
    images = [render(s) for s in SIZES]
    assets = os.path.join(ROOT, 'src', 'HeadTracking.App', 'Assets')
    write_ico(os.path.join(assets, 'HeadTracking.ico'), images)
    images[-1].save(os.path.join(assets, 'HeadTracking.png'), optimize=True)
    images[-1].save(os.path.join(ROOT, 'docs', 'icon.png'), optimize=True)
    print('Wrote HeadTracking.ico (' + ', '.join(str(s) for s in SIZES) + ' px), HeadTracking.png and docs/icon.png.')


if __name__ == '__main__':
    main()
