"""engram brand pack generator."""
import math

# ---------------------------------------------------------------- palette
ACCENT      = "#43e08b"   # terminal mint-green (primary)
ACCENT_DK   = "#179a55"   # darker green for light backgrounds
NODE        = "#e3f3ea"   # near-white mint node fill
BG_TOP      = "#19212d"
BG_BOT      = "#0b0f15"
INK         = "#0d1117"   # near-black (light-bg text / flat tile)
TEXT_DARK   = "#eaf0f5"   # wordmark on dark
CY          = "#5cc7f5"   # cluster: cyan
AM          = "#f5b657"   # cluster: amber
MG          = "#f07cc0"   # cluster: magenta

CANVAS = 512
R_TILE = 104

# ---------------------------------------------------------------- graph geometry (512 space)
# A lowercase 'e' drawn as a force-graph: ring + crossbar, open at lower-right.
# node: (x, y, r)
N = {
    "T":  (264, 150, 19),   # top of the bowl
    "L":  (150, 252, 19),   # left  (junction: bowl + bar + lower bowl)
    "R":  (378, 252, 19),   # right (crossbar terminus / top of mouth)
    "LB": (176, 364, 19),   # lower-left
    "B":  (270, 408, 19),   # bottom
    "BR": (366, 356, 20),   # lower-right  (the 'e' mouth terminus)
}
HUB = (264, 280)             # block-cursor hub (terminal caret) sitting on the crossbar
HUB_W, HUB_H, HUB_RX = 32, 40, 7

SAT = {}                     # icon stays clean: no stray satellites

EDGES = [
    ("R", "T"), ("T", "L"),          # upper bowl
    ("L", "HUB"), ("HUB", "R"),      # crossbar of the 'e' (through the cursor)
    ("L", "LB"), ("LB", "B"), ("B", "BR"),   # lower bowl, opening at the right
]
SAT_EDGES = []

# color-variant cluster tints (emergent communities)
TINT = {"LB": CY, "B": MG, "BR": AM}


def _pt(k):
    if k == "HUB":
        return HUB
    if k in N:
        return N[k][0], N[k][1]
    return SAT[k][0], SAT[k][1]


def _edges_svg(color, opacity, sat_color, sat_op):
    out = []
    for a, b in EDGES:
        (x1, y1), (x2, y2) = _pt(a), _pt(b)
        out.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" '
                   f'stroke="{color}" stroke-width="9" stroke-linecap="round" opacity="{opacity}"/>')
    for a, b in SAT_EDGES:
        (x1, y1), (x2, y2) = _pt(a), _pt(b)
        out.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" '
                   f'stroke="{sat_color}" stroke-width="3.5" stroke-linecap="round" opacity="{sat_op}"/>')
    return "\n".join(out)


