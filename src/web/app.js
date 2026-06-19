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

// ---- brand splash overlay ---------------------------------------------
// The splash (an iframe, same-origin) calls onEngramSplashDone / dispatches
// "engram:splash-done" when its intro finishes. We fade it out then; the app
// has been loading behind it the whole time.
(function wireSplash() {
  const f = document.getElementById("splash-frame");
  if (!f) return;
  let removed = false;
  const remove = () => {
    if (removed) return;
    removed = true;
    f.classList.add("gone");
    setTimeout(() => f.remove(), 600);
  };
  f.addEventListener("load", () => {
    try {
      f.contentWindow.onEngramSplashDone = remove;
      f.contentWindow.addEventListener("engram:splash-done", remove);
    } catch { /* cross-origin shouldn't happen (same virtual host) */ }
  });
  setTimeout(remove, 6500); // safety net if the splash never signals
})();

function applyImport(payload) {
  if (!payload) return;
  if (payload.graph) {
    renderGraph(payload.graph);
    renderClustersPanel(payload.graph.clusters);
    refreshTodos();
    refreshReview();
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
let lastGraph = { nodes: [], links: [], clusters: [] }; // full graph (all notes)
let clusterFilter = null;   // when set, only this cluster's subgraph is shown
let hideOrphans = false;     // hide notes with no links

// ---- graph mode: 'idle' (ambient neural firing) vs 'browse' (calm/interactive)
const IDLE_AFTER_MS = 7000;
const FLOAT_AMP = 11;    // idle float radius in graph units
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
  const h = (hex || "#5C6675").replace("#", "");
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
    const amp = FLOAT_AMP * pulse; // float radius, fades with ambient
    for (const n of Graph.graphData().nodes) {
      if (n === draggingNode || !Number.isFinite(n.bx)) continue;
      n.x = n.bx + Math.sin(t * n.f1 + n.ph) * amp;
      n.y = n.by + Math.cos(t * n.f2 + n.ph * 1.3) * amp;
    }
  }
  requestAnimationFrame(animateMode);
}

// Capture each node's settled "home" + a unique drift phase/frequency.
function captureHome() {
  for (const n of Graph.graphData().nodes) {
    // Guard against a non-finite position (would make the node vanish and
    // break zoomToFit's bounding box).
    if (!Number.isFinite(n.x) || !Number.isFinite(n.y)) { n.x = 0; n.y = 0; }
    n.bx = n.x;
    n.by = n.y;
    if (n.ph == null) {
      n.ph = Math.random() * Math.PI * 2;
      n.f1 = 0.25 + Math.random() * 0.35;
      n.f2 = 0.25 + Math.random() * 0.35;
    }
  }
  settled = true;
  if (fitOnSettle) { fitOnSettle = false; setTimeout(() => Graph && Graph.zoomToFit(400, 50), 60); }
}
let fitOnSettle = true; // fit once after the next layout settles (initial load + filter changes)

// Node radius scales with the note's content size. Square-root scaling so the
// node *area* tracks data volume (4x the data ≈ 2x the radius), clamped.
function nodeRadius(node) {
  const len = node.size || 0;
  return Math.max(3, Math.min(16, 3 + Math.sqrt(len) / 4));
}

// A simple radius-aware collision force (d3-compatible) — pushes overlapping
// nodes apart, scaled by each node's data-driven radius plus padding.
function makeCollideForce(pad) {
  let nodes = [];
  const strength = 0.85;
  const iterations = 2;
  function force() {
    for (let it = 0; it < iterations; it++) {
      for (let i = 0; i < nodes.length; i++) {
        const a = nodes[i];
        const ra = nodeRadius(a);
        for (let j = i + 1; j < nodes.length; j++) {
          const b = nodes[j];
          let dx = b.x - a.x, dy = b.y - a.y;
          const minD = ra + nodeRadius(b) + pad;
          let d2 = dx * dx + dy * dy;
          if (d2 > 0 && d2 < minD * minD) {
            const d = Math.sqrt(d2);
            const shift = ((minD - d) / d) * strength * 0.5;
            const ox = dx * shift, oy = dy * shift;
            a.x -= ox; a.y -= oy;
            b.x += ox; b.y += oy;
          }
        }
      }
    }
  }
  force.initialize = (n) => { nodes = n; };
  return force;
}

