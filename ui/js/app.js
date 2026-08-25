// The page's behaviour: ask the host for state, render whole states when they arrive.
// Rendering is replace-not-patch on purpose - the host sends complete states, so the UI
// can never drift out of sync with a missed delta.

import { bridge } from "./bridge.js";
import { MapPanel } from "./map.js";
import { RulesPanel } from "./rules.js";

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
  if (s.overlay) {
    // Live text always; the CONTROLS only while they are not being used. A colour picker
    // holds its value until the dialog is committed, so a poll landing in between would
    // put the old colour back - which is what "the swatch reverts after a second" was.
    $("ov-terrain-state").textContent = s.overlay.terrain;
    if (Date.now() >= overlayEditingUntil) {
      $("ov-loot").value = s.overlay.minLootRarity;
      $("ov-terrain").checked = s.overlay.showTerrain;
      $("ov-terrain-colour").value = s.overlay.terrainColour;
      $("ov-terrain-thickness").value = s.overlay.terrainThickness;
      $("ov-terrain-thickness-value").textContent = s.overlay.terrainThickness;
    }
  }

  if (s.rules) rules.set(s.rules);

  if (s.map) {
    $("map-status").textContent = s.map.status;
    map.setState(s.map);
    // Asking on an area CHANGE rather than on a timer: the layout is the expensive half
    // of this panel and it cannot change while the area does not.
    if (map.needsLayout(s.map.area)) bridge.send({ type: "getMapLayout" });
  }
}

// ── Rules tab ──────────────────────────────────────────────────────────────

const rules = new RulesPanel();

// ── Map tab ────────────────────────────────────────────────────────────────

const map = new MapPanel($("map-canvas"), document.querySelector(".map-frame"));

bridge.on("mapLayout", (m) => map.setLayout(m.area, m.layout));

$("map-follow").addEventListener("change", (e) => {
  map.follow = e.target.checked;
  map.draw();
});

$("map-zoom").addEventListener("input", (e) => {
  map.zoom = Number(e.target.value);
  map.draw();
});

window.addEventListener("resize", () => map.draw());

function showTab(name) {
  for (const button of document.querySelectorAll(".tab")) {
    button.classList.toggle("active", button.dataset.tab === name);
  }

  $("tab-setup").hidden = name !== "setup";
  $("tab-rules").hidden = name !== "rules";
  $("tab-map").hidden = name !== "map";
  map.show(name === "map");

  // The layout is only fetched while the tab is open, so opening it is when to ask.
  if (name === "map" && map.needsLayout(map.state?.area ?? 0)) {
    bridge.send({ type: "getMapLayout" });
  }
}

for (const button of document.querySelectorAll(".tab")) {
  button.addEventListener("click", () => showTab(button.dataset.tab));
}

// Set while a control here is in use, so an incoming state does not overwrite it. A
// timestamp rather than a focus check: a native colour dialog is a separate window, and
// which element counts as focused while it is open is not something to rely on.
let overlayEditingUntil = 0;

/** Posts the whole overlay block, so one control cannot clear another's value. */
function sendOverlay() {
  overlayEditingUntil = Date.now() + 1500;
  bridge.send({
    type: "setOverlaySettings",
    payload: {
      minLootRarity: $("ov-loot").value,
      showTerrain: $("ov-terrain").checked,
      terrainColour: $("ov-terrain-colour").value,
      terrainThickness: Number($("ov-terrain-thickness").value),
    },
  });
}

for (const id of ["ov-loot", "ov-terrain", "ov-terrain-colour", "ov-terrain-thickness"]) {
  // Touching a control claims it, even before anything is sent: dragging a colour picker
  // fires "input" for a while before the "change" that commits it.
  $(id).addEventListener("input", () => (overlayEditingUntil = Date.now() + 1500));
}

$("ov-loot").addEventListener("change", sendOverlay);
$("ov-terrain").addEventListener("change", sendOverlay);
$("ov-terrain-colour").addEventListener("change", sendOverlay);

// On "change", not "input": dragging a slider fires continuously, and each thickness step
// rebuilds the terrain texture on the render thread.
$("ov-terrain-thickness").addEventListener("input", () =>
  ($("ov-terrain-thickness-value").textContent = $("ov-terrain-thickness").value));
