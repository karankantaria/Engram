"use strict";

// ---- RPC bridge to the C# host ----------------------------------------
const pending = {};
let seq = 0;

function rpc(action, payload) {
  return new Promise((resolve, reject) => {
    const id = "r" + seq++;
    pending[id] = { resolve, reject };
    window.chrome.webview.postMessage({ id, action, payload: payload || {} });
  });
}

window.chrome.webview.addEventListener("message", (ev) => {
  const m = ev.data;
  if (m.event) { onEvent(m.event, m.payload); return; }
  const p = pending[m.id];
  if (!p) return;
  delete pending[m.id];
  if (m.ok) p.resolve(m.result);
  else p.reject(new Error(m.error || "error"));
});

function onEvent(name, payload) {
  if (name === "focusCapture") {
    const c = document.getElementById("capture");
    c.focus();
    c.select();
  } else if (name === "importing") {
    toast("importing files…");
  } else if (name === "imported") {
    applyImport(payload);
  }
}

function applyImport(payload) {
  if (!payload) return;
  if (payload.graph) {
    renderGraph(payload.graph);
    renderClustersPanel(payload.graph.clusters);
    refreshTodos();
  }
  const r = payload.result;
  if (r) toast(`imported ${r.notes} notes from ${r.files} files` + (r.skipped ? ` · ${r.skipped} skipped` : ""));
}

// ---- elements ---------------------------------------------------------
const $ = (id) => document.getElementById(id);
const capture = $("capture");
const searchInput = $("search");
let Graph = null;
let currentNoteId = null;
let lastGraph = { nodes: [], links: [], clusters: [] };

// ---- graph mode: 'idle' (ambient neural firing) vs 'browse' (calm/interactive)
const IDLE_AFTER_MS = 7000;
let mode = "idle";
let pulse = 1;            // eased 1=idle … 0=browse: drives glow, float, particle fade
let lastActivity = 0;
let settled = false;     // true once the initial layout has cooled (we own positions after)
let draggingNode = null;
const highlightNodes = new Set();
const highlightLinks = new Set();

function markActivity() {
  lastActivity = performance.now();
  if (mode !== "browse") setMode("browse");
}

function setMode(m) {
  if (m === mode) return;
  mode = m;
  if (mode === "idle") { highlightNodes.clear(); highlightLinks.clear(); }
  if (Graph) Graph.linkDirectionalParticles(particleAccessor); // refresh emitters
}

// idle: signals fire along every synapse; browse: only along the focused node's
function particleAccessor(l) {
  if (mode === "idle") {
    const w = l.weight || 0;
    return w > 0.6 ? 3 : w > 0.45 ? 2 : 1;
  }
  return highlightLinks.has(l) ? 2 : 0;
}

function hexA(hex, a) {
  const h = (hex || "#5C6370").replace("#", "");
  const r = parseInt(h.substring(0, 2), 16);
  const g = parseInt(h.substring(2, 4), 16);
  const b = parseInt(h.substring(4, 6), 16);
  return `rgba(${r},${g},${b},${a})`;
}

// One rAF loop: eases the ambient factor, gently floats the nodes when idle
// (easing them back to their settled home as it fades), and returns to idle
// after a quiet spell. force-graph renders continuously (resumeAnimation), so
// these changes are drawn every frame — no freeze on transition.
function animateMode() {
  const now = performance.now();
  const target = mode === "idle" ? 1 : 0;
  pulse += (target - pulse) * 0.05;
  if (mode === "browse" && now - lastActivity > IDLE_AFTER_MS) setMode("idle");

  if (Graph && settled && pulse > 0.004) {
    const t = now / 1000;
    const amp = 11 * pulse; // float radius in graph units, fades with ambient
    for (const n of Graph.graphData().nodes) {
      if (n === draggingNode || n.bx == null) continue;
      n.x = n.bx + Math.sin(t * n.f1 + n.ph) * amp;
      n.y = n.by + Math.cos(t * n.f2 + n.ph * 1.3) * amp;
    }
  }
  requestAnimationFrame(animateMode);
}

// Capture each node's settled "home" + a unique drift phase/frequency.
function captureHome() {
  for (const n of Graph.graphData().nodes) {
    n.bx = n.x;
    n.by = n.y;
    if (n.ph == null) {
      n.ph = Math.random() * Math.PI * 2;
      n.f1 = 0.25 + Math.random() * 0.35;
      n.f2 = 0.25 + Math.random() * 0.35;
    }
  }
  settled = true;
}

