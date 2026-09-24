const connection = new signalR.HubConnectionBuilder()
  .withUrl("/chatHub")
  .withAutomaticReconnect({
    // Default withAutomaticReconnect() gives up after 4 attempts (~42s) and never retries again —
    // too short if the server process was stopped and takes a while to relaunch. Retry forever
    // instead, with a capped backoff, since a never-returning-null policy never gives up.
    nextRetryDelayInMilliseconds: (retryContext) => Math.min(2000 * (retryContext.previousRetryCount + 1), 30000),
  })
  .build();

const statusEl = document.getElementById("connectionStatus");
const messagesEl = document.getElementById("messages");
const threadEl = document.getElementById("thread");
const form = document.getElementById("composer");
const input = document.getElementById("messageInput");
const micButton = document.getElementById("micButton");
const speakToggle = document.getElementById("speakToggle");

// correlationId -> { bubble, textEl, durationEl, traceEl, stepsEl, stepCountEl, stepCount, choicesEl }
const turns = new Map();

function setStatus(text, cls) {
  statusEl.textContent = text;
  statusEl.className = "status " + cls;
}

function newId() {
  return Math.random().toString(36).slice(2, 10);
}

function scrollToBottom() {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

// Renders a message's text into `container`, turning ```lang\n...\n``` fenced blocks into actual
// <pre><code> elements instead of showing the literal backtick markdown as plain text — the only
// markdown construct agent replies use today (e.g. WatchdogConfigAgent's proactive config
// snippets), so a small targeted parser here avoids pulling in a full markdown library. Code
// content is always set via textContent, never innerHTML, so it can't be interpreted as markup.
function renderMessageText(container, text) {
  container.innerHTML = "";
  const codeBlockPattern = /```(\w*)\n([\s\S]*?)```/g;
  let lastIndex = 0;
  let match;

  while ((match = codeBlockPattern.exec(text)) !== null) {
    if (match.index > lastIndex) {
      container.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
    }

    const pre = document.createElement("pre");
    const code = document.createElement("code");
    if (match[1]) {
      code.className = "language-" + match[1];
    }
    code.textContent = match[2].replace(/\n$/, "");
    pre.appendChild(code);
    container.appendChild(pre);

    lastIndex = codeBlockPattern.lastIndex;
  }

  if (lastIndex < text.length) {
    container.appendChild(document.createTextNode(text.slice(lastIndex)));
  }
}

// Same brain-glyph path data as graph.js's brainIconDataUri, inlined as literal <svg> markup here
// (rather than a canvas data URI, since this is real DOM) so the agent avatar matches the graph
// page's root-agent icon for a bit of visual continuity between the two views.
const AGENT_AVATAR_SVG =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">' +
  '<path d="M9 3.5c-1.7 0-3 1.3-3 3 0 .3 0 .6.1.9C4.7 8 4 9.1 4 10.3c0 1 .5 1.9 1.2 2.4-.4.6-.7 1.3-.7 2.1 0 1.8 1.4 3.3 3.1 3.4.3 1.3 1.5 2.3 3 2.3.6 0 1.2-.2 1.7-.5"/>' +
  '<path d="M15 3.5c1.7 0 3 1.3 3 3 0 .3 0 .6-.1.9 1.4.6 2.1 1.7 2.1 2.9 0 1-.5 1.9-1.2 2.4.4.6.7 1.3.7 2.1 0 1.8-1.4 3.3-3.1 3.4-.3 1.3-1.5 2.3-3 2.3-.6 0-1.2-.2-1.7-.5"/>' +
  '<path d="M12 3.5v17"/>' +
  '<path d="M9 8.2c.9.4 1.4 1.2 1.4 2.1 0 .7-.3 1.3-.8 1.8"/>' +
  '<path d="M15 8.2c-.9.4-1.4 1.2-1.4 2.1 0 .7.3 1.3.8 1.8"/>' +
  "</svg>";

// rawText is only passed for a voice-originated message (see micButton's click handler) and only
// rendered when it actually differs from the corrected text - a typed message, or a voice message
// grammar-fix left untouched, shows no "as heard" line at all.
function renderOperatorMessage(text, rawText) {
  const el = document.createElement("div");
  el.className = "message message-operator";
  el.innerHTML = '<div class="message-meta">Operator</div><div class="message-text"></div>';
  el.querySelector(".message-text").textContent = text;

  if (rawText && rawText.trim() && rawText.trim() !== text.trim()) {
    const heard = document.createElement("div");
    heard.className = "message-heard";
    heard.textContent = `As heard: "${rawText.trim()}"`;
    el.appendChild(heard);
  }

  threadEl.appendChild(el);
  scrollToBottom();
}

function ensureAgentTurn(correlationId, agentName) {
  let turn = turns.get(correlationId);
  if (turn) {
    return turn;
  }

  const bubble = document.createElement("div");
  bubble.className = "message message-agent";
  bubble.innerHTML =
    '<div class="message-row">' +
    '<span class="avatar avatar-agent" aria-hidden="true">' + AGENT_AVATAR_SVG + "</span>" +
    '<div class="message-body">' +
    '<div class="message-meta"><span class="agent-name"></span> · <span class="duration">thinking…</span></div>' +
    '<div class="message-text">…</div>' +
    '<div class="choice-options"></div>' +
    '<details class="trace"><summary>Reasoning (<span class="step-count">0</span> steps)</summary><div class="trace-steps"></div></details>' +
    "</div>" +
    "</div>";

  threadEl.appendChild(bubble);
  scrollToBottom();

  turn = {
    bubble,
    agentNameEl: bubble.querySelector(".agent-name"),
    textEl: bubble.querySelector(".message-text"),
    durationEl: bubble.querySelector(".duration"),
    choicesEl: bubble.querySelector(".choice-options"),
    traceEl: bubble.querySelector(".trace"),
    stepsEl: bubble.querySelector(".trace-steps"),
    stepCountEl: bubble.querySelector(".step-count"),
    stepCount: 0,
  };
  turn.agentNameEl.textContent = agentName;
  turns.set(correlationId, turn);
  return turn;
}

// A confirmation prompt/resolution arrives on this same event, under its own correlationId, so
// it renders as its own bubble (from whichever agent asked) rather than overwriting the turn
// that triggered it — see ConfirmationGate.
connection.on("ReceiveChatMessage", (user, text, duration, correlationId) => {
  if (user === "Operator") {
    return; // already rendered optimistically on submit
  }
  const isResolution = turns.has(correlationId);
  const turn = ensureAgentTurn(correlationId, user);

  if (isResolution) {
    // A gate (ConfirmationGate/OperatorPromptGate) resolving, re-prompting after an unrecognized
    // reply, or timing out reuses this same correlationId for its follow-up message(s). Appending
    // as a new line rather than overwriting turn.textEl keeps the original question visible —
    // replacing it left a bare "Got it — proceeding with: No." with no indication what it was
    // answering. Each follow-up gets its own line, so a re-prompt exchange still reads in order.
    const followUp = document.createElement("div");
    followUp.className = "message-text message-resolution";
    renderMessageText(followUp, text);
    // Insert right before the choices row, not right after textEl - so a second follow-up (e.g. a
    // re-prompt, then its eventual resolution) lands after the first one instead of before it.
    turn.choicesEl.insertAdjacentElement("beforebegin", followUp);
    turn.choicesEl.innerHTML = "";
  } else {
    renderMessageText(turn.textEl, text);
  }
  turn.durationEl.textContent = duration.toFixed(2) + "s";

  if (user === "BrainAgent") {
    // The turn's bubble is created as soon as its first trace event arrives, which can be well
    // before this final answer — if a confirmation prompt happens mid-turn, its own bubble gets
    // appended after the (still-empty) turn bubble and would otherwise stay below it forever,
    // making the finished turn look like it landed before the confirmation that happened during
    // it. Move the bubble to the end now so the thread reads in the order things actually
    // happened; re-appending an already-attached node just relocates it.
    threadEl.appendChild(turn.bubble);
    scrollToBottom();

    if (speakEnabled) {
      queueSpeak(text);
    }
  }
});

connection.on("ReceiveAgentTrace", (correlationId, agent, tool, argsJson, result, durationSeconds) => {
  const turn = ensureAgentTurn(correlationId, "BrainAgent");
  const step = document.createElement("div");
  step.className = "trace-step";

  const agentSpan = document.createElement("span");
  agentSpan.className = "agent";
  agentSpan.textContent = `[${agent}]`;

  const toolSpan = document.createElement("span");
  toolSpan.className = "tool";
  toolSpan.textContent = tool;

  step.appendChild(agentSpan);
  step.append(" ");
  step.appendChild(toolSpan);
  step.append(`(${argsJson}) → ${result} · ${durationSeconds.toFixed(2)}s`);

  turn.stepsEl.appendChild(step);
  turn.stepCount += 1;
  turn.stepCountEl.textContent = String(turn.stepCount);
  turn.traceEl.classList.add("has-steps"); // hidden until there's at least one step to show
});

// Sent alongside a ConfirmationGate/OperatorPromptGate prompt's ReceiveChatMessage, under the same
// correlationId, so the operator can click an answer instead of having to type it. A click submits
// that exact option text through the same path as typing it — see sendOperatorReply.
connection.on("ReceiveChoices", (correlationId, options) => {
  const turn = turns.get(correlationId);
  if (!turn) {
    return;
  }

  turn.choicesEl.innerHTML = "";
  for (const option of options) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "choice-option";
    button.textContent = option;
    button.addEventListener("click", () => {
      turn.choicesEl.querySelectorAll("button").forEach((b) => (b.disabled = true));
      sendOperatorReply(option);
    });
    turn.choicesEl.appendChild(button);
  }
});

