// Switches between the Chat and Agent Graph views client-side (toggling `hidden` on the two
// `.view` wrappers — see theme.css) instead of navigating to a second HTML document, which used
// to throw away chat.js's whole in-memory state (SignalR connection, the rendered thread, pending
// confirmation choices) every time the operator switched tabs. The active view is tracked in the
// URL hash purely so a reload or a direct link can restore it — it plays no role in the switch
// itself.
const views = {
  chat: document.getElementById("view-chat"),
  graph: document.getElementById("view-graph"),
  settings: document.getElementById("view-settings"),
};
const tabs = document.querySelectorAll(".tab[data-view]");

function viewNameFromHash() {
  if (location.hash === "#graph") return "graph";
  if (location.hash === "#settings") return "settings";
  return "chat";
}

function activateView(name) {
  for (const [viewName, el] of Object.entries(views)) {
    el.hidden = viewName !== name;
  }
  for (const tab of tabs) {
    if (tab.dataset.view === name) {
      tab.setAttribute("aria-current", "page");
    } else {
      tab.removeAttribute("aria-current");
    }
  }

  if (name === "graph") {
    if (!agentGraphView) {
      initAgentGraphView(); // graph.js — first activation only, see its comment
    } else {
      // The container was `display:none` while this tab was hidden, so vis-network's canvas
      // needs an explicit refit now that it has real layout size again.
      agentGraphView.network.fit();
    }
  } else if (name === "settings" && !settingsLoaded) {
    initSettingsView(); // settings.js — first activation only, same lazy pattern as the graph tab
  }
}

// Not a `.tab` — a top-right icon button, same as themeToggle, opening a whole view rather than
// living in the Chat/Agent Graph nav (see index.html).
document.getElementById("settingsToggle").addEventListener("click", () => {
  location.hash = "#settings";
});

window.addEventListener("hashchange", () => activateView(viewNameFromHash()));
activateView(viewNameFromHash());
