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
  Graph = ForceGraph()(document.getElementById("graph"))
    .backgroundColor("#0B0E14")
    .nodeId("id")
    .nodeRelSize(5)
    .nodeColor((n) => n.color)
    .linkColor(() => "rgba(92,103,112,0.35)")
    .linkWidth((l) => 0.5 + (l.weight || 0) * 1.8)
    .nodeCanvasObjectMode(() => "after")
    .nodeCanvasObject((node, ctx, scale) => {
      // node dot
      ctx.fillStyle = node.color;
      ctx.beginPath();
      ctx.arc(node.x, node.y, 4, 0, 2 * Math.PI);
      ctx.fill();
      if (node.id === currentNoteId) {
        ctx.strokeStyle = "#FFFFFF";
        ctx.lineWidth = 1.2 / scale;
        ctx.stroke();
      }
      // label only when zoomed in enough, to avoid clutter
      if (scale > 1.3) {
        const label = node.title || "";
        ctx.font = `${10 / scale}px Consolas, monospace`;
        ctx.fillStyle = "#BFC7D5";
        ctx.textAlign = "center";
        ctx.textBaseline = "top";
        ctx.fillText(label.slice(0, 28), node.x, node.y + 6);
      }
    })
    .onNodeClick((node) => {
      openNote(node.id);
      Graph.centerAt(node.x, node.y, 600);
    });
  window.addEventListener("resize", sizeGraph);
  sizeGraph();
}

function sizeGraph() {
  const pane = $("graphpane");
  if (Graph) Graph.width(pane.clientWidth).height(pane.clientHeight);
}

function renderGraph(g) {
  lastGraph = g;
  // force-graph mutates link source/target into objects; clone for safety.
  Graph.graphData({
    nodes: g.nodes.map((n) => ({ ...n })),
    links: g.links.map((l) => ({ source: l.source, target: l.target, weight: l.weight })),
  });
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
      row.onclick = () => renameCluster(c);
      el.appendChild(row);
    });
}

async function renameCluster(c) {
  const name = window.prompt("Rename cluster:", c.name);
  if (name && name.trim() && name !== c.name) {
    const r = await rpc("renameCluster", { id: c.id, name: name.trim() });
    renderGraph(r.graph);
  }
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
    row.onclick = () => renameCluster(c);
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
      toast("captured · " + (r.note ? r.note.title : ""));
    } catch (err) {
      toast("error: " + err.message);
    }
  }
});

// ---- reader -----------------------------------------------------------
async function openNote(id) {
  const note = await rpc("note", { id });
  if (!note) return;
  currentNoteId = id;
  $("reader").classList.remove("hidden");
  $("reader-title").textContent = note.title;
  $("reader-meta").textContent = "created " + fmtDate(note.createdAt) +
    (note.updatedAt !== note.createdAt ? " · edited " + fmtDate(note.updatedAt) : "");
  $("reader-body").value = note.body;
  if (Graph) Graph.nodeColor((n) => n.color); // trigger redraw of highlight
}

$("btn-save").onclick = async () => {
  if (currentNoteId === null) return;
  const text = $("reader-body").value;
  const r = await rpc("update", { id: currentNoteId, text });
  renderGraph(r.graph);
  renderClustersPanel(r.graph.clusters);
  if (r.note) { $("reader-title").textContent = r.note.title; }
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
  capture.focus();
})();