connection.onreconnecting(() => setStatus("reconnecting…", "status-connecting"));
connection.onreconnected(() => {
  setStatus("connected", "status-connected");
  reportChatActivity();
});
// onclose fires once automatic reconnect gives up (or the connection was never established) — the
// retry policy above never gives up on its own, but restart here too as a safety net so the page
// always keeps trying to get back to a connected state without a manual refresh.
connection.onclose(() => {
  setStatus("disconnected", "status-disconnected");
  start();
});

async function start() {
  try {
    await connection.start();
    setStatus("connected", "status-connected");
    reportChatActivity();
  } catch (err) {
    console.error(err);
    setStatus("disconnected", "status-disconnected");
    setTimeout(start, 3000);
  }
}
start();

function sendOperatorReply(text, rawText) {
  renderOperatorMessage(text, rawText);
  const correlationId = newId();
  connection.invoke("SendMessage", "Operator", text, correlationId).catch(console.error);
}

form.addEventListener("submit", (evt) => {
  evt.preventDefault();
  const text = input.value.trim();
  if (!text) {
    return;
  }
  input.value = "";
  sendOperatorReply(text);
});

// ---- Voice: speak a command (mic → Voice/VoiceEndpoints' /v1/audio/transcriptions) ----------
// Manual PCM capture + WAV encoding, as in eval/voice-test-harness's providers.js, not
// MediaRecorder's default webm/opus container — kept consistent with what's already proven to
// round-trip correctly through this same backend (whisper-server's audio decode path). Capture
// runs in an AudioWorklet (mic-capture-worklet.js) rather than the harness's ScriptProcessorNode,
// which Chrome flags as deprecated on every recording; the samples and WAV are the same either way. Unlike that harness's acoustic-loopback test, echoCancellation is left at its
// default (true) here — this is a real operator speaking into a real mic, not a speaker-to-mic
// digital loopback, so the normal echo/noise suppression a browser mic applies is exactly what's
// wanted.
function encodeWav(samples, sampleRate) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const writeStr = (offset, str) => { for (let i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i)); };

  writeStr(0, "RIFF");
  view.setUint32(4, 36 + samples.length * 2, true);
  writeStr(8, "WAVE");
  writeStr(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeStr(36, "data");
  view.setUint32(40, samples.length * 2, true);

  let offset = 44;
  for (let i = 0; i < samples.length; i++, offset += 2) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Blob([buffer], { type: "audio/wav" });
}

