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

function ensureAgentTurn(correlationId) {
  let turn = turns.get(correlationId);
  if (turn) {
    return turn;
  }

  const bubble = document.createElement("div");
  bubble.className = "message message-agent";
  bubble.innerHTML =
    '<div class="message-meta">MainAgent · <span class="duration">thinking…</span></div>' +
    '<div class="message-text">…</div>' +
    '<details class="trace"><summary>Reasoning (<span class="step-count">0</span> steps)</summary><div class="trace-steps"></div></details>';

  threadEl.appendChild(bubble);
  scrollToBottom();

  turn = {
    bubble,
    textEl: bubble.querySelector(".message-text"),
    durationEl: bubble.querySelector(".duration"),
    stepsEl: bubble.querySelector(".trace-steps"),
    stepCountEl: bubble.querySelector(".step-count"),
    stepCount: 0,
  };
  turns.set(correlationId, turn);
  return turn;
}

connection.on("ReceiveChatMessage", (user, text, duration, correlationId) => {
  if (user === "Operator") {
    return; // already rendered optimistically on submit
  }
  const turn = ensureAgentTurn(correlationId);
  turn.textEl.textContent = text;
  turn.durationEl.textContent = duration.toFixed(2) + "s";
});

connection.on("ReceiveAgentTrace", (correlationId, agent, tool, argsJson, result, durationSeconds) => {
  const turn = ensureAgentTurn(correlationId);
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

connection.on("ReceiveConfirmationRequest", (confirmationId, correlationId, agentName, operationId, argsJson) => {
  const turn = ensureAgentTurn(correlationId);
  const card = document.createElement("div");
  card.className = "confirmation";
  card.innerHTML =
    `<div><strong>${agentName}</strong> wants to call <code>${operationId}</code> with <code></code></div>` +
    '<div class="confirmation-buttons">' +
    '<button type="button" class="btn btn-approve" data-approve="true">Approve</button>' +
    '<button type="button" class="btn btn-decline" data-approve="false">Decline</button>' +
    "</div>";
  card.querySelector("code").textContent = argsJson;

  card.querySelectorAll("button").forEach((btn) => {
    btn.addEventListener("click", () => {
      const approved = btn.dataset.approve === "true";
      connection.invoke("SendConfirmationResponse", confirmationId, approved).catch(console.error);
      card.querySelectorAll("button").forEach((b) => (b.disabled = true));
      card.style.opacity = "0.6";
    });
  });

  turn.bubble.appendChild(card);
  scrollToBottom();
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