$("ov-terrain-thickness").addEventListener("change", sendOverlay);

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

  // Once, not with every state: what a rule may ask about cannot change while the tool runs,
  // and it is a few kilobytes that would otherwise ride the once-a-second poll.
  bridge.send({ type: "getRuleCatalogue" });

  // The status line and the charge counts are live values, so the panel keeps itself
  // current instead of waiting to be asked. Slow on purpose: each poll re-reads the world.
  setInterval(() => bridge.send({ type: "getState" }), 1000);
} else {
  // Opened in a plain browser for UI work: render a fake state so layout is editable
  // without the host running.
  //
  // The rule catalogue is faked too, and deliberately SHORT. It exists so the tab can be
  // laid out, not so it can be trusted - the real one is generated from the engine's tables,
  // and a long copy here would be a second list to keep in step.
  rules.catalogue = {
    facts: [
      { name: "InGame", shape: "Flag", argument: "None", unit: "", help: "In an area." },
      { name: "LifePercent", shape: "Number", argument: "None", unit: "%", help: "Life, unreserved." },
      { name: "HasBuff", shape: "Flag", argument: "Text", unit: "", help: "A named buff is on." },
      // A second TEXT fact, so the preview can show that only the buff ones get the buff list.
      { name: "AreaContains", shape: "Flag", argument: "Text", unit: "", help: "Part of an area name." },
      { name: "FlaskCharges", shape: "Number", argument: "Slot", unit: "", help: "Charges in a slot." },
      { name: "EverySeconds", shape: "Flag", argument: "Seconds", unit: "", help: "Once per interval." },
    ],
    keys: ["Q", "W", "1", "2", "F1", "Space"],
    effects: ["Text", "Bar", "Sound", "KeyPress"],
    comparisons: ["AtLeast", "AtMost", "Above", "Below", "Is", "IsNot"],
  };

  renderState({
    toolVersion: "browser preview",
    gameVersion: "(no host - open via PoEformance.App --config)",
    attached: false,
    processId: 0,
    inGame: false,
    entityCount: 0,
    staticsFound: 0,
    staticsTotal: 6,
    overlay: { minLootRarity: "Magic", showTerrain: true, terrainColour: "#96c8ff", terrainThickness: 1, terrain: "browser preview" },
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
    rules: {
      status: "browser preview",
      acted: 0,
      keySource: "Defaults - no host",
      buffRead: "Buffs at 0xPREVIEW, 24 bytes = 3 entries; 3 followed, 3 defined, 3 named",
      reader: "120 reads, 0 failed",

      // Hand-written to match the wire, which means driving the page in a browser can never
      // catch a host that spells these differently - and one did, sending Name/Active while
      // this said name/active, so the picker showed one buff called "undefined". The check
      // that catches that is on the C# side, over the serializer these names only imitate.
      buffs: [
        { name: "fire_wall", displayName: "Flame Wall", description: "Deals fire damage over time.", active: true, timeLeft: 18.4, charges: 2, flaskSlot: 0, lastSeenMs: 0 },
        { name: "flask_effect_life", displayName: "Life Flask", description: "", active: true, timeLeft: 3.1, charges: 0, flaskSlot: 1, lastSeenMs: 0 },
        { name: "chilled", displayName: "Chilled", description: "", active: false, timeLeft: 0, charges: 0, flaskSlot: 0, lastSeenMs: 0 },
      ],
      settings: {
        enabled: false,
        profile: "Default",
        noticeInBackground: false,
        minInputGapMs: 100,
        profiles: [{
          name: "Default",
          groups: [{
            name: "Example", enabled: true, inTown: false, inHideout: false, inMaps: true,
            rules: [{
              id: "preview", name: "Low life", enabled: false, priority: 0, allowLower: true,
              comment: "", condition: { kind: "Fact", fact: "LifePercent", compare: "AtMost", value: 35 },
              effects: [{ kind: "Text", text: "LOW LIFE", x: 0.5, y: 0.25, colour: "#FFFF2020" }],
            }],
          }],
        }],
      },
      shapes: {
        preview: {
          text: "LifePercent <= 35",
          graph: {
            nodes: [
              { id: "a", kind: "Fact", x: 20, y: 20, fact: "LifePercent", compare: "AtMost", value: 35, argument: 0, text: "", negate: false },
              { id: "out", kind: "Output", x: 300, y: 20 },
            ],
            links: [{ from: "a", to: "out" }],
          },
        },
      },
    },
  });
}