let micState = "idle"; // "idle" | "starting" | "recording" | "transcribing"
let micStream = null;
let micAudioCtx = null;
let micSourceNode = null;
let micCaptureNode = null; // AudioWorkletNode running mic-capture-worklet.js
let micPcmChunks = [];
let micStopRequested = false; // a release that arrived while still "starting"
let micMaxDurationTimer = null;

// A joystick-started recording stops on its own after this long, in case the release event is
// ever lost (e.g. this tab's connection dropped while the button was held).
const REMOTE_MIC_MAX_MS = 60000;

function setMicState(next) {
  micState = next;
  micButton.classList.toggle("is-recording", next === "starting" || next === "recording");
  micButton.classList.toggle("is-transcribing", next === "transcribing");
}

function releaseMicResources() {
  micCaptureNode?.disconnect();
  micSourceNode?.disconnect();
  micStream?.getTracks().forEach((t) => t.stop());
  if (micAudioCtx && micAudioCtx.state !== "closed") micAudioCtx.close();
  micStream = micAudioCtx = micSourceNode = micCaptureNode = null;
}

// Asks the worklet for its last partial batch and resolves once it has arrived (see
// mic-capture-worklet.js). Bounded, so a worklet that somehow never answers can't hang a stop.
function flushMicCapture() {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, 500);
    micCaptureNode.port.onmessage = (e) => {
      if (e.data === "flushed") {
        clearTimeout(timer);
        resolve();
      } else {
        micPcmChunks.push(e.data);
      }
    };
    micCaptureNode.port.postMessage("flush");
  });
}