// Node radius scales with the note's content size. Square-root scaling so the
// node *area* tracks data volume (4x the data ≈ 2x the radius), clamped.
function nodeRadius(node) {
  const len = node.size || 0;
  return Math.max(3, Math.min(16, 3 + Math.sqrt(len) / 4));
}

// ---- toast ------------------------------------------------------------
let toastTimer = null;
function toast(msg) {
  const t = $("toast");
  t.textContent = msg;
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove("show"), 2600);
}

// ---- graph ------------------------------------------------------------
function initGraph() {
  const el = document.getElementById("graph");
  Graph = ForceGraph()(el)
    .backgroundColor("#0B0E14")
    .nodeId("id")
    .nodeRelSize(5)
    .nodeColor((n) => n.color)
    .cooldownTime(4000)
    .linkColor((l) => {
      if (mode === "browse" && highlightLinks.size)
        return highlightLinks.has(l) ? "rgba(92,207,230,0.55)" : "rgba(92,103,112,0.07)";
      return "rgba(92,103,112,0.28)";
    })
    .linkWidth((l) => (highlightLinks.has(l) ? 2.2 : 0.5 + (l.weight || 0) * 1.5))
    .linkDirectionalParticles(particleAccessor)
    .linkDirectionalParticleSpeed((l) => 0.003 + (l.weight || 0) * 0.006)
    .linkDirectionalParticleWidth(2)
    .linkDirectionalParticleColor(() => "rgba(92,207,230,0.9)")
    .nodeCanvasObjectMode(() => "replace")
    .nodeCanvasObject((node, ctx, scale) => {
      const now = performance.now();
      const r = nodeRadius(node);
      const dim = mode === "browse" && highlightNodes.size && !highlightNodes.has(node.id);

      // Ambient pulse glow — asynchronous per node for a "firing" feel.
      if (pulse > 0.02 && !dim) {
        const phase = (Number(node.id) % 100) * 0.7;
        const beat = 0.5 + 0.5 * Math.sin(now / 620 + phase);
        const glowR = r * (2.0 + 1.6 * beat);
        const a = (0.18 + 0.34 * beat) * pulse;
        const grad = ctx.createRadialGradient(node.x, node.y, 0, node.x, node.y, glowR);
        grad.addColorStop(0, hexA(node.color, a));
        grad.addColorStop(1, hexA(node.color, 0));
        ctx.fillStyle = grad;
        ctx.beginPath();
        ctx.arc(node.x, node.y, glowR, 0, 2 * Math.PI);
        ctx.fill();
      }

      // Node core.
      const hl = highlightNodes.has(node.id);
      ctx.globalAlpha = dim ? 0.25 : 1;
      ctx.fillStyle = node.color;
      ctx.beginPath();
      ctx.arc(node.x, node.y, hl ? r * 1.4 : r, 0, 2 * Math.PI);
      ctx.fill();
      if (node.id === currentNoteId || hl) {
        ctx.strokeStyle = node.id === currentNoteId ? "#FFFFFF" : "rgba(255,255,255,0.6)";
        ctx.lineWidth = 1.4 / scale;
        ctx.stroke();
      }
      ctx.globalAlpha = 1;

      // Labels: when zoomed in, or for the focused node/neighbors while browsing.
      if (!dim && (scale > 1.3 || (mode === "browse" && hl))) {
        const label = node.title || "";
        ctx.font = `${10 / scale}px Consolas, monospace`;
        ctx.fillStyle = "#BFC7D5";
        ctx.textAlign = "center";
        ctx.textBaseline = "top";
        ctx.fillText(label.slice(0, 28), node.x, node.y + r + 2);
      }
    })
    .nodePointerAreaPaint((node, color, ctx) => {
      // Keep hover/click hit-area matching the drawn (data-scaled) radius.
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.arc(node.x, node.y, nodeRadius(node) + 2, 0, 2 * Math.PI);
      ctx.fill();
    })
    .onNodeHover((node) => {
      markActivity();
      highlightNodes.clear();
      highlightLinks.clear();
      if (node) {
        highlightNodes.add(node.id);
        Graph.graphData().links.forEach((l) => {
          const s = l.source.id ?? l.source;
          const t = l.target.id ?? l.target;
          if (s === node.id || t === node.id) {
            highlightLinks.add(l);
            highlightNodes.add(s);
            highlightNodes.add(t);
          }
        });
      }
      Graph.linkDirectionalParticles(particleAccessor);
    })
    .onNodeClick((node) => {
      markActivity();
      openNote(node.id);
      Graph.centerAt(node.x, node.y, 600);
    })
    .onNodeDrag((node) => { markActivity(); draggingNode = node; })
    .onNodeDragEnd((node) => { node.bx = node.x; node.by = node.y; draggingNode = null; })
    .onZoom(markActivity)
    .onEngineStop(captureHome) // initial layout cooled → we own positions (floating)
    .onBackgroundClick(() => {
      markActivity();
      highlightNodes.clear();
      highlightLinks.clear();
      Graph.linkDirectionalParticles(particleAccessor);
    });

  Graph.cooldownTime(2500);     // settle the layout fairly quickly, then float
  Graph.resumeAnimation();      // keep the render loop running so fades never freeze

  // Treat graph interaction as activity (drives idle ↔ browse).
  el.addEventListener("pointermove", markActivity);
  el.addEventListener("wheel", markActivity, { passive: true });
  el.addEventListener("pointerdown", markActivity);

  window.addEventListener("resize", sizeGraph);
  sizeGraph();
  lastActivity = performance.now();
  requestAnimationFrame(animateMode);
}

