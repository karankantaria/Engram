"""Build the complete engram brand pack into /mnt/user-data/outputs/engram-brand."""
import os, subprocess, cairosvg
from PIL import Image, ImageDraw, ImageFont
import engram_brand as E
import wordmark as W

OUT = "/mnt/user-data/outputs/engram-brand"
SVG = f"{OUT}/svg"; PNG = f"{OUT}/png"; PNGI = f"{PNG}/icon"; PNGT = f"{PNG}/tray"; ICO = f"{OUT}/ico"
for d in (SVG, PNGI, PNGT, ICO):
    os.makedirs(d, exist_ok=True)

FB = "/home/claude/fonts/JetBrainsMono-Bold.ttf"
FR = "/home/claude/fonts/JetBrainsMono-Regular.ttf"
FM = "/home/claude/fonts/JetBrainsMono-Medium.ttf"


def wsvg(path, svg):
    open(path, "w").write(svg)


def render(svg, path, w, h, bg=None):
    cairosvg.svg2png(bytestring=svg.encode(), write_to=path,
                     output_width=int(round(w)), output_height=int(round(h)), background_color=bg)


# ---------------------------------------------------------------- 1. SVGs
icon_color = E.icon_svg("color")
icon_mono = E.icon_svg("mono")
mark_trans = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {E.CANVAS} {E.CANVAS}" '
              f'width="{E.CANVAS}" height="{E.CANVAS}">{E.icon_body("mono", tile=False, prefix="t_")}</svg>')
wsvg(f"{SVG}/engram-icon.svg", icon_color)
wsvg(f"{SVG}/engram-icon-mono.svg", icon_mono)
wsvg(f"{SVG}/engram-mark-transparent.svg", mark_trans)

lockups = {
    "engram-logo-horizontal-dark":  W.lockup_horizontal("dark"),
    "engram-logo-horizontal-light": W.lockup_horizontal("light", variant="color"),
    "engram-logo-stacked-dark":     W.lockup_stacked("dark"),
    "engram-logo-stacked-light":    W.lockup_stacked("light", variant="color"),
    "engram-wordmark-dark":         W.wordmark_svg("dark"),
    "engram-wordmark-light":        W.wordmark_svg("light"),
}
for name, (svg, _, _) in lockups.items():
    wsvg(f"{SVG}/{name}.svg", svg)

# ---------------------------------------------------------------- 2. icon PNGs
ICON_SIZES = [16, 24, 32, 48, 64, 128, 256, 512, 1024]
for s in ICON_SIZES:
    render(icon_color, f"{PNGI}/engram-{s}.png", s, s)
TRAY_SIZES = [16, 24, 32, 48, 64]
for s in TRAY_SIZES:
    render(icon_mono, f"{PNGT}/engram-tray-{s}.png", s, s)

# ---------------------------------------------------------------- 3. lockup / wordmark PNGs (transparent)
TARGET_W = {"horizontal": 1800, "stacked": 1100, "wordmark": 1400}
for name, (svg, vw, vh) in lockups.items():
    kind = "horizontal" if "horizontal" in name else ("stacked" if "stacked" in name else "wordmark")
    tw = TARGET_W[kind]
    th = tw * vh / vw
    render(svg, f"{PNG}/{name}.png", tw, th)  # transparent bg

# ---------------------------------------------------------------- 4. ICO files
def make_ico(out, pngs):
    subprocess.run(["convert", *pngs, out], check=True)

make_ico(f"{ICO}/engram.ico", [f"{PNGI}/engram-{s}.png" for s in [16, 24, 32, 48, 64, 128, 256]])
make_ico(f"{ICO}/engram-tray.ico", [f"{PNGT}/engram-tray-{s}.png" for s in [16, 24, 32, 48]])

print("assets written")
