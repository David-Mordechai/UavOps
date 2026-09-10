// Renders the Settings page fetched from /api/settings (see Options/SettingsStore.cs) and posts
// edits back to it. The payload's shape mirrors each domain's own real config sections directly
// (e.g. agent.Ollama.Endpoint, mcpServers.moav.settings.HostHubUrl) rather than a hand-authored
// schema, so this file is a *generic*, type-driven form builder — string/number/boolean render as
// the obvious input, a nested object becomes a nested group, an array-of-strings becomes an
// add/remove list, and the two known dictionary-of-strings fields (DICTIONARY_FIELDS below) become
// a key/value row editor — rather than 30+ hand-written fields that would drift out of sync with
// the backend's own Options classes.
//
// liveFields/pathFields (both returned by the GET response) are the two places this file
// deliberately does NOT invent its own knowledge: which fields apply without a restart, and which
// are filesystem paths worth validating, are decided once, server-side, in SettingsStore.cs.

// The ~5 fields that are really enums, not free text - dotted path (matching the payload's own
// shape) -> allowed values, in the order they should appear in the <select>.
const ENUM_FIELDS = {
  "agent.ExecutionMode": ["Confirm", "Direct"],
  "agent.AgentModels.Provider": ["Ollama", "OpenAI"],
  "mcpServers.moav.settings.OperationBackend": ["Simulated", "SignalR"],
  "mcpServers.watchdog.settings.WatchdogBackend": ["Fake", "Real"],
  "mcpServers.simulator.settings.SimulatorBackend": ["Fake", "Real"],
};

// The only two object-of-string fields in the whole settings surface (Watchdog.ServiceNameMap,
// Watchdog.ExecutablePlaceholders) - hardcoded rather than inferred, since an *empty* dictionary
// (`{}`, the common default) looks identical at runtime to "a nested object with zero known
// fields," which needs the opposite rendering (a nested group, not a key/value row editor).
const DICTIONARY_FIELDS = new Set([
  "mcpServers.watchdog.settings.Watchdog.ServiceNameMap",
  "mcpServers.watchdog.settings.Watchdog.ExecutablePlaceholders",
]);

let settingsLoaded = false;
let pathValidationTargets = [];

async function initSettingsView() {
  const container = document.getElementById("settings-container");
  try {
    const res = await fetch("/api/settings");
    if (!res.ok) throw new Error("HTTP " + res.status);
    const data = await res.json();
    settingsLoaded = true;
    renderSettings(data);
  } catch (err) {
    console.error(err);
    container.textContent = "Failed to load settings: " + err.message;
  }
}

function deepClone(value) {
  return JSON.parse(JSON.stringify(value));
}

function setPath(obj, pathParts, value) {
  let cur = obj;
  for (let i = 0; i < pathParts.length - 1; i++) {
    const key = pathParts[i];
    if (typeof cur[key] !== "object" || cur[key] === null) cur[key] = {};
    cur = cur[key];
  }
  cur[pathParts[pathParts.length - 1]] = value;
}

function labelFor(pathParts) {
  const last = pathParts[pathParts.length - 1];
  const spaced = last.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function capitalize(s) {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function debounce(fn, delayMs) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), delayMs);
  };
}

function appendLiveBadge(row, dottedPath, liveFieldsSet) {
  const isLive = liveFieldsSet.has(dottedPath);
  const badge = document.createElement("span");
  badge.className = "settings-badge " + (isLive ? "settings-badge-live" : "settings-badge-restart");
  badge.textContent = isLive ? "applies immediately" : "restart required";
  row.appendChild(badge);
}

async function validatePathField(input, status) {
  const path = input.value.trim();
  if (!path) {
    status.textContent = "";
    status.className = "settings-path-status";
    return;
  }
  try {
    const res = await fetch("/api/validate-path?path=" + encodeURIComponent(path));
    const data = await res.json();
    status.textContent = data.exists ? "✓" : "✗ not found";
    status.className = "settings-path-status " + (data.exists ? "settings-path-ok" : "settings-path-missing");
  } catch {
    status.textContent = "";
    status.className = "settings-path-status";
  }
}

