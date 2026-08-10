// Renders the agent/tool graph fetched from /api/agent-graph (see Options/AgentGraphProjector.cs)
// with vis-network. Deliberately dumb: no client-side graph-building logic beyond mapping the
// server's node/edge JSON into vis-network's own shape — adding a new agent/tool YAML file and
// restarting the server is enough for a new node to show up here, no changes to this file needed.

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
    bgElevated: cssVar("--bg-elevated"),
    bgSubtle: cssVar("--bg-subtle"),
    border: cssVar("--border"),
    borderStrong: cssVar("--border-strong"),
    text: cssVar("--text"),
    textMuted: cssVar("--text-muted"),
  };
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

function toVisNode(node) {
  return {
    id: node.id,
    label: node.type === "tool" ? node.operation : node.id,
    shape: node.type === "tool" ? "dot" : node.isRoot ? "hexagon" : "box",
    group: node.isRoot ? "root" : node.type === "tool" ? "tool" : node.retrievalBased ? "agentRetrieval" : "agent",
    title: buildTooltip(node),
  };
}

function toVisEdge(edge) {
  return {
    from: edge.from,
    to: edge.to,
    dashes: edge.kind === "uses",
  };
}

function buildGroups(colors) {
  return {
    root: {
      color: { background: colors.accent, border: colors.accent },
      font: { color: colors.accentText, size: 16, face: "inherit" },
      size: 22,
    },
    agent: {
      color: { background: colors.bgElevated, border: colors.borderStrong },
      font: { color: colors.text, size: 14, face: "inherit" },
      shapeProperties: { borderDashes: false },
    },
    agentRetrieval: {
      color: { background: colors.bgElevated, border: colors.accent },
      font: { color: colors.text, size: 14, face: "inherit" },
      shapeProperties: { borderDashes: [4, 4] },
    },
    tool: {
      color: { background: colors.bgSubtle, border: colors.border },
      font: { color: colors.textMuted, size: 11, face: "inherit" },
      size: 10,
    },
  };
}

async function main() {
  const res = await fetch("/api/agent-graph");
  const data = await res.json();

  const nodes = new vis.DataSet(data.nodes.map(toVisNode));
  const edges = new vis.DataSet(data.edges.map(toVisEdge));

  const colors = readThemeColors();
  const options = {
    layout: {
      hierarchical: {
        direction: "UD",
        sortMethod: "directed",
        levelSeparation: 140,
        nodeSpacing: 130,
      },
    },
    physics: false,
    edges: {
      arrows: { to: { enabled: true, scaleFactor: 0.6 } },
      color: { color: colors.border, highlight: colors.accent },
      smooth: { type: "cubicBezier", forceDirection: "vertical", roundness: 0.5 },
    },
    nodes: {
      borderWidth: 1.5,
      shapeProperties: { interpolation: false },
    },
    groups: buildGroups(colors),
    interaction: { hover: true, tooltipDelay: 100, dragNodes: true },
  };

  const network = new vis.Network(document.getElementById("graph-container"), { nodes, edges }, options);

  // chat.css/graph.css otherwise handle dark mode passively via @media — this page is the one
  // deliberate exception, since canvas-rendered colors can't be literal CSS custom properties.
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    const updated = readThemeColors();
    network.setOptions({
      groups: buildGroups(updated),
      edges: { color: { color: updated.border, highlight: updated.accent } },
    });
  });
}

main().catch((err) => {
  console.error(err);
  const container = document.getElementById("graph-container");
  container.textContent = "Failed to load the agent graph: " + err.message;
});
