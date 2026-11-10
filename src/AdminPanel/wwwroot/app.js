// Admin panel v0 (spec 7.4). Plain DOM, no framework; every value from the server is set as text, never as HTML.
"use strict";

// Texts for the API's reason codes (AdminReasonCode) and the outcomes of actions (AdminOutcome).
const REASONS = {
  NotSignedIn: "Please sign in.",
  RequestInvalid: "The request was not valid.",
  CharacterNotFound: "No character with that id in this realm.",
  RequestConflict: "That request id was already used for something else. Try again.",
  ServerUnavailable: "The database or Redis is unavailable. Try again shortly.",
  SignInUnavailable: "This panel has no admin sign-in configured (MMORPG_ADMIN_IDENTITY).",
  ActionNotFound: "That action was not found.",
};
const OUTCOMES = {
  Done: "done",
  Kicked: "kicked: the client was disconnected",
  NotOnline: "not online: no game server holds this character",
  NotConnected: "not connected: the character is lingering without a client",
  Expired: "expired: no game server answered in time",
  MailSent: "sent: the mail waits in the character's mailbox",
  Invalid: "refused: the game server's tables do not know an item or a count, or the character is deleted",
  MailboxFull: "refused: the mailbox is full (the player claims some mail first)",
};

const $ = (id) => document.getElementById(id);
let currentCharacter = null;
let pendingKick = null; // { characterId, requestId }: a retry after a failure resends the same id
let pendingMail = null; // the same for a compensation mail (WP-M4-3)

async function api(method, path, body) {
  const options = { method, headers: { Accept: "application/json" }, credentials: "same-origin" };
  if (method !== "GET") {
    options.headers["X-Admin-Request"] = "1";
    if (body !== undefined) {
      options.headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(body);
    }
  }
  const response = await fetch(path, options);
  let data;
  try {
    data = await response.json();
  } catch {
    data = { ok: false, reasonCode: "ServerUnavailable" };
  }
  if (data.reasonCode === "NotSignedIn") showSignIn();
  return data;
}

function say(text, isError) {
  const message = $("message");
  message.hidden = !text;
  message.textContent = text || "";
  message.classList.toggle("error", !!isError);
}

function refusal(data) {
  return REASONS[data.reasonCode] || `Refused (${data.reasonCode}).`;
}

function time(ms) {
  return ms ? new Date(ms).toISOString().replace("T", " ").replace("Z", "") : "";
}

function cell(row, text, className) {
  const td = row.insertCell();
  td.textContent = text == null ? "" : String(text);
  if (className) td.className = className;
  return td;
}

function showSignIn() {
  $("sign-in-view").hidden = false;
  $("panel-view").hidden = true;
  $("who").hidden = true;
}

function showPanel(admin) {
  $("sign-in-view").hidden = true;
  $("panel-view").hidden = false;
  $("who").hidden = false;
  $("admin-name").textContent = admin;
}

// ---- Sign in ----
$("sign-in-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const data = await api("POST", "/api/session", { name: $("sign-in-name").value });
  if (!data.ok) return say(refusal(data), true);
  say("");
  showPanel(data.admin);
});

$("sign-out").addEventListener("click", async () => {
  await api("DELETE", "/api/session");
  showSignIn();
});

// ---- Tabs ----
document.querySelectorAll("nav button").forEach((button) => {
  button.addEventListener("click", () => {
    document.querySelectorAll("nav button").forEach((b) => b.classList.toggle("active", b === button));
    document.querySelectorAll("[data-panel]").forEach((p) => (p.hidden = p.dataset.panel !== button.dataset.tab));
    if (button.dataset.tab === "logs") loadLogs();
    if (button.dataset.tab === "telemetry") loadTelemetry();
    if (button.dataset.tab === "economy") loadEconomy();
    if (button.dataset.tab === "actions") loadActions();
  });
});