function renderScalarField(value, pathParts, formRoot, liveFieldsSet, pathFieldsSet, parentEl) {
  const dotted = pathParts.join(".");
  const row = document.createElement("label");
  row.className = "settings-field";

  const labelEl = document.createElement("span");
  labelEl.className = "settings-field-label";
  labelEl.textContent = labelFor(pathParts);
  row.appendChild(labelEl);

  let input;
  if (ENUM_FIELDS[dotted]) {
    input = document.createElement("select");
    input.className = "settings-input";
    for (const option of ENUM_FIELDS[dotted]) {
      const optionEl = document.createElement("option");
      optionEl.value = option;
      optionEl.textContent = option;
      if (option === value) optionEl.selected = true;
      input.appendChild(optionEl);
    }
    input.addEventListener("change", () => setPath(formRoot, pathParts, input.value));
  } else if (typeof value === "boolean") {
    input = document.createElement("input");
    input.type = "checkbox";
    input.className = "settings-checkbox";
    input.checked = value;
    input.addEventListener("change", () => setPath(formRoot, pathParts, input.checked));
  } else if (typeof value === "number") {
    input = document.createElement("input");
    input.type = "number";
    input.className = "settings-input";
    input.value = value;
    input.addEventListener("input", () => setPath(formRoot, pathParts, input.valueAsNumber));
  } else {
    input = document.createElement("input");
    input.type = "text";
    input.className = "settings-input";
    input.value = value ?? "";
    input.addEventListener("input", () => setPath(formRoot, pathParts, input.value));
  }
  row.appendChild(input);

  appendLiveBadge(row, dotted, liveFieldsSet);

  if (pathFieldsSet.has(dotted)) {
    const status = document.createElement("span");
    status.className = "settings-path-status";
    row.appendChild(status);
    pathValidationTargets.push({ path: dotted, status });
    const debouncedValidate = debounce(() => validatePathField(input, status), 400);
    input.addEventListener("input", debouncedValidate);
    validatePathField(input, status);
  }

  parentEl.appendChild(row);
}

function renderListField(value, pathParts, formRoot, parentEl) {
  const wrap = document.createElement("div");
  wrap.className = "settings-field settings-field-list";

  const labelEl = document.createElement("span");
  labelEl.className = "settings-field-label";
  labelEl.textContent = labelFor(pathParts);
  wrap.appendChild(labelEl);

  const listEl = document.createElement("div");
  listEl.className = "settings-list";
  wrap.appendChild(listEl);

  const items = [...value];
  setPath(formRoot, pathParts, items);

  function redraw() {
    listEl.innerHTML = "";
    items.forEach((item, i) => {
      const row = document.createElement("div");
      row.className = "settings-list-row";

      const input = document.createElement("input");
      input.type = "text";
      input.className = "settings-input";
      input.value = item;
      input.addEventListener("input", () => {
        items[i] = input.value;
      });
      row.appendChild(input);

      const removeBtn = document.createElement("button");
      removeBtn.type = "button";
      removeBtn.className = "settings-list-remove";
      removeBtn.textContent = "✕";
      removeBtn.addEventListener("click", () => {
        items.splice(i, 1);
        redraw();
      });
      row.appendChild(removeBtn);

      listEl.appendChild(row);
    });
  }
  redraw();

  const addBtn = document.createElement("button");
  addBtn.type = "button";
  addBtn.className = "settings-list-add";
  addBtn.textContent = "+ Add";
  addBtn.addEventListener("click", () => {
    items.push("");
    redraw();
  });
  wrap.appendChild(addBtn);

  parentEl.appendChild(wrap);
}

