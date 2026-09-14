const STORAGE_KEY = "voice-test-harness-results";
const listenGraceMs = 500;   // let recognition arm before speech starts
const trailingGraceMs = 900; // let recognition catch trailing words after speech ends

let scenarios = [...DEFAULT_SCENARIOS];
let provider = BrowserProvider;
let results = loadResults();

const el = (id) => document.getElementById(id);

function log(msg) {
  const line = `[${new Date().toISOString().slice(11, 19)}] ${msg}`;
  console.log("[voice-test-harness]", msg);
  const panel = el("log-panel");
  panel.textContent += line + "\n";
  panel.scrollTop = panel.scrollHeight;
}

function loadResults() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) || "[]");
  } catch {
    return [];
  }
}

function saveResults() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(results));
}

function recordResult(entry) {
  results = results.filter((r) => r.id !== entry.id); // replace any prior run for this id
  results.push(entry);
  results.sort((a, b) => a.id.localeCompare(b.id));
  saveResults();
  renderResultsTable();
}

function renderResultsTable() {
  const tbody = el("results-body");
  tbody.innerHTML = "";
  for (const r of results) {
    const tr = document.createElement("tr");
    tr.className = r.wer === 0 ? "pass" : r.wer <= 0.15 ? "warn" : "fail";
    tr.innerHTML = `
      <td>${r.id}</td>
      <td>${escapeHtml(r.groundTruth)}</td>
      <td>${escapeHtml(r.transcript)}</td>
      <td>${(r.wer * 100).toFixed(1)}%</td>
      <td>${r.ttsMs != null ? r.ttsMs.toFixed(0) : "-"}</td>
      <td>${r.sttMs != null ? r.sttMs.toFixed(0) : "-"}</td>
      <td>${r.provider}</td>
    `;
    tbody.appendChild(tr);
  }
  el("results-summary").textContent = summarize(results);
}

function summarize(rs) {
  if (rs.length === 0) return "No results yet.";
  const avgWer = rs.reduce((s, r) => s + r.wer, 0) / rs.length;
  const passCount = rs.filter((r) => r.wer === 0).length;
  return `${rs.length} scenario(s) run — avg WER ${(avgWer * 100).toFixed(1)}%, ${passCount}/${rs.length} exact matches.`;
}

function escapeHtml(s) {
  return (s || "").replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
}

