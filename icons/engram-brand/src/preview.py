"""Compose a preview/contact sheet for the engram brand pack."""
from PIL import Image, ImageDraw, ImageFont

OUT = "/mnt/user-data/outputs/engram-brand"
PNG = f"{OUT}/png"
FB = "/home/claude/fonts/JetBrainsMono-Bold.ttf"
FR = "/home/claude/fonts/JetBrainsMono-Regular.ttf"
FM = "/home/claude/fonts/JetBrainsMono-Medium.ttf"

WIDTH = 1500
BG = (10, 14, 19, 255)
PANEL = (15, 21, 29, 255)
PANEL2 = (13, 17, 23, 255)
WHITE = (245, 247, 249, 255)
MUTED = (120, 134, 148, 255)
ACCENT = (67, 224, 139, 255)

canvas = Image.new("RGBA", (WIDTH, 2350), BG)
d = ImageDraw.Draw(canvas)


def font(p, s):
    return ImageFont.truetype(p, s)


def rrect(box, r, fill, outline=None, ow=1):
    d.rounded_rectangle(box, radius=r, fill=fill, outline=outline, width=ow)


def paste(path, x, y, w=None, h=None):
    im = Image.open(path).convert("RGBA")
    if w or h:
        if w and not h:
            h = int(im.height * w / im.width)
        if h and not w:
            w = int(im.width * h / im.height)
        im = im.resize((w, h), Image.LANCZOS)
    canvas.alpha_composite(im, (int(x), int(y)))
    return im.width, im.height


def label(x, y, text, fnt, fill=MUTED, anchor="la"):
    d.text((x, y), text, font=fnt, fill=fill, anchor=anchor)


PAD = 70
f_h1 = font(FB, 46)
f_h2 = font(FB, 26)
f_lbl = font(FM, 19)
f_hex = font(FR, 18)
f_small = font(FR, 16)

# ---- header lockup
hw, hh = paste(f"{PNG}/engram-logo-horizontal-dark.png", PAD, 52, h=104)
label(WIDTH - PAD, 70, "brand pack", font(FM, 22), fill=MUTED, anchor="ra")
label(WIDTH - PAD, 100, "terminal second-brain", f_small, fill=(80, 92, 104, 255), anchor="ra")
y = 200
d.line([(PAD, y), (WIDTH - PAD, y)], fill=(34, 42, 52, 255), width=2)
y += 44

# ---- section: app icon
label(PAD, y, "APP ICON", f_h2, fill=WHITE)
y += 48
ic = 248
gap = 56
x = PAD
paste(f"{OUT}/svg/../png/icon/engram-256.png", x, y, w=ic, h=ic)
label(x, y + ic + 14, "engram-icon.svg", f_lbl, fill=MUTED)
label(x, y + ic + 40, "primary · clustered", f_small, fill=(80, 92, 104, 255))
x += ic + gap
import cairosvg as _cs, engram_brand as _E
_cs.svg2png(bytestring=_E.icon_svg("mono").encode(), write_to="/home/claude/_mono256.png",
            output_width=ic, output_height=ic)
paste("/home/claude/_mono256.png", x, y)
label(x, y + ic + 14, "engram-icon-mono.svg", f_lbl, fill=MUTED)
label(x, y + ic + 40, "single-colour", f_small, fill=(80, 92, 104, 255))
x += ic + gap
# mark on chrome panel
rrect([x, y, x + ic, y + ic], 30, (13, 17, 23, 255), outline=(34, 42, 52, 255), ow=2)
paste(f"{OUT}/svg/../png/icon/engram-256.png", x, y, w=ic, h=ic) if False else None
# render transparent mark for display
import cairosvg, engram_brand as E
mark_svg = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">{E.icon_body("mono", tile=False, prefix="pv_")}</svg>'
cairosvg.svg2png(bytestring=mark_svg.encode(), write_to="/home/claude/_mark.png", output_width=int(ic*0.82), output_height=int(ic*0.82))
paste("/home/claude/_mark.png", x + ic*0.09, y + ic*0.09)
label(x, y + ic + 14, "engram-mark-transparent.svg", f_lbl, fill=MUTED)
label(x, y + ic + 40, "overlay on dark chrome", f_small, fill=(80, 92, 104, 255))
y += ic + 84