// ---- Characters ----
$("search-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const query = new URLSearchParams();
  if ($("search-name").value.trim()) query.set("name", $("search-name").value.trim());
  if ($("search-account").value.trim()) query.set("account", $("search-account").value.trim());
  const data = await api("GET", `/api/characters?${query}`);
  if (!data.ok) return say(refusal(data), true);
  say(data.characters.length ? "" : "No characters found.");
  const body = $("results").tBodies[0];
  body.replaceChildren();
  for (const c of data.characters) {
    const row = body.insertRow();
    cell(row, c.characterId);
    cell(row, c.name);
    cell(row, c.accountId);
    cell(row, c.steamId);
    cell(row, c.classId);
    cell(row, c.level);
    cell(row, c.deleted ? "deleted" : c.loaded ? `online (${c.server})` : "offline", c.loaded ? "online" : "");
    const open = document.createElement("button");
    open.type = "button";
    open.textContent = "Open";
    open.addEventListener("click", () => openCharacter(c.characterId));
    row.insertCell().append(open);
  }
});

async function openCharacter(characterId) {
  const data = await api("GET", `/api/characters/${characterId}`);
  if (!data.ok) return say(refusal(data), true);
  say("");
  currentCharacter = data.character.summary;
  $("character").hidden = false;
  $("character-title").textContent = `${currentCharacter.name} (#${currentCharacter.characterId})`;
  $("kick-status").textContent = "";
  const fields = $("character-fields");
  fields.replaceChildren();
  const details = data.character;
  const shown = {
    Account: `${currentCharacter.accountId} (SteamID ${currentCharacter.steamId ?? "?"})`,
    "Class / level": `${currentCharacter.classId} / ${currentCharacter.level}`,
    Slot: currentCharacter.slot,
    State: currentCharacter.loaded ? `held by game server ${currentCharacter.server}` : "not loaded on any game server",
    "Last saved position": `${details.worldId} (${details.posXCm}, ${details.posYCm}) cm`,
    "Last saved HP / MP / stamina": `${details.hp} / ${details.mp} / ${details.stamina}`,
    "Saved version": `${details.version}, updated ${time(details.updatedAtUtcMs)} UTC`,
  };
  for (const [name, value] of Object.entries(shown)) {
    const dt = document.createElement("dt");
    dt.textContent = name;
    const dd = document.createElement("dd");
    dd.textContent = String(value);
    fields.append(dt, dd);
  }
  fillActions($("character-actions"), data.actions, false);
  loadCharacterTelemetry();
  loadCharacterLogs();
}

function fillActions(table, actions, withTarget) {
  const body = table.tBodies[0];
  body.replaceChildren();
  for (const a of actions) {
    const row = body.insertRow();
    cell(row, time(a.requestedAtUtcMs));
    cell(row, a.adminName);
    cell(row, a.action);
    if (withTarget) cell(row, a.targetCharacterId ? `character ${a.targetCharacterId}` : "");
    cell(row, a.state);
    cell(row, a.outcome);
    cell(row, a.detail, "wrap");
  }
}

$("kick-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!currentCharacter) return;
  const characterId = currentCharacter.characterId;
  if (!pendingKick || pendingKick.characterId !== characterId) {
    pendingKick = { characterId, requestId: crypto.randomUUID() };
  }
  $("kick-status").textContent = "Sending...";
  let data;
  try {
    data = await api("POST", `/api/characters/${characterId}/kick`, { requestId: pendingKick.requestId, note: $("kick-note").value });
  } catch {
    $("kick-status").textContent = "No answer from the panel. Press Kick again to retry the same request.";
    return;
  }
  if (!data.ok) {
    $("kick-status").textContent = refusal(data);
    if (data.reasonCode !== "ServerUnavailable") pendingKick = null;
    return;
  }
  const requestId = pendingKick.requestId;
  pendingKick = null;
  await followAction(requestId, data.action);
});

// Spec 7.4 "send compensation by mail" (WP-M4-3): items as "id[:count[:rarity]]", comma separated, and gold. The panel only
// records the request; a game server checks the items, writes the mail and the outcome.
function parseMailItems(text) {
  const items = [];
  for (const part of text.split(",").map((p) => p.trim()).filter((p) => p.length > 0)) {
    const [id, count, rarity] = part.split(":");
    items.push({ id, count: count === undefined ? 1 : Number(count), rarity: rarity === undefined ? 0 : Number(rarity) });
  }
  return items;
}