def _nodes_svg(color_variant):
    out = []
    # satellites first (behind)
    for k, (x, y, r) in SAT.items():
        out.append(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{ACCENT}" opacity="0.40"/>')
    for k, (x, y, r) in N.items():
        if color_variant:
            fill = TINT.get(k, NODE)
            ring = TINT.get(k, ACCENT)
        else:
            fill = NODE
            ring = ACCENT
        # ring halo
        out.append(f'<circle cx="{x}" cy="{y}" r="{r+4}" fill="{ring}" opacity="0.18"/>')
        out.append(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{fill}"/>')
        out.append(f'<circle cx="{x}" cy="{y}" r="{r}" fill="none" stroke="{ring}" '
                   f'stroke-width="2.5" opacity="0.9"/>')
    return "\n".join(out)


def _hub_svg(glow=True):
    hx, hy = HUB
    x = hx - HUB_W / 2
    y = hy - HUB_H / 2
    out = []
    if glow:
        for gr, op in ((26, 0.10), (17, 0.16), (10, 0.22)):
            out.append(f'<rect x="{hx-HUB_W/2-gr}" y="{hy-HUB_H/2-gr}" '
                       f'width="{HUB_W+2*gr}" height="{HUB_H+2*gr}" rx="{HUB_RX+gr}" '
                       f'fill="{ACCENT}" opacity="{op}"/>')
    out.append(f'<rect x="{x}" y="{y}" width="{HUB_W}" height="{HUB_H}" rx="{HUB_RX}" fill="{ACCENT}"/>')
    out.append(f'<rect x="{x}" y="{y}" width="{HUB_W}" height="{HUB_H}" rx="{HUB_RX}" '
               f'fill="none" stroke="#bff7d8" stroke-width="2" opacity="0.55"/>')
    return "\n".join(out)


def icon_defs(prefix):
    return f'''
    <linearGradient id="{prefix}bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{BG_TOP}"/>
      <stop offset="1" stop-color="{BG_BOT}"/>
    </linearGradient>
    <radialGradient id="{prefix}phos" cx="0.52" cy="0.52" r="0.6">
      <stop offset="0" stop-color="{ACCENT}" stop-opacity="0.16"/>
      <stop offset="1" stop-color="{ACCENT}" stop-opacity="0"/>
    </radialGradient>
    <clipPath id="{prefix}clip"><rect x="0" y="0" width="{CANVAS}" height="{CANVAS}" rx="{R_TILE}"/></clipPath>'''


def icon_body(variant="color", tile=True, prefix=""):
    """Return the inner SVG markup for the icon (no <svg> wrapper)."""
    mono = variant == "mono"
    bg = ""
    if tile:
        if mono:
            bg = f'<rect x="0" y="0" width="{CANVAS}" height="{CANVAS}" rx="{R_TILE}" fill="{INK}"/>'
        else:
            lines = "".join(
                f'<line x1="0" y1="{y}" x2="{CANVAS}" y2="{y}" stroke="#ffffff" stroke-width="1" opacity="0.025"/>'
                for y in range(0, CANVAS, 7))
            bg = (f'<rect x="0" y="0" width="{CANVAS}" height="{CANVAS}" rx="{R_TILE}" fill="url(#{prefix}bg)"/>'
                  f'<g clip-path="url(#{prefix}clip)">{lines}</g>'
                  f'<rect x="0" y="0" width="{CANVAS}" height="{CANVAS}" rx="{R_TILE}" fill="url(#{prefix}phos)"/>'
                  f'<rect x="1.5" y="1.5" width="{CANVAS-3}" height="{CANVAS-3}" rx="{R_TILE-1}" '
                  f'fill="none" stroke="#ffffff" stroke-width="1.5" opacity="0.07"/>')

    if mono:
        edges = _edges_svg(ACCENT, 0.55, ACCENT, 0.4)
        nodes = "\n".join(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{ACCENT}"/>'
                          for k, (x, y, r) in N.items())
        hub = _hub_svg(glow=False)
    else:
        edges = _edges_svg(ACCENT, 0.50, ACCENT, 0.38)
        nodes = _nodes_svg(color_variant=(variant == "color"))
        hub = _hub_svg(glow=True)
    return f'{bg}<g>{edges}{nodes}{hub}</g>'


def icon_svg(variant="color", tile=True, size=CANVAS):
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS} {CANVAS}" width="{size}" height="{size}">
  <defs>{icon_defs("")}</defs>
  {icon_body(variant, tile, "")}
</svg>'''


if __name__ == "__main__":
    import cairosvg, os
    os.makedirs("/home/claude/proof", exist_ok=True)
    for v in ("color", "mono"):
        svg = icon_svg(v)
        open(f"/home/claude/proof/icon-{v}.svg", "w").write(svg)
        cairosvg.svg2png(bytestring=svg.encode(), write_to=f"/home/claude/proof/icon-{v}.png",
                         output_width=512, output_height=512)
    for s in (16, 24, 32, 48, 64):
        cairosvg.svg2png(bytestring=icon_svg("color").encode(),
                         write_to=f"/home/claude/proof/c{s}.png", output_width=s, output_height=s)
        cairosvg.svg2png(bytestring=icon_svg("mono").encode(),
                         write_to=f"/home/claude/proof/m{s}.png", output_width=s, output_height=s)
    # tile a small-size strip for inspection
    from PIL import Image
    strip = Image.new("RGBA", (16+24+32+48+64+5*12, 64+24), (32, 38, 48, 255))
    x = 6
    for s in (16, 24, 32, 48, 64):
        im = Image.open(f"/home/claude/proof/c{s}.png")
        strip.paste(im, (x, (64-s)//2+6), im)
        x += s + 12
    strip.save("/home/claude/proof/strip-color.png")
    strip2 = Image.new("RGBA", (16+24+32+48+64+5*12, 64+24), (32, 38, 48, 255))
    x = 6
    for s in (16, 24, 32, 48, 64):
        im = Image.open(f"/home/claude/proof/m{s}.png")
        strip2.paste(im, (x, (64-s)//2+6), im)
        x += s + 12
    strip2.save("/home/claude/proof/strip-mono.png")
    print("proof rendered")