async function startRecording() {
  micStream = await navigator.mediaDevices.getUserMedia({ audio: true });
  micAudioCtx = new AudioContext();
  // Chrome's autoplay policy creates an AudioContext "suspended" if this page hasn't had a user
  // gesture yet - normal for a joystick press (no click here at all) in a tab the operator never
  // clicked. resume() then never settles, so don't wait on it forever; a suspended context would
  // just record silence.
  if (micAudioCtx.state !== "running") {
    await Promise.race([micAudioCtx.resume(), new Promise((r) => setTimeout(r, 500))]);
  }
  if (micAudioCtx.state !== "running") {
    releaseMicResources();
    throw new Error("audio-blocked");
  }
  try {
    await micAudioCtx.audioWorklet.addModule("mic-capture-worklet.js");
  } catch (err) {
    releaseMicResources();
    throw err;
  }
  micSourceNode = micAudioCtx.createMediaStreamSource(micStream);
  micCaptureNode = new AudioWorkletNode(micAudioCtx, "mic-capture", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
  micPcmChunks = [];
  micCaptureNode.port.onmessage = (e) => micPcmChunks.push(e.data);
  micSourceNode.connect(micCaptureNode);
  // The worklet never writes its output (silence), but keeping it wired to the destination keeps
  // it in the graph the browser actually renders - same reason the ScriptProcessorNode was.
  micCaptureNode.connect(micAudioCtx.destination);
}

async function stopRecordingAndTranscribe() {
  micSourceNode.disconnect();
  await flushMicCapture();
  const sampleRate = micAudioCtx.sampleRate;
  releaseMicResources();

  const totalLength = micPcmChunks.reduce((sum, c) => sum + c.length, 0);
  const merged = new Float32Array(totalLength);
  let offset = 0;
  for (const chunk of micPcmChunks) { merged.set(chunk, offset); offset += chunk.length; }

  const wavBlob = encodeWav(merged, sampleRate);
  const formData = new FormData();
  formData.append("file", wavBlob, "clip.wav");
  const res = await fetch("/v1/audio/transcriptions", { method: "POST", body: formData });
  if (!res.ok) throw new Error(`Transcription failed: ${res.status}`);
  const json = await res.json();
  return { text: (json.text || "").trim(), rawText: (json.rawText || "").trim() };
}

function renderNotice(text) {
  const el = document.createElement("div");
  el.className = "message message-notice";
  el.textContent = text;
  threadEl.appendChild(el);
  scrollToBottom();
}

async function beginRecording({ remote = false } = {}) {
  if (micState !== "idle") return;
  setMicState("starting");
  micStopRequested = false;
  try {
    await startRecording();
  } catch (err) {
    console.error("mic start error", err);
    setMicState("idle");
    if (err.message === "audio-blocked") {
      renderNotice("Push-to-talk couldn't turn the mic on: the browser blocks audio until this page has been clicked once. Click anywhere in this window, then press the button again.");
    }
    return;
  }
  setMicState("recording");
  if (remote) micMaxDurationTimer = setTimeout(finishRecording, REMOTE_MIC_MAX_MS);
  if (micStopRequested) await finishRecording();
}

async function finishRecording() {
  if (micState === "starting") {
    micStopRequested = true; // beginRecording finishes this as soon as the mic is actually up
    return;
  }
  if (micState !== "recording") return;
  clearTimeout(micMaxDurationTimer);
  setMicState("transcribing");
  try {
    const { text, rawText } = await stopRecordingAndTranscribe();
    setMicState("idle");
    if (text) {
      // Sent directly, not via input.value + requestSubmit() - that path only carries the
      // final text through <form>'s submit event, with nowhere to also carry rawText for the
      // "as heard" line (see renderOperatorMessage).
      sendOperatorReply(text, rawText);
    }
  } catch (err) {
    console.error("transcription error", err);
    setMicState("idle");
  }
}

micButton.addEventListener("click", () => {
  if (micState === "idle") beginRecording();
  else if (micState === "recording") finishRecording();
  // "starting"/"transcribing" ignore further clicks until the in-flight step resolves.
});

// ---- Voice: push-to-talk from the fleet app's joystick (see Voice/PushToTalkRouter.cs) --------
// The host sends SetMicActive only to the chat tab used most recently, by this tab's own report of
// when the operator last clicked or typed in it - so a press never records in two tabs at once.
let lastActivityUnixMs = Date.now();
let lastActivityReportMs = 0;
let activityReportTimer = null;

function reportChatActivity() {
  if (connection.state !== signalR.HubConnectionState.Connected) return;
  lastActivityReportMs = Date.now();
  connection.invoke("ReportChatActivity", lastActivityUnixMs).catch(console.error);
}

// At most one report per 2s while typing, but always a trailing one - the *last* click/keypress
// before the operator moves to another tab is exactly the one that decides which tab is newest.
function onOperatorActivity() {
  lastActivityUnixMs = Date.now();
  const sinceLastReport = Date.now() - lastActivityReportMs;
  if (sinceLastReport > 2000) {
    reportChatActivity();
  } else if (!activityReportTimer) {
    activityReportTimer = setTimeout(() => {
      activityReportTimer = null;
      reportChatActivity();
    }, 2000 - sinceLastReport);
  }
}

window.addEventListener("pointerdown", onOperatorActivity, true);
window.addEventListener("keydown", onOperatorActivity, true);

connection.on("SetMicActive", (active) => {
  if (active) beginRecording({ remote: true });
  else finishRecording();
});

// ---- Voice: hear replies (BrainAgent's final answer → Voice/VoiceEndpoints' /v1/audio/speech) --
const SPEAK_KEY = "uavops-speak-replies";
let speakEnabled = localStorage.getItem(SPEAK_KEY) === "true";
speakToggle.classList.toggle("is-active", speakEnabled);
speakToggle.setAttribute("aria-pressed", String(speakEnabled));

speakToggle.addEventListener("click", () => {
  speakEnabled = !speakEnabled;
  localStorage.setItem(SPEAK_KEY, String(speakEnabled));
  speakToggle.classList.toggle("is-active", speakEnabled);
  speakToggle.setAttribute("aria-pressed", String(speakEnabled));
});

// Splits a reply into short chunks for separate TTS calls, played back one after another,
// instead of sending the whole reply as a single synthesis request - real, live-observed this
// session: Chatterbox-Turbo on a long, structured reply (a 5-item numbered summary) produced
// audio that duplicated one line, dropped a digit from another, and stopped entirely before the
// closing sentence - confirmed by transcribing the generated clip back and finding the ending
// genuinely wasn't there, not a playback/truncation issue on the browser side. Splitting on
// line breaks first (paragraph/list-item boundaries already break most replies into short-enough
// pieces on their own) with a sentence-level fallback for anything still long keeps each
// individual synthesis request short enough that this instability hasn't been reproduced since.
function splitIntoSpeechChunks(text) {
  const MAX_CHUNK_LENGTH = 200;
  const chunks = [];
  for (const line of text.split(/\n+/)) {
    const trimmedLine = line.trim();
    if (!trimmedLine) {
      continue;
    }
    if (trimmedLine.length <= MAX_CHUNK_LENGTH) {
      chunks.push(trimmedLine);
      continue;
    }
    const sentences = trimmedLine.match(/[^.!?]+[.!?]+(\s|$)/g) || [trimmedLine];
    for (const sentence of sentences) {
      const trimmedSentence = sentence.trim();
      if (trimmedSentence) {
        chunks.push(trimmedSentence);
      }
    }
  }
  return chunks;
}

async function speakChunk(chunk) {
  const res = await fetch("/v1/audio/speech", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ input: chunk }),
  });
  if (!res.ok) throw new Error(`Speech synthesis failed: ${res.status}`);
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  try {
    const audio = new Audio(url);
    await new Promise((resolve, reject) => {
      audio.onended = resolve;
      audio.onerror = () => reject(new Error("audio playback failed"));
      audio.play().catch(reject);
    });
  } finally {
    URL.revokeObjectURL(url);
  }
}

async function speakText(text) {
  if (!text) {
    return;
  }
  for (const chunk of splitIntoSpeechChunks(text)) {
    try {
      await speakChunk(chunk);
    } catch (err) {
      // One bad chunk shouldn't silence the rest of the reply - log and keep going.
      console.error("speak error", err, "chunk:", chunk);
    }
  }
}

// Every ReceiveChatMessage handler call below fires speech independently and was never awaited
// by its caller (a SignalR event handler can't block the connection on how long TTS playback
// takes) - real, live-observed this session: two BrainAgent messages arriving close together
// (e.g. a confirmation prompt immediately followed by its own resolution) each started their own
// speakText() call with no coordination between them, so their audio played concurrently and
// overlapped instead of one after another. This single shared promise chain serializes every
// queued reply's speech across ALL messages, not just chunks within one message - each new call
// waits for everything already queued to finish first.
let speechQueue = Promise.resolve();
function queueSpeak(text) {
  speechQueue = speechQueue.then(() => speakText(text)).catch(err => console.error("speak queue error", err));
}
