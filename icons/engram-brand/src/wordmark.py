"""Wordmark + lockups for engram, with text outlined to vector paths."""
from matplotlib.textpath import TextPath
from matplotlib.font_manager import FontProperties
from matplotlib.path import Path
import engram_brand as E

FONT = "/home/claude/fonts/JetBrainsMono-Bold.ttf"
M, L, C3, C4, CL = Path.MOVETO, Path.LINETO, Path.CURVE3, Path.CURVE4, Path.CLOSEPOLY


def text_path_d(text, size_px, x0, baseline_y, tracking=0.0):
    """Outline text to an SVG path 'd' string. Returns (d, advance_px, capheight_px)."""
    fp = FontProperties(fname=FONT)
    tp = TextPath((0, 0), text, size=size_px, prop=fp)
    # apply simple horizontal tracking by nudging based on x (monospace -> uniform)
    cmds = []
    for verts, code in tp.iter_segments():
        def TX(i):
            return x0 + verts[i]
        def TY(i):
            return baseline_y - verts[i]
        if code == M:
            cmds.append(f"M{TX(0):.2f},{TY(1):.2f}")
        elif code == L:
            cmds.append(f"L{TX(0):.2f},{TY(1):.2f}")
        elif code == C3:
            cmds.append(f"Q{TX(0):.2f},{TY(1):.2f} {TX(2):.2f},{TY(3):.2f}")
        elif code == C4:
            cmds.append(f"C{TX(0):.2f},{TY(1):.2f} {TX(2):.2f},{TY(3):.2f} {TX(4):.2f},{TY(5):.2f}")
        elif code == CL:
            cmds.append("Z")
    ext = tp.get_extents()
    advance = ext.x1  # right edge in px (x0 not yet added)
    capheight = ext.y1
    return "".join(cmds), advance, capheight


def wordmark_svg(mode="dark", pad=40):
    """Standalone wordmark: 'engram' + block-cursor, outlined."""
    text_col = E.TEXT_DARK if mode == "dark" else E.INK
    cur_col = E.ACCENT if mode == "dark" else E.ACCENT_DK
    size = 220
    d, adv, cap = text_path_d("engram", size, pad, pad + cap_for(size))
    cap = cap_for(size)
    # block cursor after the word
    gap = size * 0.16
    cw, ch = size * 0.58, cap * 1.02
    cx = pad + adv + gap
    cy = pad + cap - ch
    W = cx + cw + pad
    H = pad + cap + pad
    bg = ""
    if mode == "dark":
        bg = f'<rect x="0" y="0" width="{W:.0f}" height="{H:.0f}" fill="none"/>'
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W:.0f} {H:.0f}" width="{W:.0f}" height="{H:.0f}">
  {bg}
  <path d="{d}" fill="{text_col}"/>
  <rect x="{cx:.1f}" y="{cy:.1f}" width="{cw:.1f}" height="{ch:.1f}" rx="6" fill="{cur_col}"/>
</svg>''', W, H


def cap_for(size):
    # JetBrains Mono cap height ~ 0.73 em
    return size * 0.73


def lockup_horizontal(mode="dark", variant=None):
    """Icon on the left, wordmark on the right."""
    variant = variant or ("color" if mode == "dark" else "color")
    text_col = E.TEXT_DARK if mode == "dark" else E.INK
    cur_col = E.ACCENT if mode == "dark" else E.ACCENT_DK
    pad = 56
    icon_box = 300
    size = 196
    cap = cap_for(size)
    gap_icon = 56
    wx = pad + icon_box + gap_icon
    # vertically center wordmark cap-block to icon center
    icon_cy = pad + icon_box / 2
    baseline_y = icon_cy + cap / 2
    d, adv, _ = text_path_d("engram", size, wx, baseline_y)
    gap = size * 0.16
    cw, ch = size * 0.58, cap * 1.02
    cx = wx + adv + gap
    cy = baseline_y - cap
    W = cx + cw + pad
    H = pad + icon_box + pad
    prefix = "lh_"
    icon = (f'<g transform="translate({pad},{pad}) scale({icon_box/E.CANVAS:.5f})">'
            f'{E.icon_body(variant, True, prefix)}</g>')
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W:.0f} {H:.0f}" width="{W:.0f}" height="{H:.0f}">
  <defs>{E.icon_defs(prefix)}</defs>
  {icon}
  <path d="{d}" fill="{text_col}"/>
  <rect x="{cx:.1f}" y="{cy:.1f}" width="{cw:.1f}" height="{ch:.1f}" rx="6" fill="{cur_col}"/>
</svg>''', W, H


def lockup_stacked(mode="dark", variant="color"):
    text_col = E.TEXT_DARK if mode == "dark" else E.INK
    cur_col = E.ACCENT if mode == "dark" else E.ACCENT_DK
    pad = 56
    icon_box = 300
    size = 150
    cap = cap_for(size)
    gap_v = 50
    # measure word width to center
    d0, adv, _ = text_path_d("engram", size, 0, 0)
    gap = size * 0.16
    cw = size * 0.58
    word_w = adv + gap + cw
    content_w = max(icon_box, word_w)
    W = pad + content_w + pad
    icon_x = pad + (content_w - icon_box) / 2
    word_x = pad + (content_w - word_w) / 2
    baseline_y = pad + icon_box + gap_v + cap
    d, _, _ = text_path_d("engram", size, word_x, baseline_y)
    cx = word_x + adv + gap
    cy = baseline_y - cap
    ch = cap * 1.02
    H = baseline_y + (cap * 0.02) + pad
    prefix = "ls_"
    icon = (f'<g transform="translate({icon_x:.1f},{pad}) scale({icon_box/E.CANVAS:.5f})">'
            f'{E.icon_body(variant, True, prefix)}</g>')
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W:.0f} {H:.0f}" width="{W:.0f}" height="{H:.0f}">
  <defs>{E.icon_defs(prefix)}</defs>
  {icon}
  <path d="{d}" fill="{text_col}"/>
  <rect x="{cx:.1f}" y="{cy:.1f}" width="{cw:.1f}" height="{ch:.1f}" rx="6" fill="{cur_col}"/>
</svg>''', W, H


if __name__ == "__main__":
    import cairosvg, os
    os.makedirs("/home/claude/proof", exist_ok=True)
    for name, (svg, W, H) in {
        "wordmark-dark": wordmark_svg("dark"),
        "wordmark-light": wordmark_svg("light"),
        "lockup-h-dark": lockup_horizontal("dark"),
        "lockup-stacked-dark": lockup_stacked("dark"),
    }.items():
        open(f"/home/claude/proof/{name}.svg", "w").write(svg)
        cairosvg.svg2png(bytestring=svg.encode(), write_to=f"/home/claude/proof/{name}.png",
                         output_width=int(W), output_height=int(H), background_color=("#0b0f15" if "light" not in name else "#ffffff"))
    print("wordmark/lockups rendered")
