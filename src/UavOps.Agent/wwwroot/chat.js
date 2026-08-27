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

function renderOperatorMessage(text) {
  const el = document.createElement("div");
  el.className = "message message-operator";
  el.innerHTML = '<div class="message-meta">Operator</div><div class="message-text"></div>';
  el.querySelector(".message-text").textContent = text;
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
  renderMessageText(turn.textEl, text);
  turn.durationEl.textContent = duration.toFixed(2) + "s";

  if (isResolution) {
    // A gate (ConfirmationGate/OperatorPromptGate) resolving or timing out reuses this same
    // correlationId for its follow-up message — any choice buttons offered for the prompt are no
    // longer valid to click, whether or not the operator actually used one.
    turn.choicesEl.innerHTML = "";
  }

  if (user === "BrainAgent") {
    // The turn's bubble is created as soon as its first trace event arrives, which can be well
    // before this final answer — if a confirmation prompt happens mid-turn, its own bubble gets
    // appended after the (still-empty) turn bubble and would otherwise stay below it forever,
    // making the finished turn look like it landed before the confirmation that happened during
    // it. Move the bubble to the end now so the thread reads in the order things actually
    // happened; re-appending an already-attached node just relocates it.
    threadEl.appendChild(turn.bubble);
    scrollToBottom();
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
connection.onreconnected(() => setStatus("connected", "status-connected"));
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
  } catch (err) {
    console.error(err);
    setStatus("disconnected", "status-disconnected");
    setTimeout(start, 3000);
  }
}
start();

function sendOperatorReply(text) {
  renderOperatorMessage(text);
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
