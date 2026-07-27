const connection = new signalR.HubConnectionBuilder()
  .withUrl("/chatHub")
  .withAutomaticReconnect()
  .build();

const statusEl = document.getElementById("connectionStatus");
const messagesEl = document.getElementById("messages");
const threadEl = document.getElementById("thread");
const form = document.getElementById("composer");
const input = document.getElementById("messageInput");

// correlationId -> { bubble, textEl, durationEl, stepsEl, stepCountEl, stepCount }
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
    '<div class="message-meta"><span class="agent-name"></span> · <span class="duration">thinking…</span></div>' +
    '<div class="message-text">…</div>' +
    '<details class="trace"><summary>Reasoning (<span class="step-count">0</span> steps)</summary><div class="trace-steps"></div></details>';

  threadEl.appendChild(bubble);
  scrollToBottom();

  turn = {
    bubble,
    agentNameEl: bubble.querySelector(".agent-name"),
    textEl: bubble.querySelector(".message-text"),
    durationEl: bubble.querySelector(".duration"),
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
  const turn = ensureAgentTurn(correlationId, user);
  turn.textEl.textContent = text;
  turn.durationEl.textContent = duration.toFixed(2) + "s";

  if (user === "MainAgent") {
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
  const turn = ensureAgentTurn(correlationId, "MainAgent");
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
});

connection.onreconnecting(() => setStatus("reconnecting…", "status-connecting"));
connection.onreconnected(() => setStatus("connected", "status-connected"));
connection.onclose(() => setStatus("disconnected", "status-disconnected"));

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

form.addEventListener("submit", (evt) => {
  evt.preventDefault();
  const text = input.value.trim();
  if (!text) {
    return;
  }
  input.value = "";
  renderOperatorMessage(text);
  const correlationId = newId();
  connection.invoke("SendMessage", "Operator", text, correlationId).catch(console.error);
});