function sizeGraph() {
  const pane = $("graphpane");
  if (Graph) Graph.width(pane.clientWidth).height(pane.clientHeight);
}

function renderGraph(g) {
  lastGraph = g;
  // Stale highlights reference old link objects after a data swap.
  highlightNodes.clear();
  highlightLinks.clear();
  // New data → let the engine re-lay-out, then re-home on the next stop.
  settled = false;
  draggingNode = null;
  // force-graph mutates link source/target into objects; clone for safety.
  Graph.graphData({
    nodes: g.nodes.map((n) => ({ ...n })),
    links: g.links.map((l) => ({ source: l.source, target: l.target, weight: l.weight })),
  });
  Graph.resumeAnimation();
  renderLegend(g.clusters);
  $("statusline").textContent =
    `${g.nodes.length} notes · ${g.links.length} links · ${g.clusters.length} clusters`;
}

function renderLegend(clusters) {
  const el = $("legend");
  el.innerHTML = "";
  clusters
    .filter((c) => c.count > 0)
    .forEach((c) => {
      const row = document.createElement("div");
      row.className = "cluster-item";
      row.innerHTML =
        `<span class="swatch" style="background:${c.color}"></span>` +
        `<span class="cluster-name">${esc(c.name)}</span>` +
        `<span class="cluster-count">${c.count}</span>`;
      row.onclick = () => openClusterDetail(c);
      el.appendChild(row);
    });
}

async function renameCluster(c) {
  const name = window.prompt("Rename cluster:", c.name);
  if (name && name.trim() && name !== c.name) {
    const r = await rpc("renameCluster", { id: c.id, name: name.trim() });
    renderGraph(r.graph);
    renderClustersPanel(r.graph.clusters);
    const updated = r.graph.clusters.find((x) => x.id === c.id);
    if (updated) openClusterDetail(updated);
  }
}

function openClusterDetail(c) {
  const el = $("clusterview");
  el.classList.remove("hidden");
  const members = lastGraph.nodes.filter((n) => n.cluster === c.id);
  let html =
    `<div class="panel-title">cluster <span class="mini-link" id="cv-close">close ✕</span></div>` +
    `<div class="cv-head"><span class="swatch" style="background:${c.color}"></span>` +
    `<span class="cv-name">${esc(c.name)}</span>` +
    `<span class="mini-link" id="cv-rename">rename</span></div>` +
    (c.summary
      ? `<div class="cv-summary">${esc(c.summary)}</div>`
      : `<div class="empty">no summary yet — run ⟳ librarian to generate one</div>`) +
    `<div class="cv-notes-title">${members.length} notes</div>`;
  members.forEach((m) => {
    html += `<div class="row cv-note" data-id="${m.id}"><div class="row-title" style="color:${m.color}">${esc(m.title)}</div></div>`;
  });
  el.innerHTML = html;
  $("cv-close").onclick = () => el.classList.add("hidden");
  $("cv-rename").onclick = () => renameCluster(c);
  el.querySelectorAll(".cv-note").forEach((node) => {
    node.onclick = () => {
      const id = Number(node.getAttribute("data-id"));
      openNote(id);
      focusNode(id);
    };
  });
}