$("mail-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!currentCharacter) return;
  const characterId = currentCharacter.characterId;
  if (!pendingMail || pendingMail.characterId !== characterId) {
    pendingMail = { characterId, requestId: crypto.randomUUID() };
  }
  const body = {
    requestId: pendingMail.requestId,
    items: parseMailItems($("mail-items").value),
    gold: Number($("mail-gold").value || 0),
    note: $("mail-note").value,
  };
  $("mail-status").textContent = "Sending...";
  let data;
  try {
    data = await api("POST", `/api/characters/${characterId}/mail`, body);
  } catch {
    $("mail-status").textContent = "No answer from the panel. Press Send mail again to retry the same request.";
    return;
  }
  if (!data.ok) {
    $("mail-status").textContent = refusal(data);
    if (data.reasonCode !== "ServerUnavailable") pendingMail = null;
    return;
  }
  const requestId = pendingMail.requestId;
  pendingMail = null;
  await followAction(requestId, data.action, "mail-status", "Compensation");
});

async function followAction(requestId, action, statusId = "kick-status", label = "Kick") {
  for (let i = 0; action.state !== "done" && i < 120; i++) {
    $(statusId).textContent = `${label} ${action.state}, waiting for the game server...`;
    await new Promise((resolve) => setTimeout(resolve, 500));
    const data = await api("GET", `/api/actions/${requestId}`);
    if (!data.ok) {
      $(statusId).textContent = refusal(data);
      return;
    }
    action = data.action;
  }
  $(statusId).textContent = `${label} ${OUTCOMES[action.outcome] || action.outcome || action.state} (action ${action.actionId}).`;
  if (currentCharacter) openCharacter(currentCharacter.characterId);
}

async function loadCharacterTelemetry() {
  const data = await api("GET", `/api/telemetry?characterId=${currentCharacter.characterId}&limit=50`);
  $("character-telemetry").textContent = data.ok
    ? data.records.map((r) => JSON.stringify(r)).join("\n") || "No telemetry for this character in the newest files."
    : refusal(data);
}

async function loadCharacterLogs() {
  const data = await api("GET", `/api/logs?contains=${encodeURIComponent(currentCharacter.name)}&lines=50`);
  $("character-logs").textContent = data.ok
    ? data.lines.join("\n") || (data.file ? `No lines in ${data.file} name this character.` : "No server log files.")
    : refusal(data);
}

// ---- Logs ----
async function loadLogs() {
  const query = new URLSearchParams();
  if ($("logs-file").value) query.set("file", $("logs-file").value);
  if ($("logs-contains").value.trim()) query.set("contains", $("logs-contains").value.trim());
  const data = await api("GET", `/api/logs?${query}`);
  if (!data.ok) return say(refusal(data), true);
  const select = $("logs-file");
  select.replaceChildren(...data.files.map((f) => new Option(f, f, false, f === data.file)));
  $("logs-lines").textContent = data.lines.join("\n") || (data.file ? "No matching lines." : "No server log files in the log folder.");
}

$("logs-form").addEventListener("submit", (event) => {
  event.preventDefault();
  loadLogs();
});

// ---- Realm telemetry ----
async function loadTelemetry() {
  const kind = $("telemetry-kind").value.trim();
  const data = await api("GET", `/api/telemetry${kind ? `?kind=${encodeURIComponent(kind)}` : ""}`);
  if (!data.ok) return say(refusal(data), true);
  $("telemetry-lines").textContent = data.records.map((r) => JSON.stringify(r)).join("\n") || "No telemetry records.";
}

$("telemetry-form").addEventListener("submit", (event) => {
  event.preventDefault();
  loadTelemetry();
});

// ---- Economy (WP-M4-5) ----
const HOUR_MS = 3600000;

function hourLabel(ms) {
  return new Date(ms).toISOString().slice(0, 13).replace("T", " ") + ":00";
}

function headerRow(table, names) {
  const row = table.tHead.insertRow();
  names.forEach((name, i) => {
    const th = document.createElement("th");
    th.textContent = name;
    if (i > 0) th.className = "num";
    row.append(th);
  });
}

// The reasons that moved in the window, in enum order (data.reasons), then any name the ledger holds that this panel's
// enum lacks (a newer server): a new InventoryReason needs no page change.
function movedReasons(order, byReason) {
  const known = order.filter((r) => byReason[r]);
  return known.concat(Object.keys(byReason).filter((r) => !order.includes(r)));
}