# ---- section: scales
label(PAD, y, "SCALES DOWN", f_h2, fill=WHITE)
y += 46
# plate
plate_h = 110
rrect([PAD, y, WIDTH - PAD, y + plate_h], 18, PANEL)
sizes = [16, 24, 32, 48, 64]
x = PAD + 40
cy = y + plate_h // 2
for s in sizes:
    im = Image.open(f"{PNG}/icon/engram-{s}.png").convert("RGBA")
    canvas.alpha_composite(im, (int(x), int(cy - s/2)))
    label(x + s/2, y + plate_h - 22, f"{s}px", f_small, fill=MUTED, anchor="ma")
    x += s + 54
label(x + 20, cy - 30, "engram.ico", f_lbl, fill=WHITE, anchor="la")
label(x + 20, cy - 4, "16 → 256, full colour", f_small, fill=MUTED, anchor="la")
y += plate_h + 22
rrect([PAD, y, WIDTH - PAD, y + plate_h], 18, PANEL2)
x = PAD + 40
cy = y + plate_h // 2
for s in sizes:
    im = Image.open(f"{PNG}/tray/engram-tray-{s}.png").convert("RGBA")
    canvas.alpha_composite(im, (int(x), int(cy - s/2)))
    label(x + s/2, y + plate_h - 22, f"{s}px", f_small, fill=MUTED, anchor="ma")
    x += s + 54
label(x + 20, cy - 30, "engram-tray.ico", f_lbl, fill=ACCENT, anchor="la")
label(x + 20, cy - 4, "monochrome · system tray", f_small, fill=MUTED, anchor="la")
y += plate_h + 84

# ---- section: lockups
label(PAD, y, "LOCKUPS", f_h2, fill=WHITE)
y += 46
# horizontal on dark panel
ph = 240
rrect([PAD, y, WIDTH - PAD, y + ph], 18, PANEL)
paste(f"{PNG}/engram-logo-horizontal-dark.png", PAD + 50, y + 70, h=ph - 140)
label(PAD + 24, y + ph - 30, "engram-logo-horizontal-dark.svg", f_small, fill=MUTED)
y += ph + 22
# stacked dark + light side by side
ph2 = 360
half = (WIDTH - 2*PAD - 30) // 2
rrect([PAD, y, PAD + half, y + ph2], 18, PANEL)
paste(f"{PNG}/engram-logo-stacked-dark.png", PAD + (half-300)//2, y + 30, h=ph2 - 90)
label(PAD + 24, y + ph2 - 30, "engram-logo-stacked-dark.svg", f_small, fill=MUTED)
rrect([PAD + half + 30, y, WIDTH - PAD, y + ph2], 18, (244, 246, 248, 255))
paste(f"{PNG}/engram-logo-stacked-light.png", PAD + half + 30 + (half-300)//2, y + 30, h=ph2 - 90)
label(PAD + half + 54, y + ph2 - 30, "engram-logo-stacked-light.svg", f_small, fill=(120, 130, 140, 255))
y += ph2 + 84

# ---- section: palette
label(PAD, y, "PALETTE", f_h2, fill=WHITE)
y += 46
swatches = [
    ("Accent", "#43E08B"), ("Accent dark", "#179A55"), ("Node", "#E3F3EA"),
    ("Ink / tile", "#0D1117"), ("BG top", "#19212D"), ("BG bottom", "#0B0F15"),
    ("Cluster cyan", "#5CC7F5"), ("Cluster amber", "#F5B657"), ("Cluster magenta", "#F07CC0"),
]
cols = 3
cw = (WIDTH - 2*PAD - (cols-1)*24) // cols
ch = 92
for i, (nm, hx) in enumerate(swatches):
    cx = PAD + (i % cols) * (cw + 24)
    cyy = y + (i // cols) * (ch + 18)
    rrect([cx, cyy, cx + cw, cyy + ch], 14, PANEL, outline=(34, 42, 52, 255), ow=1)
    rgb = tuple(int(hx[1+j*2:3+j*2], 16) for j in range(3)) + (255,)
    rrect([cx + 14, cyy + 14, cx + 14 + 64, cyy + ch - 14], 10, rgb, outline=(40, 48, 58, 255), ow=1)
    label(cx + 94, cyy + 24, nm, f_lbl, fill=WHITE)
    label(cx + 94, cyy + 52, hx, f_hex, fill=MUTED)
y += 3 * (ch + 18) + 30

# trim
canvas = canvas.crop((0, 0, WIDTH, y + 30))
canvas.convert("RGB").save(f"{OUT}/preview.png", quality=95)
print("preview.png", canvas.size)