function renderDictionaryField(value, pathParts, formRoot, parentEl) {
  const wrap = document.createElement("div");
  wrap.className = "settings-field settings-field-list";

  const labelEl = document.createElement("span");
  labelEl.className = "settings-field-label";
  labelEl.textContent = labelFor(pathParts);
  wrap.appendChild(labelEl);

  const listEl = document.createElement("div");
  listEl.className = "settings-list";
  wrap.appendChild(listEl);

  const entries = Object.entries(value).map(([key, val]) => ({ key, value: String(val) }));

  function commit() {
    const obj = {};
    for (const entry of entries) {
      if (entry.key.trim()) obj[entry.key] = entry.value;
    }
    setPath(formRoot, pathParts, obj);
  }
  commit();

  function redraw() {
    listEl.innerHTML = "";
    entries.forEach((entry, i) => {
      const row = document.createElement("div");
      row.className = "settings-list-row";

      const keyInput = document.createElement("input");
      keyInput.type = "text";
      keyInput.className = "settings-input settings-dict-key";
      keyInput.placeholder = "key";
      keyInput.value = entry.key;
      keyInput.addEventListener("input", () => {
        entry.key = keyInput.value;
        commit();
      });
      row.appendChild(keyInput);

      const valueInput = document.createElement("input");
      valueInput.type = "text";
      valueInput.className = "settings-input";
      valueInput.placeholder = "value";
      valueInput.value = entry.value;
      valueInput.addEventListener("input", () => {
        entry.value = valueInput.value;
        commit();
      });
      row.appendChild(valueInput);

      const removeBtn = document.createElement("button");
      removeBtn.type = "button";
      removeBtn.className = "settings-list-remove";
      removeBtn.textContent = "✕";
      removeBtn.addEventListener("click", () => {
        entries.splice(i, 1);
        commit();
        redraw();
      });
      row.appendChild(removeBtn);

      listEl.appendChild(row);
    });
  }
  redraw();

  const addBtn = document.createElement("button");
  addBtn.type = "button";
  addBtn.className = "settings-list-add";
  addBtn.textContent = "+ Add";
  addBtn.addEventListener("click", () => {
    entries.push({ key: "", value: "" });
    commit();
    redraw();
  });
  wrap.appendChild(addBtn);

  parentEl.appendChild(wrap);
}

function renderFields(value, pathParts, formRoot, liveFieldsSet, pathFieldsSet, parentEl) {
  if (value === null || typeof value !== "object") {
    renderScalarField(value, pathParts, formRoot, liveFieldsSet, pathFieldsSet, parentEl);
    return;
  }
  if (Array.isArray(value)) {
    renderListField(value, pathParts, formRoot, parentEl);
    return;
  }
  if (DICTIONARY_FIELDS.has(pathParts.join("."))) {
    renderDictionaryField(value, pathParts, formRoot, parentEl);
    return;
  }

  const group = document.createElement("div");
  group.className = "settings-group";
  const groupLabel = document.createElement("div");
  groupLabel.className = "settings-group-label";
  groupLabel.textContent = labelFor(pathParts);
  group.appendChild(groupLabel);

  for (const [key, childValue] of Object.entries(value)) {
    renderFields(childValue, [...pathParts, key], formRoot, liveFieldsSet, pathFieldsSet, group);
  }
  parentEl.appendChild(group);
}

// Compares the original GET payload against the current edited form state, returning the dotted
// paths of every leaf that actually changed - used to decide whether Save needs to show the
// "restart required" banner or just the lightweight "applied immediately" toast.
function diffPaths(original, current) {
  const changed = [];

  function walk(a, b, pathParts) {
    const aIsObj = a !== null && typeof a === "object" && !Array.isArray(a);
    const bIsObj = b !== null && typeof b === "object" && !Array.isArray(b);
    if (aIsObj && bIsObj) {
      const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
      for (const key of keys) walk(a[key], b[key], [...pathParts, key]);
      return;
    }
    if (Array.isArray(a) && Array.isArray(b)) {
      if (JSON.stringify(a) !== JSON.stringify(b)) changed.push(pathParts.join("."));
      return;
    }
    if (a !== b) changed.push(pathParts.join("."));
  }

  const comparableMcp = {};
  for (const [name, server] of Object.entries(original.mcpServers)) {
    comparableMcp[name] = { enabled: server.enabled, settings: server.settings };
  }
  walk({ agent: original.agent, mcpServers: comparableMcp }, current, []);
  return changed;
}

function showRestartBanner(banner) {
  banner.hidden = false;
  banner.className = "settings-banner settings-banner-restart";
  banner.innerHTML = "";

  const text = document.createElement("span");
  text.textContent = "Settings saved — restart UavOps.Agent to apply.";
  banner.appendChild(text);

  const shutdownBtn = document.createElement("button");
  shutdownBtn.type = "button";
  shutdownBtn.className = "settings-shutdown-btn";
  shutdownBtn.textContent = "Shut down now";
  shutdownBtn.addEventListener("click", async () => {
    shutdownBtn.disabled = true;
    shutdownBtn.textContent = "Shutting down…";
    try {
      await fetch("/api/shutdown", { method: "POST" });
      text.textContent = "Shut down. Relaunch UavOps.Agent to continue.";
    } catch {
      text.textContent = "Shutdown request failed — stop the process manually.";
      shutdownBtn.disabled = false;
      shutdownBtn.textContent = "Shut down now";
    }
  });
  banner.appendChild(shutdownBtn);
}