function drawChart(data) {
  const chart = $("economy-chart");
  chart.replaceChildren();
  const byHour = new Map(data.hours.map((h) => [h.hourStartMs, h]));
  let max = 1;
  for (const h of data.hours) max = Math.max(max, h.goldCreated, h.goldDestroyed);
  for (let start = data.fromMs; start <= data.toMs; start += HOUR_MS) {
    const h = byHour.get(start) || { goldCreated: 0, goldDestroyed: 0 };
    const group = document.createElement("div");
    group.className = "hour";
    group.title = `${hourLabel(start)} UTC: created ${h.goldCreated}, destroyed ${h.goldDestroyed}`;
    const bars = document.createElement("div");
    bars.className = "bars";
    for (const [kind, value] of [["created", h.goldCreated], ["destroyed", h.goldDestroyed]]) {
      const bar = document.createElement("div");
      bar.className = `bar ${kind}`;
      bar.style.height = `${(100 * value) / max}%`;
      bars.append(bar);
    }
    const label = document.createElement("div");
    label.className = "label";
    label.textContent = new Date(start).toISOString().slice(11, 13);
    group.append(bars, label);
    chart.append(group);
  }
}

function fillEconomyHours(data) {
  const table = $("economy-hours-table");
  table.tHead.replaceChildren();
  table.tBodies[0].replaceChildren();
  table.tFoot.replaceChildren();
  const created = movedReasons(data.reasons, data.createdByReason);
  const destroyed = movedReasons(data.reasons, data.destroyedByReason);
  headerRow(table, ["Hour (UTC)", "Created", "Destroyed", "Net"]
    .concat(created.map((r) => `+ ${r}`), destroyed.map((r) => `- ${r}`)));
  const line = (row, label, h) => {
    cell(row, label);
    cell(row, h.goldCreated, "num");
    cell(row, h.goldDestroyed, "num");
    cell(row, h.goldCreated - h.goldDestroyed, "num");
    for (const r of created) cell(row, h.createdByReason[r] || 0, "num");
    for (const r of destroyed) cell(row, h.destroyedByReason[r] || 0, "num");
  };
  for (const h of data.hours) line(table.tBodies[0].insertRow(), hourLabel(h.hourStartMs), h);
  line(table.tFoot.insertRow(), "Total", data);
}

function fillSnapshots(snapshots) {
  const body = $("economy-snapshots").tBodies[0];
  body.replaceChildren();
  for (const s of snapshots.slice().reverse()) {
    const row = body.insertRow();
    cell(row, time(s.takenAtMs));
    cell(row, s.worldId, "num");
    cell(row, s.goldCreated, "num");
    cell(row, s.goldDestroyed, "num");
    cell(row, s.goldOnline, "num");
    cell(row, s.activePlayers, "num");
    cell(row, Object.entries(s.byReason)
      .map(([r, f]) => `${r} +${f.created}/-${f.destroyed}${f.itemsDestroyed ? ` (${f.itemsDestroyed} items)` : ""}`).join(", "), "wrap");
  }
}

async function loadEconomy() {
  const hours = $("economy-hours").value.trim();
  const data = await api("GET", `/api/economy${hours ? `?hours=${encodeURIComponent(hours)}` : ""}`);
  if (!data.ok) return say(refusal(data), true);
  say(data.hours.length || data.snapshots.length ? "" : `No faucet or sink rows and no snapshots in the last ${data.windowHours} hours.`);
  $("economy-hours").placeholder = String(data.windowHours);
  const live = data.live;
  $("economy-live").textContent = live
    ? `Live (world ${live.worldId}, ${time(live.t)} UTC, since the world started): created ${live.goldCreated}, destroyed ${live.goldDestroyed}, `
      + `gold online ${live.goldOnline}, active players ${live.activePlayers}.`
    : "No live economy totals in the newest telemetry files.";
  drawChart(data);
  fillEconomyHours(data);
  fillSnapshots(data.snapshots);
}

$("economy-form").addEventListener("submit", (event) => {
  event.preventDefault();
  loadEconomy();
});

// ---- Admin actions ----
async function loadActions() {
  const data = await api("GET", "/api/actions");
  if (!data.ok) return say(refusal(data), true);
  fillActions($("actions-table"), data.actions, true);
}

$("actions-refresh").addEventListener("click", loadActions);

// ---- Start ----
(async () => {
  const data = await api("GET", "/api/session");
  if (data.ok) showPanel(data.admin);
  else showSignIn();
})();
