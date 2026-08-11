// Renders the agent/tool graph fetched from /api/agent-graph (see Options/AgentGraphProjector.cs)
// with vis-network. AgentGraphProjector still owns *what nodes/edges can ever exist* — the full
// flat payload is fetched once and never re-requested. This file owns *which of them are
// currently shown, and where*: progressive disclosure (only BrainAgent + its direct subagents on
// load; clicking a node reveals its own children-agents, or its tools once you're at a leaf
// agent) plus a hand-rolled radial layout, so the graph stays a compact set of rings instead of
// vis-network's hierarchical layout fanning out wide as more agents/tools are added.

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

// vis-network draws to <canvas>, which can't consume var(--x) references directly — colors must
// be resolved to literal strings, and re-resolved whenever the OS theme changes (see
// applyThemeColors below).
function readThemeColors() {
  return {
    accent: cssVar("--accent"),
    accentText: cssVar("--accent-text"),
    accentSoft: cssVar("--accent-soft"),
    bgElevated: cssVar("--bg-elevated"),
    bgSubtle: cssVar("--bg-subtle"),
    border: cssVar("--border"),
    borderStrong: cssVar("--border-strong"),
    text: cssVar("--text"),
    textMuted: cssVar("--text-muted"),
    edge: cssVar("--graph-edge"),
    edgeHover: cssVar("--graph-edge-hover"),
    nodeShadow: cssVar("--graph-node-shadow"),
    // Canvas text needs a real font-family value — "inherit" isn't valid CSS-font syntax for a
    // 2D context and silently falls back to the browser's generic default (serif, wrong size),
    // so the app's actual font stack has to be read and passed explicitly.
    font: cssVar("--font"),
  };
}

// A minimal line-art brain glyph (two mirrored hemisphere outlines, a center fissure, a few fold
// strokes) rendered into the root node via vis-network's circularImage shape. Built as an inline
// SVG data URI rather than a vendored image file, matching this app's zero-external-asset/no
// PNG/no build-step convention already used for the vendored vis-network lib. Stroke-only, so it
// composites cleanly onto the accent-colored circle `groups.root.color.background` paints behind
// it (see buildGroups below).
function brainIconDataUri(strokeColor) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="${strokeColor}" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M9 3.5c-1.7 0-3 1.3-3 3 0 .3 0 .6.1.9C4.7 8 4 9.1 4 10.3c0 1 .5 1.9 1.2 2.4-.4.6-.7 1.3-.7 2.1 0 1.8 1.4 3.3 3.1 3.4.3 1.3 1.5 2.3 3 2.3.6 0 1.2-.2 1.7-.5"/>
    <path d="M15 3.5c1.7 0 3 1.3 3 3 0 .3 0 .6-.1.9 1.4.6 2.1 1.7 2.1 2.9 0 1-.5 1.9-1.2 2.4.4.6.7 1.3.7 2.1 0 1.8-1.4 3.3-3.1 3.4-.3 1.3-1.5 2.3-3 2.3-.6 0-1.2-.2-1.7-.5"/>
    <path d="M12 3.5v17"/>
    <path d="M9 8.2c.9.4 1.4 1.2 1.4 2.1 0 .7-.3 1.3-.8 1.8"/>
    <path d="M15 8.2c-.9.4-1.4 1.2-1.4 2.1 0 .7.3 1.3.8 1.8"/>
    <path d="M7.3 14.5c.8 0 1.5.5 1.8 1.2"/>
    <path d="M16.7 14.5c-.8 0-1.5.5-1.8 1.2"/>
  </svg>`;
  return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
}

// A small wrench glyph for tool nodes, same inline-SVG-data-URI technique as the root's brain
// icon above — replaces a plain unlabeled dot with something that actually reads as "a tool".
function toolIconDataUri(strokeColor) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="${strokeColor}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
    <path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/>
  </svg>`;
  return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
}