// ---- clusters panel (mirror of legend, in sidebar) --------------------
function renderClustersPanel(clusters) {
  const el = $("clusters");
  const active = clusters.filter((c) => c.count > 0);
  el.innerHTML = `<div class="panel-title">clusters</div>`;
  if (active.length === 0) {
    el.innerHTML += `<div class="empty">no clusters yet</div>`;
    return;
  }
  active.forEach((c) => {
    const row = document.createElement("div");
    row.className = "cluster-item";
    row.innerHTML =
      `<span class="swatch" style="background:${c.color}"></span>` +
      `<span class="cluster-name">${esc(c.name)}</span>` +
      `<span class="cluster-count">${c.count}</span>`;
    row.onclick = () => openClusterDetail(c);
    el.appendChild(row);
  });
}

// ---- capture ----------------------------------------------------------
capture.addEventListener("input", () => {
  capture.style.height = "auto";
  capture.style.height = Math.min(capture.scrollHeight, 160) + "px";
});

capture.addEventListener("keydown", async (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    const text = capture.value.trim();
    if (!text) return;
    capture.value = "";
    capture.style.height = "auto";
    try {
      const r = await rpc("capture", { text });
      renderGraph(r.graph);
      renderClustersPanel(r.graph.clusters);
      refreshTodos();
      toast("captured · " + (r.note ? r.note.title : ""));
    } catch (err) {
      toast("error: " + err.message);
    }
  }
});

// ---- reader -----------------------------------------------------------
function renderMarkdown(text) {
  try { return marked.parse(text || ""); }
  catch { return esc(text || ""); }
}

function setReaderMode(editing) {
  $("reader-body").classList.toggle("hidden", !editing);
  $("reader-rendered").classList.toggle("hidden", editing);
  $("btn-edit").textContent = editing ? "preview" : "edit";
}

async function openNote(id) {
  const note = await rpc("note", { id });
  if (!note) return;
  currentNoteId = id;
  $("reader").classList.remove("hidden");
  $("reader-title").textContent = note.title;
  $("reader-meta").textContent = "created " + fmtDate(note.createdAt) +
    (note.updatedAt !== note.createdAt ? " · edited " + fmtDate(note.updatedAt) : "");
  $("reader-body").value = note.body;
  $("reader-rendered").innerHTML = renderMarkdown(note.body);
  setReaderMode(false); // default to rendered view
  if (Graph) Graph.nodeColor((n) => n.color); // trigger redraw of highlight
}

$("btn-edit").onclick = () => {
  const editing = $("reader-body").classList.contains("hidden"); // currently rendered → go edit
  setReaderMode(editing);
  if (editing) $("reader-body").focus();
};

$("btn-save").onclick = async () => {
  if (currentNoteId === null) return;
  const text = $("reader-body").value;
  const r = await rpc("update", { id: currentNoteId, text });
  renderGraph(r.graph);
  renderClustersPanel(r.graph.clusters);
  if (r.note) { $("reader-title").textContent = r.note.title; }
  $("reader-rendered").innerHTML = renderMarkdown(text);
  setReaderMode(false);
  refreshTodos();
  toast("saved");
};

$("btn-delete").onclick = async () => {
  if (currentNoteId === null) return;
  if (!window.confirm("Delete this note?")) return;
  const r = await rpc("delete", { id: currentNoteId });
  currentNoteId = null;
  $("reader").classList.add("hidden");
  renderGraph(r.graph);
  renderClustersPanel(r.graph.clusters);
  refreshTodos();
  toast("deleted");
};

$("btn-close").onclick = () => {
  $("reader").classList.add("hidden");
  currentNoteId = null;
};

// ---- search -----------------------------------------------------------
let searchTimer = null;
searchInput.addEventListener("input", () => {
  clearTimeout(searchTimer);
  const q = searchInput.value.trim();
  if (!q) { $("searchresults").classList.add("hidden"); return; }
  searchTimer = setTimeout(async () => {
    const hits = await rpc("search", { query: q });
    renderSearch(hits);
  }, 220);
});

// Enter in the search box → ask Claude a question grounded in the notes.
searchInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") {
    e.preventDefault();
    const q = searchInput.value.trim();
    if (q) ask(q);
  }
});