function renderScenarioRows() {
  const tbody = el("scenarios-body");
  tbody.innerHTML = "";
  for (const s of scenarios) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td>${s.id}</td>
      <td>${escapeHtml(s.text)}</td>
      <td>
        <button data-action="speak" data-id="${s.id}">Speak only</button>
        <button data-action="run" data-id="${s.id}">Run closed-loop</button>
      </td>
    `;
    tbody.appendChild(tr);
  }
  tbody.addEventListener("click", onScenarioButtonClick);
}

function onScenarioButtonClick(e) {
  const btn = e.target.closest("button[data-action]");
  if (!btn) return;
  const scenario = scenarios.find((s) => s.id === btn.dataset.id);
  if (!scenario) return;
  if (btn.dataset.action === "speak") {
    if (scenario.audioFile) {
      log(`Playing pre-recorded audio for scenario ${scenario.id}: "${scenario.text}"`);
      playAudioFile(scenario.audioFile)
        .then(() => log(`Playback finished for scenario ${scenario.id}`))
        .catch((err) => log(`playback error: ${err.message}`));
      return;
    }
    const speakText = scenario.speakText || scenario.text;
    log(`Speaking scenario ${scenario.id}: "${speakText}"`);
    provider.tts.speak(speakText)
      .then(() => log(`Speak finished for scenario ${scenario.id}`))
      .catch((err) => log(`speak error: ${err.message}`));
  } else if (btn.dataset.action === "run") {
    runClosedLoopScenario(scenario);
  }
}

async function runClosedLoopScenario(scenario) {
  log(`--- Scenario ${scenario.id}: "${scenario.text}" ---`);
  setBusy(true);
  let listening = false;
  try {
    provider.stt.onInterim?.((finalText, interim) => {
      el("live-transcript").textContent = (finalText + " " + interim).trim();
    });
    el("live-transcript").textContent = "";

    await provider.stt.startListening();
    listening = true;
    await sleep(listenGraceMs);

    const ttsStart = performance.now();
    if (scenario.audioFile) {
      await playAudioFile(scenario.audioFile);
    } else {
      await provider.tts.speak(scenario.speakText || scenario.text);
    }
    const ttsMs = performance.now() - ttsStart;

    await sleep(trailingGraceMs);

    const sttStart = performance.now();
    const transcript = await provider.stt.stopListening();
    listening = false;
    const sttMs = performance.now() - sttStart;

    const { wer } = computeWer(scenario.text, transcript);
    log(`Transcript: "${transcript}" | WER ${(wer * 100).toFixed(1)}%`);

    recordResult({
      id: scenario.id,
      groundTruth: scenario.text,
      transcript,
      wer,
      ttsMs,
      sttMs,
      provider: provider.name,
      timestamp: new Date().toISOString(),
    });
  } catch (err) {
    log(`ERROR in scenario ${scenario.id}: ${err.message}`);
    if (listening) {
      // Release the mic stream/AudioContext a failed TTS/STT call would otherwise leak —
      // a leaked getUserMedia stream from one failed scenario was observed to make the
      // *next* scenario's recording silently stall indefinitely.
      try { await provider.stt.stopListening(); } catch { /* best-effort cleanup only */ }
    }
  } finally {
    setBusy(false);
  }
}

async function runAllScenarios() {
  for (const s of scenarios) {
    await runClosedLoopScenario(s);
    await sleep(1000);
  }
  log("=== Run All complete ===");
}

function setBusy(isBusy) {
  document.querySelectorAll("button").forEach((b) => (b.disabled = isBusy));
  el("status-indicator").textContent = isBusy ? "Running..." : "Idle";
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Plays a static audio file through the speakers (same acoustic path as provider.tts.speak) -
// used for scenario sets that have no live TTS voice available (e.g. Hebrew - see
// scenarios-he.js's own comment for why).
function playAudioFile(url) {
  return new Promise((resolve, reject) => {
    const audio = new Audio(url);
    audio.onended = resolve;
    audio.onerror = () => reject(new Error(`failed to play ${url}`));
    audio.play().catch(reject);
  });
}

function exportResultsJson() {
  el("export-output").textContent = JSON.stringify(results, null, 2);
}

function resetResults() {
  results = [];
  saveResults();
  renderResultsTable();
  el("export-output").textContent = "";
  log("Results cleared.");
}

async function enableMicrophone() {
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    stream.getTracks().forEach((t) => t.stop());
    log("Microphone permission granted.");
    el("mic-status").textContent = "Microphone: granted";
  } catch (err) {
    log(`Microphone permission error: ${err.message}`);
    el("mic-status").textContent = "Microphone: denied/error";
  }
}

function applyProviderSelection() {
  const value = el("provider-select").value;
  if (value === "browser") {
    provider = BrowserProvider;
    el("custom-provider-config").hidden = true;
  } else {
    const sttUrl = el("custom-stt-url").value.trim();
    const ttsUrl = el("custom-tts-url").value.trim();
    provider = makeCustomProvider({ sttUrl, ttsUrl });
    el("custom-provider-config").hidden = false;
  }
  log(`Provider set to "${provider.name}".`);
}

function applyScenarioSetSelection() {
  const value = el("scenario-set-select").value;
  scenarios = value === "he" ? [...HEBREW_SCENARIOS] : [...DEFAULT_SCENARIOS];
  renderScenarioRows();
  log(`Scenario set: "${value}" (${scenarios.length} scenarios).`);
}

function init() {
  renderScenarioRows();
  renderResultsTable();
  el("enable-mic-btn").addEventListener("click", enableMicrophone);
  el("run-all-btn").addEventListener("click", runAllScenarios);
  el("reset-results-btn").addEventListener("click", resetResults);
  el("export-results-btn").addEventListener("click", exportResultsJson);
  el("provider-select").addEventListener("change", applyProviderSelection);
  el("apply-custom-provider-btn").addEventListener("click", applyProviderSelection);
  el("scenario-set-select").addEventListener("change", applyScenarioSetSelection);

  if (!("speechSynthesis" in window)) log("WARNING: speechSynthesis not supported in this browser.");
  if (!(window.SpeechRecognition || window.webkitSpeechRecognition)) {
    log("WARNING: SpeechRecognition not supported — use Chrome, and serve over http://localhost, not file://.");
  }
  log("Ready. Click \"Enable Microphone\" once, grant the permission prompt, then Run All / Run closed-loop.");
}

init();