function buildTooltip(node) {
  const el = document.createElement("div");
  el.className = "graph-tooltip";

  const desc = document.createElement("p");
  desc.textContent = node.description || "(no description)";
  el.appendChild(desc);

  if (node.exampleUtterance) {
    const example = document.createElement("p");
    example.className = "example";
    example.textContent = `e.g. "${node.exampleUtterance}"`;
    el.appendChild(example);
  }

  if (node.type === "tool") {
    if (node.parameters && node.parameters.length > 0) {
      const params = document.createElement("p");
      params.className = "params";
      params.textContent = "Parameters: " + node.parameters.join(", ");
      el.appendChild(params);
    }
    if (node.requiresConfirmation) {
      const confirm = document.createElement("p");
      confirm.className = "params";
      confirm.textContent = "Requires operator confirmation";
      el.appendChild(confirm);
    }
  }

  if (node.type === "agent" && node.retrievalBased) {
    const retrieval = document.createElement("p");
    retrieval.className = "params";
    retrieval.textContent = "Delegates dynamically based on embedding similarity, not a fixed list.";
    el.appendChild(retrieval);
  }

  return el;
}

// vis-network's built-in "chosen" behavior (on by default) swaps in its own hardcoded hover
// colors (a light blue unreadable against this app's dark theme) *and* grows the node/font a
// couple of pixels on hover/select — felt like a jarring, jumpy size change on every mouse
// pass. `chosen: false` in the constructor's node options turns off both effects globally, so
// every group below only needs to define its one steady-state look.
function buildGroups(colors) {
  return {
    root: {
      shape: "circularImage",
      image: brainIconDataUri(colors.accentText),
      color: { background: colors.accent, border: colors.accent },
      font: { color: colors.text, size: 15, face: colors.font, vadjust: 2 },
      size: 26,
      borderWidth: 3,
      shadow: { enabled: true, color: colors.nodeShadow, size: 12, x: 0, y: 3 },
    },
    agent: {
      shape: "box",
      color: { background: colors.bgElevated, border: colors.borderStrong },
      font: { color: colors.text, size: 13.5, face: colors.font },
      shapeProperties: { borderRadius: 10, borderDashes: false },
      margin: { top: 10, right: 14, bottom: 10, left: 14 },
      shadow: { enabled: true, color: colors.nodeShadow, size: 7, x: 0, y: 2 },
    },
    agentRetrieval: {
      shape: "box",
      color: { background: colors.accentSoft, border: colors.accent },
      font: { color: colors.text, size: 13.5, face: colors.font },
      shapeProperties: { borderRadius: 10, borderDashes: [3, 3] },
      margin: { top: 10, right: 14, bottom: 10, left: 14 },
      shadow: { enabled: true, color: colors.nodeShadow, size: 7, x: 0, y: 2 },
    },
    tool: {
      shape: "circularImage",
      image: toolIconDataUri(colors.textMuted),
      color: { background: colors.bgSubtle, border: colors.border },
      font: { color: colors.textMuted, size: 11.5, face: colors.font },
      size: 13,
      borderWidth: 1.5,
      shadow: { enabled: true, color: colors.nodeShadow, size: 4, x: 0, y: 1 },
    },
  };
}

// Radial layout constants. Agent rings get more breathing room than the tool ring since agent
// labels are longer; tuned empirically against the current AgentsConfig roster (at most a
// handful of siblings visible at once, thanks to the accordion rule below) and against the
// larger card-style node footprint (shadow + padding) set up in buildGroups.
const RING_SPACING = 190;