let asking = false;
async function ask(question) {
  if (asking) return;
  asking = true;
  const el = $("answer");
  el.classList.remove("hidden");
  el.innerHTML = `<div class="panel-title">ask</div><div class="thinking">thinking… (asking claude)</div>`;
  try {
    const a = await rpc("ask", { question });
    renderAnswer(question, a);
  } catch (err) {
    el.innerHTML = `<div class="panel-title">ask</div><div class="empty">error: ${esc(err.message)}</div>`;
  } finally {
    asking = false;
  }
}

function renderAnswer(question, a) {
  const el = $("answer");
  if (a.error) {
    el.innerHTML = `<div class="panel-title">ask</div><div class="empty">${esc(a.error)}</div>`;
    return;
  }
  // Linkify [id] citations into clickable references.
  const text = esc(a.text).replace(/\[(\d+)\]/g, '<a class="cite" data-id="$1">[$1]</a>');
  let html =
    `<div class="panel-title">ask</div>` +
    `<div class="ask-q">${esc(question)}</div>` +
    `<div class="ask-a">${text}</div>`;
  if (a.sources && a.sources.length) {
    html += `<div class="ask-src-title">sources</div>`;
    a.sources.forEach((s) => {
      html +=
        `<div class="ask-src" data-id="${s.id}">` +
        `<span class="swatch" style="background:${s.color}"></span>` +
        `<span class="ask-src-name">${esc(s.title)}</span>` +
        `<span class="score">${(s.score * 100).toFixed(0)}%</span></div>`;
    });
  }
  el.innerHTML = html;
  el.querySelectorAll(".cite, .ask-src").forEach((node) => {
    node.onclick = () => {
      const id = Number(node.getAttribute("data-id"));
      openNote(id);
      focusNode(id);
    };
  });
}

function renderSearch(hits) {
  const el = $("searchresults");
  el.classList.remove("hidden");
  el.innerHTML = `<div class="panel-title">search</div>`;
  if (!hits || hits.length === 0) {
    el.innerHTML += `<div class="empty">no matches</div>`;
    return;
  }
  hits.forEach((h) => {
    const row = document.createElement("div");
    row.className = "row";
    row.innerHTML =
      `<span class="score">${(h.score * 100).toFixed(0)}%</span>` +
      `<div class="row-title" style="color:${h.color}">${esc(h.title)}</div>` +
      `<div class="row-sub">${esc(h.snippet)}</div>`;
    row.onclick = () => {
      openNote(h.id);
      focusNode(h.id);
    };
    el.appendChild(row);
  });
}

function focusNode(id) {
  const node = Graph.graphData().nodes.find((n) => n.id === id);
  if (node && node.x !== undefined) Graph.centerAt(node.x, node.y, 600);
}

// ---- librarian + export ----------------------------------------------
$("btn-ask").onclick = () => {
  const q = searchInput.value.trim();
  if (q) ask(q);
  else { searchInput.focus(); toast("type a question first"); }
};

$("btn-librarian").onclick = async () => {
  toast("librarian thinking… (calling claude)");
  try {
    const r = await rpc("librarian");
    renderGraph(r.graph);
    renderClustersPanel(r.graph.clusters);
    renderSuggestions(r.suggestions);
    const res = r.result;
    if (res.error) toast("librarian: " + res.error);
    else toast(`librarian · named ${res.named} · ${res.links} links · ${res.merges} merges`);
  } catch (err) {
    toast("librarian error: " + err.message);
  }
};

$("btn-import").onclick = async () => {
  const r = await rpc("import");
  applyImport(r);
};

$("btn-export").onclick = async () => {
  const r = await rpc("export");
  if (r && r.path) toast("exported → " + r.path);
};

