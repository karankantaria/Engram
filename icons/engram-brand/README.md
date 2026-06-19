# engram — brand pack

A logo + icon set for **engram**, the terminal-themed second brain.

## The mark

The icon is a lowercase **“e”** drawn as a **force-graph** — the exact thing the
app renders from your notes. At its centre sits a glowing **block cursor**, the
terminal caret, doubling as the brightest node in the cluster. Three ideas in
one glyph: the name (*e*), the product (*self-organising graph*), and the
medium (*terminal*). Peripheral nodes carry the cluster colours that
community-detection assigns in-app.

```
svg/
  engram-icon.svg                 primary app icon (clustered, full colour)
  engram-icon-mono.svg            single-colour icon (flat tile)
  engram-mark-transparent.svg     just the mark, no tile — overlay on dark chrome
  engram-logo-horizontal-dark.svg   icon + wordmark, light text   (use on dark)
  engram-logo-horizontal-light.svg  icon + wordmark, dark text    (use on light)
  engram-logo-stacked-dark.svg
  engram-logo-stacked-light.svg
  engram-wordmark-dark.svg        wordmark only (+ block cursor)
  engram-wordmark-light.svg

png/
  icon/   engram-{16,24,32,48,64,128,256,512,1024}.png   (full colour)
  tray/   engram-tray-{16,24,32,48,64}.png               (monochrome)
  engram-logo-*.png  engram-wordmark-*.png               (transparent bg)

ico/
  engram.ico        16→256, full colour — app & window icon
  engram-tray.ico   16→48, monochrome  — system tray

preview.png         this pack at a glance
```

The wordmark is **JetBrains Mono Bold**, outlined to vector paths — the SVGs
need no font installed to render correctly.

## Wiring it into the app

`.csproj` (sets the exe icon):

```xml
<PropertyGroup>
  <ApplicationIcon>assets\engram.ico</ApplicationIcon>
</PropertyGroup>
```

`MainWindow.xaml` (window chrome icon):

```xml
<Window ... Icon="pack://application:,,,/assets/engram.ico">
```

Tray icon (`System.Windows.Forms.NotifyIcon` or `H.NotifyIcon`) — use the
monochrome build so it stays crisp at 16px on light *and* dark taskbars:

```csharp
notifyIcon.Icon = new Icon("assets/engram-tray.ico", new Size(16, 16));
```

WebView2 favicon for the local `web/index.html`:

```html
<link rel="icon" type="image/png" href="engram-32.png">
```

## Palette

| Token            | Hex       | Use |
|------------------|-----------|-----|
| Accent           | `#43E08B` | edges, cursor, links, primary accent |
| Accent (dark)    | `#179A55` | accent on light backgrounds |
| Node             | `#E3F3EA` | default node fill |
| Ink / tile       | `#0D1117` | flat icon tile, dark text |
| BG top → bottom  | `#19212D` → `#0B0F15` | icon tile gradient / app background |
| Cluster cyan     | `#5CC7F5` | community colour |
| Cluster amber    | `#F5B657` | community colour |
| Cluster magenta  | `#F07CC0` | community colour |

These cluster colours are meant to echo the ones your label-propagation step
paints onto the live graph.

## Regenerating

The whole pack is generated from three scripts (`engram_brand.py`,
`wordmark.py`, `build_pack.py`). Geometry, palette, and sizes are parameters at
the top of `engram_brand.py` — tweak and re-run `build_pack.py` to rebuild
every asset, then `preview.py` for the sheet.
