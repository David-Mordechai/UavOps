// Theme toggle, shared by the Chat and Agent Graph views (both part of the one index.html page).
// Pairs with the inline no-flash snippet in <head> (which only handles the very first paint) —
// this file handles the actual click interaction, persistence, and icon state for the rest of the
// page's lifetime.
const THEME_KEY = "uavops-theme";
const themeToggle = document.getElementById("themeToggle");

function currentTheme() {
  const stored = localStorage.getItem(THEME_KEY);
  if (stored === "light" || stored === "dark") {
    return stored;
  }
  return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

function applyIconState(theme) {
  themeToggle.classList.toggle("is-light", theme === "light");
}

applyIconState(currentTheme());

themeToggle.addEventListener("click", () => {
  const next = currentTheme() === "dark" ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", next);
  localStorage.setItem(THEME_KEY, next);
  applyIconState(next);
  // graph.js already knows how to re-resolve its <canvas> colors (it listens for OS-level
  // prefers-color-scheme changes) — this reuses that same path for a manual toggle, which the
  // media-query listener alone can't see since the OS setting itself didn't change.
  document.dispatchEvent(new CustomEvent("themechange"));
});