class AgentGraph {
  constructor(container, data) {
    this.container = container;
    this.nodesById = new Map(data.nodes.map((n) => [n.id, n]));
    this.childrenOf = new Map();
    this.toolsOf = new Map();
    this.parentOf = new Map();

    for (const edge of data.edges) {
      if (edge.kind === "delegates") {
        this._push(this.childrenOf, edge.from, edge.to);
        this.parentOf.set(edge.to, edge.from);
      } else if (edge.kind === "uses") {
        this._push(this.toolsOf, edge.from, edge.to);
        this.parentOf.set(edge.to, edge.from);
      }
    }

    this.rootId = data.nodes.find((n) => n.isRoot).id;

    // Ids of agents whose children/tools are currently revealed. Seeded with just the root, so
    // the initial view is BrainAgent + its direct subagents only.
    this.expanded = new Set([this.rootId]);

    this.colors = readThemeColors();
    const options = {
      layout: { randomSeed: 1 },
      physics: false,
      edges: {
        arrows: { to: { enabled: true, scaleFactor: 0.45 } },
        color: { color: this.colors.edge, highlight: this.colors.edgeHover, hover: this.colors.edgeHover },
        width: 1.25,
        hoverWidth: 0.5,
        selectionWidth: 0.5,
        // "continuous" auto-shapes each curve from the two nodes' actual positions — unlike a
        // fixed forceDirection (meant for a top-down tree), this looks right regardless of where
        // around the radial layout an edge happens to sit.
        smooth: { type: "continuous", roundness: 0.35 },
      },
      nodes: {
        borderWidth: 1.5,
        shapeProperties: { interpolation: false },
        // Turns off vis-network's built-in hover/select embellishment (its own hardcoded light-
        // blue recolor, plus a couple of pixels of size/font growth) — see buildGroups above.
        chosen: false,
      },
      groups: buildGroups(this.colors),
      interaction: { hover: true, tooltipDelay: 100, dragNodes: true },
    };

    this.nodes = new vis.DataSet([]);
    this.edges = new vis.DataSet([]);
    this.network = new vis.Network(container, { nodes: this.nodes, edges: this.edges }, options);
    this.network.on("click", (params) => {
      if (params.nodes.length !== 1) return;
      this._handleClick(params.nodes[0]);
    });

    this.render(false);
  }

  _push(map, key, value) {
    if (!map.has(key)) map.set(key, []);
    map.get(key).push(value);
  }

  _expandableChildrenOf(id) {
    const children = this.childrenOf.get(id);
    if (children && children.length > 0) return children;
    const tools = this.toolsOf.get(id);
    if (tools && tools.length > 0) return tools;
    return [];
  }

  // Collapsing a node has to forget its whole subtree's expansion state, not just the node
  // itself — otherwise a grandchild drilled into earlier stays silently "remembered" in
  // `expanded`, and re-expanding the same branch later jumps straight back to that old depth
  // instead of starting fresh, as if the branch had never been opened before.
  _collapseSubtree(id) {
    this.expanded.delete(id);
    for (const kid of this._expandableChildrenOf(id)) {
      if (this.expanded.has(kid)) this._collapseSubtree(kid);
    }
  }

  _handleClick(id) {
    if (this._expandableChildrenOf(id).length === 0) return; // leaf: nothing to expand

    if (this.expanded.has(id)) {
      this._collapseSubtree(id);
      this.render(true);
      return;
    }

    // Accordion: collapse any other already-expanded sibling under the same parent before
    // opening this one, so at most one branch per level stays open and the graph stays compact.
    const parent = this.parentOf.get(id);
    if (parent !== undefined) {
      for (const sibling of this._expandableChildrenOf(parent)) {
        if (sibling !== id) this._collapseSubtree(sibling);
      }
    }
    this.expanded.add(id);
    this.render(true);
  }

  // BFS from the root, only descending into a node's children/tools when that node is itself in
  // `expanded`.
  _computeVisible() {
    const visible = [this.rootId];
    const byParent = new Map(); // parentId -> ordered list of visible child ids
    let frontier = [this.rootId];

    while (frontier.length > 0) {
      const next = [];
      for (const id of frontier) {
        if (!this.expanded.has(id)) continue;
        const kids = this._expandableChildrenOf(id);
        if (kids.length === 0) continue;
        byParent.set(id, kids);
        for (const kid of kids) {
          visible.push(kid);
          next.push(kid);
        }
      }
      frontier = next;
    }

    return { visible, byParent };
  }