// ---- todos ------------------------------------------------------------
function renderTodos(todos) {
  const el = $("todos");
  const open = todos.filter((t) => !t.done).length;
  let html = `<div class="panel-title">tasks ${open ? `· ${open} open ` : ""}<span class="mini-link" id="todo-scan">scan with claude</span></div>`;

  if (todos.length === 0) {
    el.innerHTML = html + `<div class="empty">no tasks — add "- [ ] something" to a note, or scan</div>`;
    $("todo-scan").onclick = scanTodos;
    return;
  }

  // group by cluster; 'unsorted' (id 0) sinks to the bottom
  const groups = {};
  todos.forEach((t) => {
    (groups[t.cluster] ||= { name: t.clusterName, color: t.color, items: [] }).items.push(t);
  });
  Object.keys(groups)
    .sort((a, b) => (a === "0") - (b === "0") || groups[a].name.localeCompare(groups[b].name))
    .forEach((cid) => {
      const g = groups[cid];
      html += `<div class="todo-group"><span class="swatch" style="background:${g.color}"></span>${esc(g.name)}</div>`;
      g.items.sort((a, b) => (a.done - b.done)).forEach((t) => {
        html +=
          `<label class="todo ${t.done ? "done" : ""}">` +
          `<input type="checkbox" data-id="${t.id}" ${t.done ? "checked" : ""}>` +
          `<span class="todo-text">${esc(t.text)}</span>` +
          `<span class="todo-src" data-note="${t.noteId}" title="${esc(t.noteTitle)}">⤴</span>` +
          `</label>`;
      });
    });
  el.innerHTML = html;

  $("todo-scan").onclick = scanTodos;
  el.querySelectorAll(".todo input[type=checkbox]").forEach((box) => {
    box.onchange = async () => {
      const r = await rpc("toggleTodo", { id: box.getAttribute("data-id"), done: box.checked });
      renderTodos(r);
    };
  });
  el.querySelectorAll(".todo-src").forEach((s) => {
    s.onclick = (e) => {
      e.preventDefault();
      const id = Number(s.getAttribute("data-note"));
      openNote(id);
      focusNode(id);
    };
  });
}

async function scanTodos() {
  toast("scanning notes for tasks… (claude)");
  try {
    const r = await rpc("extractTodos");
    renderTodos(r.todos);
    if (r.result && r.result.error) toast("scan: " + r.result.error);
    else toast(`found ${r.result ? r.result.added : 0} new tasks`);
  } catch (err) {
    toast("scan error: " + err.message);
  }
}

async function refreshTodos() {
  try { renderTodos(await rpc("todos")); } catch {}
}

// ---- suggestions ------------------------------------------------------
function renderSuggestions(suggestions) {
  const el = $("suggestions");
  el.innerHTML = `<div class="panel-title">review · suggestions</div>`;
  if (!suggestions || suggestions.length === 0) {
    el.innerHTML += `<div class="empty">nothing to review</div>`;
    return;
  }
  const byId = {};
  lastGraph.nodes.forEach((n) => (byId[n.id] = n.title));
  suggestions.forEach((s) => {
    let body = "";
    try {
      const p = JSON.parse(s.payload);
      if (s.kind === "link")
        body = `link <b>${esc(byId[p.a] || p.a)}</b> ↔ <b>${esc(byId[p.b] || p.b)}</b><br><span class="row-sub">${esc(p.reason || "")}</span>`;
      else if (s.kind === "merge")
        body = `merge <b>${esc(byId[p.from] || p.from)}</b> → <b>${esc(byId[p.to] || p.to)}</b><br><span class="row-sub">${esc(p.reason || "")}</span>`;
      else body = esc(s.payload);
    } catch {
      body = esc(s.payload);
    }
    const div = document.createElement("div");
    div.className = "sug";
    div.innerHTML =
      `<div class="sug-kind">${esc(s.kind)}</div>` +
      `<div class="sug-body">${body}</div>` +
      `<div class="sug-actions">` +
      `<button data-accept="1">accept</button>` +
      `<button data-accept="0">dismiss</button></div>`;
    div.querySelector("[data-accept='1']").onclick = () => resolveSug(s.id, true);
    div.querySelector("[data-accept='0']").onclick = () => resolveSug(s.id, false);
    el.appendChild(div);
  });
}

async function resolveSug(id, accept) {
  const r = await rpc("resolveSuggestion", { id, accept });
  renderGraph(r.graph);
  renderClustersPanel(r.graph.clusters);
  renderSuggestions(r.suggestions);
  toast(accept ? "accepted" : "dismissed");
}

// ---- helpers ----------------------------------------------------------
function esc(s) {
  return String(s == null ? "" : s)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
function fmtDate(iso) {
  try { return new Date(iso).toLocaleString(); } catch { return iso; }
}

// ---- boot -------------------------------------------------------------
(async function boot() {
  initGraph();
  const r = await rpc("init");
  $("backend").textContent = r.backend;
  renderGraph(r.graph);
  renderClustersPanel(r.graph.clusters);
  renderSuggestions(r.suggestions);
  renderTodos(r.todos || []);
  capture.focus();
})();
