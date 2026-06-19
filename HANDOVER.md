# engram — Handover

> **A terminal-themed "second brain": dump notes, the structure organizes itself.**
> Pick up here in a fresh Claude Code session. Open Claude in this directory:
> `C:\Users\karan\Documents\Programming\engram`

---

## Status — v1 BUILT ✅ (updated 2026-06-19)

The full v1 vertical slice is implemented, builds clean, and is verified
end-to-end. See `README.md` for build/run. Quick start:

```powershell
powershell -File scripts/fetch-model.ps1   # one-time: ~90MB model (gitignored)
cd src; dotnet run -c Debug
```

**What's done:** WPF + WebView2 terminal shell · SQLite + markdown storage ·
local ONNX `all-MiniLM-L6-v2` embeddings (verified: semantic search returns the
right notes, clusters separate accountancy / AI / work) · cosine auto-linking ·
label-propagation clusters · force-graph view · semantic search · auto-titling ·
on-demand Claude librarian (cluster naming + link/merge suggestions) · review
panel · export-to-zip · global hotkey Ctrl+Alt+Space · tray. Headless smoke test
passes (`engram.exe --selftest`).

**Decisions resolved this build:** data in `%APPDATA%\engram` + in-app export ·
WPF host · hotkey **Ctrl+Alt+Space** · name kept **engram**.

**Not yet done / next:** validate `dotnet publish` single-file packaging on this
machine; live-test the librarian against the real `claude` CLI (code path
verified, no model call made yet); tune the 0.33 similarity threshold against
real notes. Deferred add-ons unchanged (see bottom).

### Original resume steps (kept for reference)

1. Open a terminal in `C:\Users\karan\Documents\Programming\engram`
2. Run `claude`
3. Say: **"Read HANDOVER.md and continue building engram."**

---

## What engram is

A standalone Windows app that acts as a frictionless second brain. The user is self-described as "awful at organization," so **organization must be emergent, not manual** — you dump raw notes and the app links, clusters, titles, and visualizes them automatically.

**Core daily loop:** capture in a terminal-style bar → embeddings auto-link by meaning → community detection forms emergent categories → graph view (Obsidian-style) is your map → Claude labels clusters & suggests links on demand.

### Why it fits this user
- Lives in the terminal → UI must look/feel like a terminal (monospace, dark, ASCII chrome).
- Already built a disguised EPUB reader → values lightweight, camouflaged, glanceable tools. **Avoid bloat.**
- Does 2 evening courses (accountancy + AI business strategy) → notes naturally cluster into work / accountancy / AI-strategy; resurfacing doubles as light spaced-repetition.
- Replaces "random notes on notepad."

---

## Locked-in decisions

| Decision | Choice | Notes |
|---|---|---|
| Form factor | **All-in-one desktop window**, terminal-themed | NOT a separate browser tab for the graph — graph lives *inside* the app |
| Packaging | **.NET 8 self-contained single-file `.exe`** (win-x64) | No install for end user |
| UI host | **WebView2** | Runtime already present on the machine (v149.x) |
| Graph rendering | **`force-graph`** JS lib (Obsidian-style force-directed) | Rendered inside the WebView2 |
| Embeddings | **Local, offline** — ONNX Runtime + `all-MiniLM-L6-v2` | Bundled in exe (~90MB). Free per note, private, no internet |
| Organization style | **Automatic + review** | Auto-does everything; surfaces a dismissible "suggested links / rename cluster?" panel. Never mandatory work |
| Claude's role | **Librarian, on-demand only** | Names clusters, suggests links/merges. NOT called per-note. Shells out to `claude` CLI |
| Storage | Markdown files on disk + **SQLite** index (incl. vectors) | Greppable, portable, future-proof |

---

## Toolchain status (checked 2026-06-19)

- ✅ **.NET SDK — installed** (8.0.422, via winget) at `C:\Program Files\dotnet`.
- ✅ WebView2 runtime present (149.0.4022.69)
- ✅ Node: `C:\Program Files\nodejs\node.exe`
- ✅ git: `C:\Program Files\Git\cmd\git.exe`
- ✅ claude CLI: `C:\Users\karan\.local\bin\claude.exe`

---

## Build plan

### Step 0 — Install .NET 8 SDK
- `winget install Microsoft.DotNet.SDK.8` (then restart shell / re-check `dotnet --version`).
- Confirm `dotnet --list-sdks` shows an 8.x SDK before proceeding.

### Step 1 — Scaffold (first vertical slice)
Goal: a working **capture → embed → graph** loop, nothing fancy.
- `dotnet new` WinForms or WPF host (host just owns the WebView2 + window chrome + global hotkey/tray).
- Add WebView2 (`Microsoft.Web.WebView2`) and ONNX Runtime (`Microsoft.ML.OnnxRuntime`) packages.
- Web frontend (HTML/CSS/JS) embedded as app assets: terminal theme + `force-graph`.
- SQLite via `Microsoft.Data.Sqlite`.
- Bundle `all-MiniLM-L6-v2` ONNX model + tokenizer as embedded/content files.

### Step 2 — Data layer
- Notes = markdown files in a `notes/` data dir (under `%APPDATA%\engram` or a portable folder next to the exe — decide with user).
- SQLite schema (draft):
  - `notes(id, title, path, created_at, updated_at, body)`
  - `embeddings(note_id, vector BLOB, dim)`
  - `edges(a_id, b_id, weight)` — derived from cosine similarity above threshold
  - `clusters(id, name, color)` + `note_clusters(note_id, cluster_id)`
  - `suggestions(id, kind, payload, status)` — for the review panel
- Auto-title via embeddings/first line in v1; Claude polish later.

### Step 3 — Auto-organization pipeline (background)
1. On new note: embed → compute cosine similarity to existing → write edges above threshold.
2. Community detection over the edge graph → assign clusters (emergent categories).
3. On demand (`engram reindex` / button): call `claude` CLI to name clusters + propose links/merges → write to `suggestions`.

### Step 4 — UI
- **Capture bar:** slim top prompt; `Enter` saves + dismisses. Summoned via global hotkey (Win32 interop).
- **Brain view:** force-graph left (clusters = color regions, similarity = edges), note reader/editor right.
- **Review panel:** dismissible list of suggestions; accept/ignore.
- **Semantic search box** (free once embeddings exist — include in v1).

### Step 5 — Package
- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.
- Expect ~110–130MB exe (ONNX model is the bulk — acceptable; it's the price of offline embeddings).

---

## v1 scope vs. deferred

**In v1:** capture bar, local embeddings + auto-linking, community-detection clusters, graph view, semantic search, auto-titling, on-demand Claude cluster-naming, review panel.

**Deferred (clean add-ons, don't affect core design):**
- Resurfacing / spaced-repetition surfacing of old notes
- Orphan rescue (flag notes connected to nothing)
- Cross-machine sync
- Encryption at rest
- Integration with the user's EPUB reader / VSCode

---

## Open questions for the user (ask when resuming)

1. Data location: portable (folder next to exe) or `%APPDATA%\engram`?
2. Preferred global hotkey to summon the capture bar?
3. WPF vs WinForms for the host shell (either is fine; WinForms is lighter for a single WebView2 window).
4. Keep the name **engram**? (alternates: mneme, cortex, synapse, recall)

---

## Naming
- App: **engram** — neuroscience term for a physical memory trace. CLI/exe: `engram` / `engram.exe`.
