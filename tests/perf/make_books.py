"""Builds a realistic test library: big scanned-style comics and PDF, an illustrated EPUB.

Usage: python make_books.py OUT_DIR
Needs Pillow and reportlab; uses the 7z command for the .cb7 comic.
"""
import io
import os
import random
import shutil
import subprocess
import sys
import zipfile

from PIL import Image, ImageDraw, ImageFilter
from reportlab.lib.pagesizes import letter
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
random.seed(7)


def scan_page(w, h, n, quality=92):
    """A page that compresses like a real scan: shapes, text-ish marks and grain."""
    base = Image.new("RGB", (w, h), (random.randint(200, 245),) * 3)
    d = ImageDraw.Draw(base)
    for _ in range(9):
        x0, y0 = random.randint(0, w - 200), random.randint(0, h - 200)
        x1, y1 = x0 + random.randint(150, w // 2), y0 + random.randint(150, h // 3)
        d.rectangle([x0, y0, x1, y1], fill=tuple(random.randint(30, 220) for _ in range(3)), outline=(0, 0, 0), width=8)
    for _ in range(60):
        x, y = random.randint(0, w - 300), random.randint(0, h - 40)
        d.line([x, y, x + random.randint(80, 300), y], fill=(20, 20, 20), width=6)
    d.text((w // 2 - 40, 40), f"PAGE {n}", fill=(0, 0, 0))
    grain = Image.effect_noise((w, h), 28).convert("RGB")
    img = Image.blend(base, grain, 0.18).filter(ImageFilter.SMOOTH)
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=quality)
    return buf.getvalue()


def comic_pages(count, w=2000, h=3000):
    return [(f"Issue/page_{i:03d}.jpg", scan_page(w, h, i + 1)) for i in range(count)]


# --- Comics: one ZIP (CBZ) and one solid 7-Zip (CB7, behaves like CBR) ---
pages = comic_pages(120)
with zipfile.ZipFile(os.path.join(OUT, "Big Comic.cbz"), "w", zipfile.ZIP_STORED) as z:
    for name, data in pages:
        z.writestr(name, data)

tmp = os.path.join(OUT, "_cb7")
os.makedirs(tmp, exist_ok=True)
for name, data in pages:
    p = os.path.join(tmp, os.path.basename(name))
    with open(p, "wb") as f:
        f.write(data)
subprocess.run(["7z", "a", "-t7z", "-ms=on", "-mx=1", os.path.join(os.path.abspath(OUT), "Big Comic Solid.cb7"), "."], cwd=tmp, check=True, stdout=subprocess.DEVNULL)
shutil.rmtree(tmp)

# --- A comic with real panel layouts (white gutters), for the panel-by-panel view ---
def panel_page(n):
    W, H, g = 1200, 1800, 36
    im = Image.new("RGB", (W, H), "white")
    d = ImageDraw.Draw(im)
    layouts = [
        [(g, g, W - g, 640), (g, 640 + g, W // 2 - g // 2, 1200), (W // 2 + g // 2, 640 + g, W - g, 1200), (g, 1200 + g, W - g, H - g)],
        [(g, g, W // 2 - g // 2, 880), (W // 2 + g // 2, g, W - g, 880), (g, 880 + g, W - g, H - g)],
    ]
    colours = [(36, 64, 107), (184, 67, 58), (244, 208, 111), (60, 95, 140)]
    for k, box in enumerate(layouts[n % 2]):
        d.rectangle(box, fill=colours[k % 4], outline="black", width=8)
        d.ellipse([box[0] + 40, box[1] + 40, box[0] + 200, box[1] + 200], fill="white", outline="black", width=5)
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=88)
    return buf.getvalue()


with zipfile.ZipFile(os.path.join(OUT, "Panel Comic.cbz"), "w", zipfile.ZIP_STORED) as z:
    for i in range(8):
        z.writestr(f"page_{i + 1:02d}.jpg", panel_page(i))

# --- Scanned-style PDF ---
c = canvas.Canvas(os.path.join(OUT, "Big Scanned Manual.pdf"), pagesize=letter)
for p in range(1, 201):
    img = scan_page(1275, 1650, p, quality=80)
    c.drawImage(ImageReader(io.BytesIO(img)), 0, 0, width=letter[0], height=letter[1])
    if p % 20 == 1:
        c.bookmarkPage(f"p{p}")
        c.addOutlineEntry(f"Section {(p - 1) // 20 + 1}", f"p{p}", level=0)
    c.showPage()
c.save()

# --- Long illustrated EPUB ---
words = ("harbour light keeper tide evening lantern quiet water stone rope gull fog shore "
         "signal distant boat window stair bell weather salt morning north current").split()


def paragraph():
    return " ".join(random.choice(words) for _ in range(random.randint(60, 140))).capitalize() + "."


chapters = 80
with zipfile.ZipFile(os.path.join(OUT, "Long Illustrated Novel.epub"), "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr(zipfile.ZipInfo("mimetype"), "application/epub+zip", compress_type=zipfile.ZIP_STORED)
    z.writestr("META-INF/container.xml", '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
    manifest, spine, nav = [], [], []
    for i in range(chapters):
        img_tag = ""
        if i % 2 == 0:
            z.writestr(f"OEBPS/img{i}.jpg", scan_page(1400, 1000, i, quality=85), compress_type=zipfile.ZIP_STORED)
            manifest.append(f'<item id="i{i}" href="img{i}.jpg" media-type="image/jpeg"/>')
            img_tag = f'<p><img src="img{i}.jpg" alt=""/></p>'
        body = "".join(f"<p>{paragraph()}</p>" for _ in range(45))
        z.writestr(f"OEBPS/c{i}.xhtml", f'<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter {i + 1}</title></head><body><h1>Chapter {i + 1}</h1>{img_tag}{body}</body></html>')
        manifest.append(f'<item id="c{i}" href="c{i}.xhtml" media-type="application/xhtml+xml"/>')
        spine.append(f'<itemref idref="c{i}"/>')
        nav.append(f'<li><a href="c{i}.xhtml">Chapter {i + 1}</a></li>')
    z.writestr("OEBPS/nav.xhtml", '<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>nav</title></head><body><nav epub:type="toc"><ol>' + "".join(nav) + "</ol></nav></body></html>")
    z.writestr("OEBPS/content.opf", '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">perf-novel</dc:identifier><dc:title>Long Illustrated Novel</dc:title><dc:language>en</dc:language></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>' + "".join(manifest) + "</manifest><spine>" + "".join(spine) + "</spine></package>")

for f in sorted(os.listdir(OUT)):
    print(f"{os.path.getsize(os.path.join(OUT, f)) / 1e6:8.1f} MB  {f}")