function showSavedToast(banner) {
  banner.hidden = false;
  banner.className = "settings-banner settings-banner-saved";
  banner.textContent = "Saved — applied immediately, no restart needed.";
}

async function handleSave(originalData, formState, liveFieldsSet, banner) {
  const missingPaths = pathValidationTargets.filter((t) => t.status.classList.contains("settings-path-missing"));
  if (missingPaths.length > 0) {
    const proceed = confirm(missingPaths.length + " path(s) don't exist on disk — save anyway?");
    if (!proceed) return;
  }

  const changedPaths = diffPaths(originalData, formState);
  const needsRestart = changedPaths.some((p) => !liveFieldsSet.has(p));

  const body = { agent: formState.agent, mcpServers: {} };
  for (const [name, server] of Object.entries(formState.mcpServers)) {
    body.mcpServers[name] = { enabled: server.enabled, settings: server.settings };
  }

  try {
    const res = await fetch("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error("HTTP " + res.status);

    const refreshed = await (await fetch("/api/settings")).json();
    renderSettings(refreshed, needsRestart ? "restart" : "saved");
  } catch (err) {
    banner.hidden = false;
    banner.className = "settings-banner settings-banner-error";
    banner.textContent = "Failed to save settings: " + err.message;
  }
}

function renderSettings(data, flash) {
  const formState = { agent: deepClone(data.agent), mcpServers: {} };
  for (const [name, server] of Object.entries(data.mcpServers)) {
    formState.mcpServers[name] = { enabled: server.enabled, settings: deepClone(server.settings) };
  }

  const liveFieldsSet = new Set(data.liveFields || []);
  const pathFieldsSet = new Set(data.pathFields || []);
  pathValidationTargets = [];

  const container = document.getElementById("settings-container");
  container.innerHTML = "";

  const heading = document.createElement("h2");
  heading.className = "settings-heading";
  heading.textContent = "Settings";
  container.appendChild(heading);

  const banner = document.createElement("div");
  banner.className = "settings-banner";
  banner.hidden = true;
  container.appendChild(banner);

  const agentSection = document.createElement("section");
  agentSection.className = "settings-domain";
  const agentTitle = document.createElement("h3");
  agentTitle.textContent = "Agent";
  agentSection.appendChild(agentTitle);
  renderFields(formState.agent, ["agent"], formState, liveFieldsSet, pathFieldsSet, agentSection);
  container.appendChild(agentSection);

  for (const [name, server] of Object.entries(data.mcpServers)) {
    const section = document.createElement("section");
    section.className = "settings-domain";

    const title = document.createElement("h3");
    title.textContent = "MCP · " + capitalize(name);
    section.appendChild(title);

    const enabledRow = document.createElement("label");
    enabledRow.className = "settings-field settings-field-enabled";
    const enabledLabel = document.createElement("span");
    enabledLabel.className = "settings-field-label";
    enabledLabel.textContent = "Enabled";
    enabledRow.appendChild(enabledLabel);
    const enabledInput = document.createElement("input");
    enabledInput.type = "checkbox";
    enabledInput.className = "settings-checkbox";
    enabledInput.checked = server.enabled;
    enabledInput.addEventListener("change", () => {
      formState.mcpServers[name].enabled = enabledInput.checked;
    });
    enabledRow.appendChild(enabledInput);
    appendLiveBadge(enabledRow, "mcpServers." + name + ".enabled", liveFieldsSet);
    section.appendChild(enabledRow);

    renderFields(formState.mcpServers[name].settings, ["mcpServers", name, "settings"], formState, liveFieldsSet, pathFieldsSet, section);
    container.appendChild(section);
  }

  const saveRow = document.createElement("div");
  saveRow.className = "settings-save-row";
  const saveBtn = document.createElement("button");
  saveBtn.type = "button";
  saveBtn.className = "settings-save-btn";
  saveBtn.textContent = "Save";
  saveBtn.addEventListener("click", () => handleSave(data, formState, liveFieldsSet, banner));
  saveRow.appendChild(saveBtn);
  container.appendChild(saveRow);

  if (flash === "restart") showRestartBanner(banner);
  else if (flash === "saved") showSavedToast(banner);
}