  // Recursive equal angular subdivision: each node's own angular slice is split evenly among
  // its visible children, producing deterministic concentric rings (no physics jitter).
  _computePositions(byParent) {
    const positions = new Map([[this.rootId, { x: 0, y: 0 }]]);

    const place = (id, depth, angleStart, angleEnd) => {
      const kids = byParent.get(id);
      if (!kids || kids.length === 0) return;
      const slice = (angleEnd - angleStart) / kids.length;
      kids.forEach((kidId, i) => {
        const angle = angleStart + slice * (i + 0.5);
        const radius = RING_SPACING * (depth + 1);
        positions.set(kidId, { x: radius * Math.cos(angle), y: radius * Math.sin(angle) });
        place(kidId, depth + 1, angleStart + slice * i, angleStart + slice * (i + 1));
      });
    };

    place(this.rootId, 0, 0, 2 * Math.PI);
    return positions;
  }

  _labelFor(node) {
    const text = node.type === "tool" ? node.operation : node.id;
    if (node.type === "tool") return text; // tools are always leaves
    if (this._expandableChildrenOf(node.id).length === 0) return text; // childless/toolless agent
    // The "small triangle" Unicode variants (▸/▾) rendered too faintly at label font size; the
    // plain black-triangle glyphs (▶/▼) are visually heavier and read clearly at the same size.
    return (this.expanded.has(node.id) ? "▼ " : "▶ ") + text; // ▼ expanded / ▶ collapsed
  }

  _toVisNode(node, pos) {
    // Shape lives on the group definition (buildGroups) now, not here, so root/agent/tool
    // styling — including the root's brain icon — stays in one place.
    return {
      id: node.id,
      label: this._labelFor(node),
      group: node.isRoot ? "root" : node.type === "tool" ? "tool" : node.retrievalBased ? "agentRetrieval" : "agent",
      title: buildTooltip(node),
      x: pos.x,
      y: pos.y,
    };
  }

  render(animate) {
    const { visible, byParent } = this._computeVisible();
    const positions = this._computePositions(byParent);
    const visibleSet = new Set(visible);

    const visNodes = visible.map((id) => this._toVisNode(this.nodesById.get(id), positions.get(id)));
    const visEdges = [];
    for (const [parent, kids] of byParent) {
      for (const kid of kids) {
        if (visibleSet.has(kid)) {
          const dashes = (this.toolsOf.get(parent) || []).includes(kid);
          visEdges.push({ id: `${parent}::${kid}`, from: parent, to: kid, dashes });
        }
      }
    }

    // Visible set is always small (accordion keeps at most one open branch per level), so a full
    // clear+rebuild each render is simpler and safer than incrementally diffing the DataSets.
    this.nodes.clear();
    this.edges.clear();
    this.nodes.add(visNodes);
    this.edges.add(visEdges);

    if (animate) {
      this.network.fit({ animation: { duration: 300, easingFunction: "easeInOutQuad" } });
    } else {
      this.network.fit();
    }
  }

  applyThemeColors(colors) {
    this.colors = colors;
    this.network.setOptions({
      groups: buildGroups(colors), // regenerates the root's brain icon too (its stroke is theme-dependent)
      edges: { color: { color: colors.edge, highlight: colors.edgeHover, hover: colors.edgeHover } },
    });
    // setOptions only changes the *defaults* new nodes/edges would get — vis-network doesn't
    // retroactively re-merge group options into DataSet entries that already exist, so already-
    // rendered nodes/edges would otherwise keep their stale-theme colors. Forcing render()'s
    // usual clear+re-add path re-creates them against the just-updated groups/edge defaults.
    this.render(false);
  }
}

async function main() {
  const res = await fetch("/api/agent-graph");
  const data = await res.json();

  const graph = new AgentGraph(document.getElementById("graph-container"), data);

  // chat.css/graph.css otherwise handle dark mode passively via @media — this page is the one
  // deliberate exception, since canvas-rendered colors can't be literal CSS custom properties.
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    graph.applyThemeColors(readThemeColors());
  });
}

main().catch((err) => {
  console.error(err);
  const container = document.getElementById("graph-container");
  container.textContent = "Failed to load the agent graph: " + err.message;
});
