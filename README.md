# engram

> A terminal-themed "second brain": dump notes, the structure organizes itself.

engram is a standalone Windows desktop app. You capture raw thoughts in a
terminal-style bar; the app embeds them locally, auto-links them by meaning,
forms emergent clusters via community detection, and draws an Obsidian-style
force-graph of your mind. Claude acts as an on-demand *librarian* — naming
clusters and suggesting links/merges — but is never called per note.

Everything (embeddings included) runs **locally and offline**. No note ever
leaves your machine unless you press the librarian button (which shells out to
the `claude` CLI) or export.

## Stack

| Layer | Choice |
|---|---|
| Host shell | .NET 8 **WPF**, borderless terminal chrome |
| UI | **WebView2** rendering local HTML/CSS/JS |
| Graph | [`force-graph`](https://github.com/vasturiano/force-graph) (canvas, force-directed) |
| Embeddings | **ONNX Runtime** + `all-MiniLM-L6-v2` (384-dim), fully local |
| Storage | Markdown files on disk + **SQLite** index (vectors, edges, clusters) |
| Librarian | on-demand `claude` CLI call |

Data lives in `%APPDATA%\engram` (notes as markdown + `engram.db`). Use the
in-app **export** button to zip it up and move machines.

## Build & run

```powershell
# 1. one-time: fetch the embedding model (~90MB, gitignored)
powershell -File scripts/fetch-model.ps1

# 2. build + run
cd src
dotnet run -c Debug
```

Requirements: .NET 8 SDK, the WebView2 runtime (preinstalled on Win11), and —
only for the librarian feature — the `claude` CLI on PATH or at
`~/.local/bin/claude.exe`.

### Smoke test (headless)

```powershell
cd src
dotnet build -c Debug
./bin/Debug/net8.0-windows/engram.exe --selftest   # writes selftest.out
```

Exercises capture → embed → link → cluster → search in a throwaway temp dir.

### Package (single-file exe)

```powershell
cd src
dotnet publish -c Release -p:PublishSingleFile=true
```

Produces a self-contained `win-x64` exe. The `models\` folder ships alongside it.

## How it works

1. **Capture** — note text is written to `notes/{id}.md` and indexed in SQLite.
2. **Embed** — `OnnxEmbedder` tokenizes (WordPiece) and mean-pools MiniLM into a
   normalized 384-d vector. (Falls back to a deterministic hash embedding if the
   model isn't present, so the app still runs.)
3. **Link** — cosine similarity above ~0.33 creates edges (top-8 per note).
4. **Cluster** — label-propagation community detection assigns colored clusters.
5. **Librarian** (on demand) — Claude names clusters and proposes links/merges,
   which land in the dismissible review panel.

## Keyboard

- **Ctrl+Alt+Space** — global hotkey: summon the window + focus the capture bar.
- **Enter** — save the note · **Shift+Enter** — newline.
- Closing the window hides to the tray; right-click the tray icon → Exit to quit.

## Project layout

```
src/
  App.xaml(.cs)         single-instance + startup
  MainWindow.xaml(.cs)  WPF chrome + WebView2 host + JS<->C# RPC bridge
  Core/
    Paths.cs            %APPDATA% locations
    AssetManager.cs     extracts embedded web/ assets at runtime
    Database.cs         SQLite schema + queries
    Models.cs           DTOs shared with the frontend
    Embedder.cs         ONNX + WordPiece tokenizer + hash fallback
    NoteService.cs      capture/edit/delete, edges, clusters, search, export
    Clustering.cs       label-propagation communities
    Librarian.cs        on-demand claude CLI integration
    SelfTest.cs         headless pipeline smoke test
  Interop/GlobalHotkey.cs
  web/                  index.html, styles.css, app.js, vendor/force-graph
  models/               model.onnx + vocab.txt (gitignored; see fetch script)
```
