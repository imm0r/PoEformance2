// The page's behaviour: ask the host for state, render whole states when they arrive.
// Rendering is replace-not-patch on purpose - the host sends complete states, so the UI
// can never drift out of sync with a missed delta.

import { bridge } from "./bridge.js";

const $ = (id) => document.getElementById(id);

// The last auto-flask block the host sent. Edits are applied to a COPY of this and posted
// back whole; the host normalises it and answers with a fresh state, which re-renders.
// So the page never holds settings the host has not accepted.
let flasks = null;

function renderState(s) {
  $("tool-version").textContent = s.toolVersion;
  $("s-attached").innerHTML = s.attached
    ? `<span class="ok">attached</span> (pid ${s.processId})`
    : `<span class="bad">not attached</span>`;
  $("s-ingame").textContent = s.inGame ? "yes" : "no";
  $("s-entities").textContent = s.inGame ? String(s.entityCount) : "–";
  $("s-statics").textContent = `${s.staticsFound} / ${s.staticsTotal}`;
  $("s-schema").textContent = s.gameVersion;

  if (s.autoFlask) renderFlasks(s.autoFlask);
}

// ── Auto flask ─────────────────────────────────────────────────────────────

const VITALS = ["Life", "Mana", "EnergyShield"];
const VITAL_LABELS = { Life: "Life", Mana: "Mana", EnergyShield: "Energy shield" };

function renderFlasks(af) {
  flasks = af;

  $("af-enabled").checked = af.enabled;
  $("af-status").textContent = af.status;
  $("af-keysource").textContent = `Keys: ${af.keySource}`;

  // Rebuilding the table replaces its controls, which would yank focus out of whatever is
  // being edited and drop a half-typed number. A poll arriving mid-edit therefore updates
  // the live text above and leaves the rows alone until the field is left.
  if ($("af-slots").contains(document.activeElement)) return;

  const rows = $("af-rows");
  rows.replaceChildren();

  for (const slot of af.slots) {
    // A charm is triggered by the game itself - there is no key that uses one, so arming
    // this slot could only ever send keystrokes that do nothing. Say so and lock the row
    // rather than letting it look armed.
    const locked = slot.isCharm || slot.key === "unbound";

    const tr = document.createElement("tr");
    if (slot.isCharm) tr.className = "af-charm";

    tr.appendChild(cell(checkbox(slot.enabled, locked, (on) => update(slot.slot, { enabled: on }))));
    tr.appendChild(cell(text(String(slot.slot))));
    tr.appendChild(cell(equipped(slot)));
    tr.appendChild(cell(vitalPicker(slot, locked)));
    tr.appendChild(cell(thresholdInput(slot, locked)));
    tr.appendChild(cell(keyLabel(slot)));
    rows.appendChild(tr);
  }
}

function cell(child) {
  const td = document.createElement("td");
  td.appendChild(child);
  return td;
}

function text(value, className) {
  const span = document.createElement("span");
  span.textContent = value;
  if (className) span.className = className;
  return span;
}

function checkbox(checked, disabled, onChange) {
  const box = document.createElement("input");
  box.type = "checkbox";
  box.checked = checked;
  box.disabled = disabled;
  box.addEventListener("change", () => onChange(box.checked));
  return box;
}

function equipped(slot) {
  if (slot.isCharm) return text(`${slot.item} (charm)`, "af-empty");
  if (!slot.item) return text("empty", "af-empty");
  return text(slot.charges ? `${slot.item}  ${slot.charges}` : slot.item, "af-item");
}

function vitalPicker(slot, disabled) {
  const select = document.createElement("select");
  select.disabled = disabled;
  for (const vital of VITALS) {
    const option = document.createElement("option");
    option.value = vital;
    option.textContent = VITAL_LABELS[vital];
    option.selected = vital === slot.vital;
    select.appendChild(option);
  }
  select.addEventListener("change", () => update(slot.slot, { vital: select.value }));
  return select;
}

function thresholdInput(slot, disabled) {
  const input = document.createElement("input");
  input.type = "number";
  input.min = 1;
  input.max = 100;
  input.value = slot.thresholdPercent;
  input.disabled = disabled;
  // On "change", not "input": a partially typed number ("3" on the way to "35") would
  // otherwise be sent and saved as a real threshold.
  input.addEventListener("change", () =>
    update(slot.slot, { thresholdPercent: Number(input.value) }));
  return input;
}

function keyLabel(slot) {
  if (slot.isCharm) return text("self-triggering", "af-empty");
  if (slot.key === "unbound") return text("unbound", "bad");
  return text(slot.key, "af-key");
}

/** Applies one change to one slot and posts the whole settings object back. */
function update(slot, change) {
  if (!flasks) return;
  send({
    ...flasks,
    slots: flasks.slots.map((s) => (s.slot === slot ? { ...s, ...change } : s)),
  });
}

/** Posts settings to the host, stripped down to what the settings record actually holds. */
function send(af) {
  bridge.send({
    type: "setFlaskSettings",
    payload: {
      enabled: af.enabled,
      slots: af.slots.map((s) => ({
        slot: s.slot,
        enabled: s.enabled,
        vital: s.vital,
        thresholdPercent: s.thresholdPercent,
      })),
    },
  });
}

$("af-enabled").addEventListener("change", () => {
  if (flasks) send({ ...flasks, enabled: $("af-enabled").checked });
});

// ── Wiring ─────────────────────────────────────────────────────────────────

bridge.on("state", renderState);

// The raw traffic view, newest first, capped so a long session cannot grow it unbounded.
const log = $("log");
bridge.onAny((message) => {
  const line = `${new Date().toLocaleTimeString()}  ${JSON.stringify(message)}`;
  log.textContent = `${line}\n${log.textContent}`.split("\n").slice(0, 200).join("\n");
});

$("refresh").addEventListener("click", () => bridge.send({ type: "getState" }));

if (bridge.connected) {
  bridge.send({ type: "hello" });

  // The status line and the charge counts are live values, so the panel keeps itself
  // current instead of waiting to be asked. Slow on purpose: each poll re-reads the world.
  setInterval(() => bridge.send({ type: "getState" }), 1000);
} else {
  // Opened in a plain browser for UI work: render a fake state so layout is editable
  // without the host running.
  renderState({
    toolVersion: "browser preview",
    gameVersion: "(no host - open via PoEformance.App --config)",
    attached: false,
    processId: 0,
    inGame: false,
    entityCount: 0,
    staticsFound: 0,
    staticsTotal: 6,
    autoFlask: {
      enabled: false,
      keySource: "Defaults - no host",
      status: "not started",
      slots: [
        { slot: 1, enabled: false, vital: "Life", thresholdPercent: 65, key: "1", item: "FlaskLife1", charges: "42/9", isCharm: false },
        { slot: 2, enabled: true, vital: "Mana", thresholdPercent: 30, key: "2", item: "FlaskMana3", charges: "30/8", isCharm: false },
        { slot: 3, enabled: false, vital: "Life", thresholdPercent: 50, key: "3", item: "CharmFreeze", charges: "12/12", isCharm: true },
        { slot: 4, enabled: false, vital: "Life", thresholdPercent: 50, key: "4", item: "", charges: "", isCharm: false },
        { slot: 5, enabled: false, vital: "Life", thresholdPercent: 50, key: "unbound", item: "", charges: "", isCharm: false },
      ],
    },
  });
}