// Per-cluster anchor positions (a ring that grows with the cluster count), so
// clusters settle into distinct regions instead of collapsing onto each other.
let clusterAnchors = {};
function computeClusterAnchors(nodes) {
  const ids = [...new Set(nodes.map((n) => n.cluster ?? 0))];
  clusterAnchors = {};
  const R = 90 + ids.length * 55; // ring radius scales with how many clusters
  ids.forEach((cid, i) => {
    if (ids.length <= 1) { clusterAnchors[cid] = { x: 0, y: 0 }; return; }
    const ang = (2 * Math.PI * i) / ids.length;
    clusterAnchors[cid] = { x: Math.cos(ang) * R, y: Math.sin(ang) * R };
  });
}

// Pulls each node gently toward its cluster's anchor → grouped, separated blobs.
function makeClusterForce() {
  let nodes = [];
  function force(alpha) {
    const k = 0.13 * alpha;
    for (const n of nodes) {
      const a = clusterAnchors[n.cluster ?? 0];
      if (!a) continue;
      n.vx += (a.x - n.x) * k;
      n.vy += (a.y - n.y) * k;
    }
  }
  force.initialize = (n) => { nodes = n; };
  return force;
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
    .backgroundColor("#0B0F15")
    .nodeId("id")
    .nodeRelSize(5)
    .nodeColor((n) => n.color)
    .cooldownTime(4000)
    .linkColor((l) => {
      if (mode === "browse" && highlightLinks.size)
        return highlightLinks.has(l) ? "rgba(67,224,139,0.55)" : "rgba(92,102,117,0.07)";
      return "rgba(92,102,117,0.26)";
    })
    .linkWidth((l) => (highlightLinks.has(l) ? 2.2 : 0.5 + (l.weight || 0) * 1.5))
    .linkDirectionalParticles(particleAccessor)
    .linkDirectionalParticleSpeed((l) => 0.003 + (l.weight || 0) * 0.006)
    .linkDirectionalParticleWidth(2)
    .linkDirectionalParticleColor(() => "rgba(67,224,139,0.9)")
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
        ctx.fillStyle = "#C7D0DC";
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
      // Hovering is not "input" — it must not wake the idle animation.
      // Highlighting only happens once you've actually interacted (browse mode).
      if (mode !== "browse") return;
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

  // Layout tuning: more repulsion + longer (similarity-scaled) links so nodes
  // don't pile up, plus per-cluster anchoring so clusters spread into regions.
  const charge = Graph.d3Force("charge");
  if (charge) charge.strength(-95).distanceMax(380);
  const link = Graph.d3Force("link");
  if (link) link.distance((l) => 28 + (1 - (l.weight || 0)) * 34);
  Graph.d3Force("cluster", makeClusterForce());

  // Size-aware collision so big (data-heavy) nodes don't overlap. Runs during
  // the layout phase, so the captured "home" positions are already spaced; the
  // padding leaves headroom for the idle float drift.
  Graph.d3Force("collide", makeCollideForce(FLOAT_AMP + 4));

  Graph.cooldownTime(2500);     // settle the layout fairly quickly, then float
  Graph.resumeAnimation();      // keep the render loop running so fades never freeze

  // Only genuine input (click / drag / scroll-zoom) drops out of the idle
  // animation — not hovering or moving the cursor over the graph.
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

// Reset / fit the whole graph back into view (after panning/zooming away).
// Also recovers a blank graph: if nothing is shown but notes exist, clear any
// filter and re-fit.
function resetView() {
  if (!Graph) return;
  markActivity();
  const shown = Graph.graphData().nodes.length;
  if (shown === 0 && lastGraph.nodes.length > 0) {
    resetFilter(); // un-stick a stale/empty filter
    setTimeout(() => Graph.zoomToFit(500, 40), 120);
    return;
  }
  Graph.zoomToFit(500, 40);
}
$("btn-reset-view").onclick = resetView;

function renderGraph(g) {
  lastGraph = g; // keep the full graph (palette, cluster detail use it)
  renderLegend(g.clusters);
  applyFilter();
}

// Build the displayed subgraph from the full graph + current filter state.
function applyFilter() {
  const g = lastGraph;
  // Drop a stale cluster focus (cluster ids get renumbered on reindex, e.g.
  // after capture / accept-merge / librarian) so the graph never goes blank.
  if (clusterFilter != null && !g.nodes.some((n) => n.cluster === clusterFilter)) {
    clusterFilter = null;
  }
  let nodes = g.nodes;
  if (clusterFilter != null) nodes = nodes.filter((n) => n.cluster === clusterFilter);

  let idset = new Set(nodes.map((n) => n.id));
  let links = g.links.filter((l) => idset.has(l.source) && idset.has(l.target));

  if (hideOrphans) {
    const connected = new Set();
    links.forEach((l) => { connected.add(l.source); connected.add(l.target); });
    nodes = nodes.filter((n) => connected.has(n.id));
    idset = new Set(nodes.map((n) => n.id));
    links = links.filter((l) => idset.has(l.source) && idset.has(l.target));
  }

  highlightNodes.clear();
  highlightLinks.clear();
  settled = false;       // re-layout the filtered set, then re-home
  draggingNode = null;
  computeClusterAnchors(nodes);
  Graph.graphData({
    nodes: nodes.map((n) => ({ ...n })),
    links: links.map((l) => ({ source: l.source, target: l.target, weight: l.weight })),
  });
  Graph.resumeAnimation();

  const filtered = clusterFilter != null || hideOrphans;
  $("statusline").textContent =
    `${nodes.length}${filtered ? `/${g.nodes.length}` : ""} notes · ${links.length} links · ${g.clusters.length} clusters`;
  updateFilterBar();
}

// Filter changes refit the view (the subset re-lays-out around the origin, so
// the camera must follow or the result looks blank/off-screen).
function focusCluster(id) { clusterFilter = id; changeFilter(); }
function resetFilter() { clusterFilter = null; hideOrphans = false; changeFilter(); }
function toggleOrphans() { hideOrphans = !hideOrphans; changeFilter(); }

function changeFilter() {
  fitOnSettle = true;          // refine-fit once the new layout settles
  applyFilter();
  // early fit so the subset is framed immediately (initial positions exist now)
  setTimeout(() => { if (Graph && Graph.graphData().nodes.length) Graph.zoomToFit(350, 50); }, 220);
}

function updateFilterBar() {
  const bar = $("filterbar");
  if (clusterFilter == null && !hideOrphans) { bar.classList.add("hidden"); return; }
  bar.classList.remove("hidden");
  const c = lastGraph.clusters.find((x) => x.id === clusterFilter);
  const bits = [];
  if (c) bits.push(`focus: ${esc(c.name)}`);
  if (hideOrphans) bits.push("orphans hidden");
  bar.innerHTML = `<span>${bits.join(" · ")}</span> <span class="mini-link" id="filter-reset">show all ✕</span>`;
  $("filter-reset").onclick = resetFilter;
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
    `<span class="mini-link" id="cv-focus">focus</span>` +
    `<span class="mini-link" id="cv-quiz">quiz</span>` +
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
  $("cv-focus").onclick = () => { focusCluster(c.id); el.classList.add("hidden"); };
  $("cv-quiz").onclick = () => { el.classList.add("hidden"); startQuiz(c.id, c.name); };
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
      refreshReview();
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
  refreshReview();
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

// ---- spaced-repetition resurfacing ------------------------------------
function renderReview(items) {
  const el = $("review");
  let html = `<div class="panel-title">resurface ${items.length ? `· ${items.length} due ` : ""}</div>`;
  if (!items.length) {
    el.innerHTML = html + `<div class="empty">nothing due — you're caught up</div>`;
    return;
  }
  items.forEach((it) => {
    html +=
      `<div class="rev" data-id="${it.id}">` +
      `<div class="rev-title" style="color:${it.color}">${esc(it.title)}</div>` +
      `<div class="row-sub">${esc(it.snippet)}</div>` +
      `<div class="rev-actions">` +
      `<button data-g="again" title="see again soon">again</button>` +
      `<button data-g="good">good</button>` +
      `<button data-g="easy" title="push further out">easy</button>` +
      `</div></div>`;
  });
  el.innerHTML = html;
  el.querySelectorAll(".rev").forEach((div) => {
    const id = Number(div.getAttribute("data-id"));
    div.querySelector(".rev-title").onclick = () => { openNote(id); focusNode(id); };
    div.querySelectorAll(".rev-actions button").forEach((b) => {
      b.onclick = async () => {
        const items2 = await rpc("rateReview", { id, grade: b.getAttribute("data-g") });
        renderReview(items2);
      };
    });
  });
}

async function refreshReview() {
  try { renderReview(await rpc("review")); } catch {}
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

// ---- command palette (Ctrl+K) ----------------------------------------
const paletteEl = $("palette");
const paletteInput = $("palette-input");
let paletteItems = [];
let paletteSel = 0;

const COMMANDS = [
  { label: "Capture a note", hint: "new", run: () => capture.focus() },
  { label: "Ask Claude a question", hint: "ask", run: () => searchInput.focus() },
  { label: "Run librarian (name + summarize clusters)", hint: "claude", run: () => $("btn-librarian").click() },
  { label: "Scan notes for tasks", hint: "claude", run: () => scanTodos() },
  { label: "Quiz me on a cluster", hint: "claude", run: () => startQuiz(null) },
  { label: "Review due notes", hint: "study", run: () => $("review")?.scrollIntoView({ behavior: "smooth" }) },
  { label: "Import files", hint: "", run: () => $("btn-import").click() },
  { label: "Export to zip", hint: "", run: () => $("btn-export").click() },
  { label: "Reset / fit graph view", hint: "graph", run: () => resetView() },
  { label: "Show all (clear graph filter)", hint: "graph", run: () => resetFilter() },
  { label: "Toggle orphan notes", hint: "graph", run: () => toggleOrphans() },
];

// Subsequence fuzzy score; -1 = no match, higher = better.
function fuzzy(q, text) {
  if (!q) return 0.0001;
  q = q.toLowerCase();
  text = (text || "").toLowerCase();
  let ti = 0, score = 0, streak = 0;
  for (const c of q) {
    let found = -1;
    for (let k = ti; k < text.length; k++) if (text[k] === c) { found = k; break; }
    if (found < 0) return -1;
    streak = found === ti ? streak + 1 : 0;
    score += 1 + streak * 0.5 - (found - ti) * 0.02;
    ti = found + 1;
  }
  return score;
}

function openPalette() {
  paletteEl.classList.remove("hidden");
  paletteInput.value = "";
  buildPalette("");
  paletteInput.focus();
}
function closePalette() { paletteEl.classList.add("hidden"); }

function buildPalette(q) {
  const results = [];
  COMMANDS.forEach((c) => {
    const s = fuzzy(q, c.label);
    if (s >= 0) results.push({ label: c.label, hint: c.hint, score: s + 0.2, run: c.run });
  });
  lastGraph.nodes.forEach((n) => {
    const s = fuzzy(q, n.title);
    if (s >= 0) results.push({ label: n.title, hint: "note", color: n.color, score: s, run: () => { openNote(n.id); focusNode(n.id); } });
  });
  results.sort((a, b) => b.score - a.score);
  paletteItems = results.slice(0, 40);
  paletteSel = 0;
  renderPalette();
}

function renderPalette() {
  const list = $("palette-list");
  if (paletteItems.length === 0) { list.innerHTML = `<div class="empty">no matches</div>`; return; }
  list.innerHTML = paletteItems.map((it, i) =>
    `<div class="pal-item ${i === paletteSel ? "sel" : ""}" data-i="${i}">` +
    `<span class="pal-dot" style="background:${it.color || "transparent"}"></span>` +
    `<span class="pal-label">${esc(it.label)}</span>` +
    `<span class="pal-hint">${esc(it.hint || "")}</span></div>`).join("");
  list.querySelectorAll(".pal-item").forEach((el) => {
    el.onclick = () => runPalette(Number(el.getAttribute("data-i")));
  });
}

function runPalette(i) {
  const it = paletteItems[i];
  if (!it) return;
  closePalette();
  it.run();
}

paletteInput.addEventListener("input", () => buildPalette(paletteInput.value.trim()));
paletteInput.addEventListener("keydown", (e) => {
  if (e.key === "ArrowDown") { e.preventDefault(); paletteSel = Math.min(paletteSel + 1, paletteItems.length - 1); renderPalette(); scrollPaletteSel(); }
  else if (e.key === "ArrowUp") { e.preventDefault(); paletteSel = Math.max(paletteSel - 1, 0); renderPalette(); scrollPaletteSel(); }
  else if (e.key === "Enter") { e.preventDefault(); runPalette(paletteSel); }
  else if (e.key === "Escape") { e.preventDefault(); closePalette(); }
});
function scrollPaletteSel() {
  const el = $("palette-list").querySelector(".pal-item.sel");
  if (el) el.scrollIntoView({ block: "nearest" });
}
paletteEl.onclick = (e) => { if (e.target === paletteEl) closePalette(); };

window.addEventListener("keydown", (e) => {
  if ((e.ctrlKey || e.metaKey) && (e.key === "k" || e.key === "K")) {
    e.preventDefault();
    paletteEl.classList.contains("hidden") ? openPalette() : closePalette();
  } else if (e.key === "Escape") {
    if (!paletteEl.classList.contains("hidden")) closePalette();
    if (!$("quiz").classList.contains("hidden")) closeQuiz();
  } else if ((e.key === "f" || e.key === "F") && !e.ctrlKey && !e.metaKey && !e.altKey) {
    const tag = (e.target.tagName || "").toLowerCase();
    if (tag !== "input" && tag !== "textarea") { e.preventDefault(); resetView(); }
  }
});

// ---- quiz / flashcards ------------------------------------------------
let quizCards = [];
let quizIdx = 0;
let quizRevealed = false;
let quizCluster = null;

function closeQuiz() { $("quiz").classList.add("hidden"); }

async function startQuiz(clusterId, clusterName) {
  const overlay = $("quiz");
  overlay.classList.remove("hidden");
  // No cluster chosen → show a picker of clusters.
  if (clusterId == null) {
    const picks = lastGraph.clusters.filter((c) => c.count > 0);
    $("quiz-head").textContent = "quiz — pick a cluster";
    $("quiz-card").innerHTML = picks.length
      ? picks.map((c) => `<div class="quiz-pick" data-id="${c.id}" data-name="${esc(c.name)}"><span class="swatch" style="background:${c.color}"></span>${esc(c.name)} <span class="cluster-count">${c.count}</span></div>`).join("")
      : `<div class="empty">no clusters yet</div>`;
    $("quiz-actions").innerHTML = `<button id="quiz-close">close</button>`;
    $("quiz-close").onclick = closeQuiz;
    $("quiz-card").querySelectorAll(".quiz-pick").forEach((el) => {
      el.onclick = () => startQuiz(Number(el.getAttribute("data-id")), el.getAttribute("data-name"));
    });
    return;
  }

  quizCluster = clusterId;
  $("quiz-head").textContent = `quiz — ${clusterName || "cluster"}`;
  $("quiz-card").innerHTML = `<div class="thinking">generating cards… (claude)</div>`;
  $("quiz-actions").innerHTML = "";
  try {
    const r = await rpc("quiz", { clusterId });
    quizCards = r.cards || [];
    quizIdx = 0;
    quizRevealed = false;
    if (quizCards.length === 0) {
      $("quiz-card").innerHTML = `<div class="empty">${esc(r.error || "no cards generated")}</div>`;
      $("quiz-actions").innerHTML = `<button id="quiz-close">close</button>`;
      $("quiz-close").onclick = closeQuiz;
      return;
    }
    renderQuizCard();
  } catch (err) {
    $("quiz-card").innerHTML = `<div class="empty">error: ${esc(err.message)}</div>`;
  }
}

function renderQuizCard() {
  const card = quizCards[quizIdx];
  $("quiz-head").textContent = `card ${quizIdx + 1} / ${quizCards.length}`;
  $("quiz-card").innerHTML =
    `<div class="quiz-q">${esc(card.question)}</div>` +
    (quizRevealed ? `<div class="quiz-a">${esc(card.answer)}</div>` : "");
  if (!quizRevealed) {
    $("quiz-actions").innerHTML = `<button id="quiz-reveal">reveal</button><button id="quiz-close">close</button>`;
    $("quiz-reveal").onclick = () => { quizRevealed = true; renderQuizCard(); };
  } else {
    const last = quizIdx === quizCards.length - 1;
    $("quiz-actions").innerHTML =
      `<button id="quiz-next">${last ? "finish" : "next"}</button><button id="quiz-close">close</button>`;
    $("quiz-next").onclick = () => {
      if (last) { closeQuiz(); toast("quiz complete"); return; }
      quizIdx++; quizRevealed = false; renderQuizCard();
    };
  }
  $("quiz-close").onclick = closeQuiz;
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
  renderReview(r.review || []);
  capture.focus();
})();
