# Genera app.ico: el dado d20 de la aplicación, en latón sobre madera oscura, en varios tamaños.
# Uso: python generar_icono.py <carpeta_de_salida>   (necesita Pillow: pip install pillow)
import sys, io, struct
from PIL import Image, ImageDraw

OUT = sys.argv[1]
SS = 8  # supermuestreo

BG_TOP = (0x26, 0x1e, 0x17)
BG_BOTTOM = (0x16, 0x11, 0x0d)
BORDER = (0x45, 0x37, 0x29)
BRASS = (0xd9, 0xa4, 0x41)
BRASS_LIGHT = (0xf0, 0xc2, 0x63)

HEX = [(12, 2), (20.5, 7), (20.5, 17), (12, 22), (3.5, 17), (3.5, 7)]
TRI = [(12, 7.5), (17, 15.5), (7, 15.5)]
LINES = [
    [(3.5, 7), (12, 7.5), (20.5, 7)],
    [(3.5, 7), (7, 15.5), (3.5, 17)],
    [(7, 15.5), (12, 22), (17, 15.5), (20.5, 17)],
    [(20.5, 7), (17, 15.5)],
]

def render(size):
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    # Fondo: losa redondeada con un degradado vertical suave.
    margin = round(n * (0.03 if size >= 48 else 0.0))
    radius = round(n * 0.22)
    grad = Image.new("RGBA", (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(n):
        t = y / max(1, n - 1)
        c = tuple(round(BG_TOP[i] * (1 - t) + BG_BOTTOM[i] * t) for i in range(3)) + (255,)
        gd.line([(0, y), (n, y)], fill=c)
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, n - 1 - margin, n - 1 - margin], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    border_w = max(SS, round(n * 0.012))
    d.rounded_rectangle([margin, margin, n - 1 - margin, n - 1 - margin], radius=radius, outline=BORDER + (255,), width=border_w)

    # Dado: ocupa ~72 % (más en tamaños pequeños para que se lea).
    scale_frac = 0.74 if size >= 48 else 0.86
    box = n * scale_frac
    k = box / 20.0                     # el dado mide 17 x 20 en la caja de 24
    ox = (n - 17 * k) / 2 - 3.5 * k
    oy = (n - 20 * k) / 2 - 2 * k
    P = lambda p: (ox + p[0] * k, oy + p[1] * k)

    # Grosor del trazo según el tamaño final (en píxeles reales).
    if size >= 128: stroke_px = size * 0.040
    elif size >= 48: stroke_px = size * 0.052
    elif size >= 32: stroke_px = 2.3
    elif size >= 24: stroke_px = 1.9
    else: stroke_px = 1.45
    sw = max(1, round(stroke_px * SS))

    # Caras con un relleno tenue para dar volumen (más marcado en pequeño).
    face = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    fd = ImageDraw.Draw(face)
    fd.polygon([P(p) for p in HEX], fill=BRASS + (34 if size >= 48 else 46,))
    fd.polygon([P(p) for p in TRI], fill=BRASS + (70 if size >= 48 else 255 if size <= 16 else 120,))
    img.alpha_composite(face)

    def poly(points, closed, color):
        pts = [P(p) for p in points]
        if closed: pts = pts + [pts[0]]
        d.line(pts, fill=color + (255,), width=sw, joint="curve")
        r = sw / 2
        for x, y in pts:
            d.ellipse([x - r, y - r, x + r, y + r], fill=color + (255,))

    if size > 16:
        for l in LINES:
            poly(l, False, BRASS)
    poly(TRI, True, BRASS_LIGHT if size >= 48 else BRASS)
    poly(HEX, True, BRASS)

    return img.resize((size, size), Image.LANCZOS)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = {s: render(s) for s in sizes}

# ICO: tamaños pequeños en BMP de 32 bits con canal alfa (máxima compatibilidad con Windows y .NET)
# y los grandes en PNG (como hace Visual Studio).
def dib(im):
    w, h = im.size
    px = im.load()
    out = bytearray(struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0))
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            out += bytes((b, g, r, a))
    row = ((w + 31) // 32) * 4
    for y in range(h - 1, -1, -1):
        bits = bytearray(row)
        for x in range(w):
            if px[x, y][3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        out += bits
    return bytes(out)

entries = []
for s in sizes:
    if s >= 64:
        buf = io.BytesIO()
        images[s].save(buf, format="PNG", optimize=True)
        entries.append((s, buf.getvalue()))
    else:
        entries.append((s, dib(images[s])))
header = struct.pack("<HHH", 0, 1, len(entries))
offset = 6 + 16 * len(entries)
dir_bytes = b""
data = b""
for s, png in entries:
    w = 0 if s >= 256 else s
    dir_bytes += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(png), offset + len(data))
    data += png
open(OUT + "/app.ico", "wb").write(header + dir_bytes + data)

# Hoja de muestra: todos los tamaños sobre fondo claro y oscuro, y a tamaño real ampliado.
W = sum(s + 24 for s in sizes) + 24
sheet = Image.new("RGBA", (W, 2 * (256 + 40)), (255, 255, 255, 255))
x = 24
for s in sizes:
    for row, bg in enumerate([(236, 236, 236, 255), (32, 32, 32, 255)]):
        tile = Image.new("RGBA", (s + 16, 256 + 24), bg)
        tile.alpha_composite(images[s], (8, 12))
        sheet.paste(tile, (x - 8, row * (256 + 40) + 8))
    x += s + 24
sheet.save(OUT + "/new_icon_sheet.png")

# Pequeños ampliados 8x (pixelado) para ver la legibilidad real.
small = [16, 24, 32, 48]
Z = 8
zs = Image.new("RGBA", (sum(s * Z + 24 for s in small) + 24, 48 * Z + 48), (236, 236, 236, 255))
x = 24
for s in small:
    zs.alpha_composite(images[s].resize((s * Z, s * Z), Image.NEAREST), (x, 24))
    x += s * Z + 24
zs.save(OUT + "/new_icon_small_zoom.png")
print("ok", len(open(OUT + "/app.ico", "rb").read()), "bytes")
