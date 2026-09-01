// The page's behaviour: ask the host for state, render whole states when they arrive.
// Rendering is replace-not-patch on purpose - the host sends complete states, so the UI
// can never drift out of sync with a missed delta.

import { bridge } from "./bridge.js";
import { MapPanel } from "./map.js";
import { RulesPanel } from "./rules.js";
import { UpdatePanel } from "./updates.js";

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

  if (s.evasion) renderEvasion(s.evasion);
  if (s.ground) renderGround(s.ground);
  if (s.rules) rules.set(s.rules);

  // Every state, not only while the tab is open: the dot on the tab is what tells somebody
  // sitting on Setup that a newer build exists, and it cannot appear on a tab that has to be
  // visited before it renders.
  updates.set(s.update);

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

// ── Update tab ─────────────────────────────────────────────────────────────

const updates = new UpdatePanel();

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
  $("tab-update").hidden = name !== "update";
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

// ── Evasion ────────────────────────────────────────────────────────────────
//
// The last settings block the host sent. Edits are applied to a COPY and posted back, exactly
// as the flasks are: the host normalises what it receives and the next state carries whatever
// it decided, so the page never becomes a second source of truth.
let evasion = null;

// While a field is being typed in, a poll must not put the old value back mid-keystroke. Same
// guard the overlay card uses, and for the same reason it needed one.
let evasionEditingUntil = 0;
const holdEvasion = () => (evasionEditingUntil = Date.now() + 2000);

function renderEvasion(ev) {
  evasion = ev.settings;

  // Live text always - it is the answer to "why is nothing happening" and is worth being
  // current even while somebody is editing the thing that will change it.
  $("ev-status").textContent = ev.status;
  $("ev-keyname").textContent = ev.keyName;
  $("ev-keyhints").textContent = ev.keyHints.length
    ? `Lines in the game's config that mention a dodge: ${ev.keyHints.join("  ·  ")}`
    : "Nothing in the game's config mentions a dodge — set the code yourself.";
  $("ev-movenames").textContent = ev.moveKeyNames ?? "–";
  $("ev-movehints").textContent = (ev.moveHints ?? []).length
    ? `Lines in the game's config that mention movement: ${ev.moveHints.join("  ·  ")}`
    : "W A S D is what the game ships with — check these if you have rebound them.";

  if (Date.now() < evasionEditingUntil) return;

  const warn = ev.settings.warn ?? {};
  const act = ev.settings.act ?? {};
  $("ev-warn").checked = !!warn.enabled;
  $("ev-act").checked = !!act.enabled;
  $("ev-warn-rarity").value = warn.fromRarity ?? "Normal";
  $("ev-act-rarity").value = act.fromRarity ?? "Rare";
  $("ev-key").value = ev.settings.dodgeKey ?? 0;
  $("ev-radius").value = ev.settings.dangerRadius ?? 90;
  $("ev-cooldown").value = ev.settings.cooldownMs ?? 1200;
  $("ev-quiet").checked = !!ev.settings.onlyDangerousAnimations;
  $("ev-only").value = (warn.onlyPaths ?? []).join(", ");
  $("ev-ignore").value = (warn.ignorePaths ?? []).join(", ");

  const keys = ev.settings.keys ?? {};
  $("ev-steer").checked = !!ev.settings.steer;
  $("ev-move-up").value = keys.up ?? 0x57;
  $("ev-move-left").value = keys.left ?? 0x41;
  $("ev-move-down").value = keys.down ?? 0x53;
  $("ev-move-right").value = keys.right ?? 0x44;
  $("ev-roll").value = ev.settings.rollDistance ?? 400;
  $("ev-hold").value = ev.settings.steerHoldMs ?? 60;
}

// A comma-separated field to the list the host wants. Empty means "no filter", which is not
// the same as a list holding one empty string - that would match every monster.
const paths = (id) =>
  $(id).value.split(",").map((p) => p.trim()).filter((p) => p.length > 0);

function sendEvasion() {
  if (!evasion) return;
  holdEvasion();

  // The type filters are edited once and applied to BOTH gates. Two sets of boxes would be
  // the more flexible page and a worse one: the question "which monsters is this about" has
  // one answer, and the two gates already differ where it matters - by rarity.
  const only = paths("ev-only");
  const ignore = paths("ev-ignore");

  bridge.send({
    type: "setEvasionSettings",
    payload: {
      ...evasion,
      warn: {
        enabled: $("ev-warn").checked,
        fromRarity: $("ev-warn-rarity").value,
        onlyPaths: only,
        ignorePaths: ignore,
      },
      act: {
        enabled: $("ev-act").checked,
        fromRarity: $("ev-act-rarity").value,
        onlyPaths: only,
        ignorePaths: ignore,
      },
      dodgeKey: Number($("ev-key").value) || 0,
      dangerRadius: Number($("ev-radius").value) || 90,
      cooldownMs: Number($("ev-cooldown").value) || 0,
      onlyDangerousAnimations: $("ev-quiet").checked,
      steer: $("ev-steer").checked,
      rollDistance: Number($("ev-roll").value) || 400,

      // NOT `|| 60`: zero is a legal hold and means "send the keys and release them at once",
      // which is worth being able to try on a machine with a high frame rate. The fallback is
      // for a field that is empty or unparseable, and Number("") is 0 - so it has to be tested
      // for emptiness rather than for falsiness.
      steerHoldMs: $("ev-hold").value === "" ? 60 : Number($("ev-hold").value),
      keys: {
        up: Number($("ev-move-up").value) || 0,
        left: Number($("ev-move-left").value) || 0,
        down: Number($("ev-move-down").value) || 0,
        right: Number($("ev-move-right").value) || 0,
      },
    },
  });
}

for (const id of [
  "ev-warn", "ev-act", "ev-warn-rarity", "ev-act-rarity",
  "ev-key", "ev-radius", "ev-cooldown", "ev-quiet", "ev-only", "ev-ignore",
  "ev-steer", "ev-roll", "ev-hold",
  "ev-move-up", "ev-move-left", "ev-move-down", "ev-move-right",
]) {
  // On "change", not "input": every one of these posts to the host, and a number field fires
  // per keystroke - which would send a radius of 9 on the way to typing 90.
  $(id).addEventListener("change", sendEvasion);
  $(id).addEventListener("input", holdEvasion);
}

// ── Dangerous ground ───────────────────────────────────────────────────────
//
// The rules the host last accepted, and what the session has seen. Edits go out as the WHOLE
// rule list, the same shape the flasks and the evasion card post in: the host merges it into
// the tracker's settings, normalises, and the next state carries what it decided.
let groundRules = [];
let groundSeen = [];

// A table rebuilt under someone's cursor drops a half-typed path and yanks focus, so a poll
// landing mid-edit leaves it alone - the same guard the other two cards needed.
let groundEditingUntil = 0;
const holdGround = () => (groundEditingUntil = Date.now() + 2000);

function renderGround(g) {
  groundRules = g.rules ?? [];
  groundSeen = g.seen ?? [];

  // The reading line is live ALWAYS, even mid-edit: it is the answer to "why is the dropdown
  // empty", and an empty dropdown is the normal state in a hideout and the alarming one in
  // a map. Holding it back while somebody types would hide it exactly when it is read.
  $("gd-read").textContent = g.reading || "";

  if (Date.now() < groundEditingUntil) return;

  renderGroundPicker();
  renderGroundRules();
}

/**
 * The dropdown of what the session has walked past.
 *
 * Each row says the two things that decide whether a rule is worth writing: whether the game
 * already tags it (a component ring covers those without any rule at all) and how many turn up
 * at once (a rule on something that arrives in twenties fills the screen).
 */
function renderGroundPicker() {
  const pick = $("gd-pick");
  const chosen = pick.value;
  pick.replaceChildren();

  if (!groundSeen.length) {
    const none = document.createElement("option");
    none.value = "";
    none.textContent = "nothing seen yet — walk past some";
    pick.appendChild(none);
    pick.disabled = true;
    $("gd-add").disabled = true;
    return;
  }

  pick.disabled = false;
  $("gd-add").disabled = false;

  for (const seen of groundSeen) {
    const option = document.createElement("option");
    option.value = seen.path;
    option.textContent = `${seen.path}  —  ${describeSeen(seen)}`;
    pick.appendChild(option);
  }

  // Keep the selection across the once-a-second poll, or the list would scroll back to the
  // top under the cursor between opening it and pressing Add.
  if (chosen && groundSeen.some((s) => s.path === chosen)) pick.value = chosen;
}

function describeSeen(seen) {
  const notes = [];
  if (seen.onScreen) notes.push("here now");

  // The game marks it, so the overlay already rings it AND can name the kind out of the game's
  // own table. A rule here is not wasted - it wins, and it is how a colour or size gets chosen -
  // but it is not needed just to see the ground.
  if (seen.hasComponent) notes.push("the game marks this one — already ringed and named");
  else notes.push("the game does not mark it — a rule is the only way it gets ringed");

  if (seen.most > 1) notes.push(`up to ${seen.most} at once`);
  if (groundRules.some((r) => matchesRule(r, seen.path))) notes.push("a rule covers it");
  return notes.join(", ");
}

const matchesRule = (rule, path) =>
  (rule.path ?? "").trim().length > 0
  && path.toLowerCase().startsWith(rule.path.trim().toLowerCase());

function renderGroundRules() {
  const rows = $("gd-rows");
  rows.replaceChildren();

  for (const [index, rule] of groundRules.entries()) {
    const tr = document.createElement("tr");

    tr.appendChild(cell(checkbox(rule.enabled !== false, false,
      (on) => editGround(index, { enabled: on }))));
    tr.appendChild(cell(groundPathField(rule, index)));
    tr.appendChild(cell(groundColour(rule, index)));
    tr.appendChild(cell(groundNumber(rule.radius ?? 100, 4, 2000, 5,
      (v) => editGround(index, { radius: v }))));
    tr.appendChild(cell(groundNumber(rule.thickness ?? 2, 1, 20, 1,
      (v) => editGround(index, { thickness: v }))));
    tr.appendChild(cell(checkbox(!!rule.filled, false,
      (on) => editGround(index, { filled: on }))));

    const remove = document.createElement("button");
    remove.type = "button";
    remove.textContent = "−";
    remove.title = "Delete this rule";
    remove.addEventListener("click", () =>
      sendGround(groundRules.filter((_, i) => i !== index)));
    tr.appendChild(cell(remove));

    rows.appendChild(tr);
  }

  // The same paths as a datalist, so the field completes as it is typed - the dropdown is for
  // picking one whole, this is for trimming one down to a prefix that covers its variants.
  let options = $("gd-paths");
  if (!options) {
    options = document.createElement("datalist");
    options.id = "gd-paths";
    $("ground").appendChild(options);
  }

  options.replaceChildren();
  for (const seen of groundSeen) {
    const option = document.createElement("option");
    option.value = seen.path;
    options.appendChild(option);
  }
}

function groundPathField(rule, index) {
  const input = document.createElement("input");
  input.type = "text";
  input.className = "gd-path";
  input.value = rule.path ?? "";
  input.placeholder = "Metadata/Effects/Spells/ground_effects/";
  input.setAttribute("list", "gd-paths");
  input.title = "Matched against the START of an entity's metadata path, ignoring case.";
  input.addEventListener("input", holdGround);
  input.addEventListener("change", () => editGround(index, { path: input.value }));
  return input;
}

function groundNumber(value, min, max, step, onChange) {
  const input = document.createElement("input");
  input.type = "number";
  input.min = min;
  input.max = max;
  input.step = step;
  input.value = value;
  input.addEventListener("input", holdGround);
  // On "change", not "input": a number field fires per keystroke, which would save a radius
  // of 1 on the way to typing 120.
  input.addEventListener("change", () => onChange(Number(input.value)));
  return input;
}

/**
 * A colour picker over the tool's #AARRGGBB.
 *
 * The native input only understands #RRGGBB, so the alpha rides around it rather than being
 * dropped - a ring somebody made faint must not turn solid because they nudged the hue. Same
 * dance the rules tab does, and it is duplicated rather than shared because the two pages have
 * no module between them and one import to move four lines would be the bigger coupling.
 */
function groundColour(rule, index) {
  const text = (rule.colour ?? "#99FF4D00").replace("#", "");
  const alpha = text.length === 8 ? text.slice(0, 2) : "99";
  const rgb = text.length === 8 ? text.slice(2) : text;

  const input = document.createElement("input");
  input.type = "color";
  input.value = `#${rgb}`;
  input.addEventListener("input", holdGround);
  input.addEventListener("change", () =>
    editGround(index, { colour: `#${alpha}${input.value.replace("#", "")}` }));
  return input;
}

function editGround(index, change) {
  sendGround(groundRules.map((r, i) => (i === index ? { ...r, ...change } : r)));
}

/**
 * Turns a path the session saw into the prefix a rule should hold.
 *
 * EVERYTHING AFTER AN '@' GOES. The game appends a variant marker to a lot of its metadata
 * paths - `.../BurningGroundDaemonParent@75` - and it differs per instance, so a rule seeded
 * with the marker still on it would match the one patch that was on screen when it was added
 * and none of the others. The rest of this tool drops it in the same place and for the same
 * reason; see PreloadReader.
 */
const asPrefix = (path) => {
  const at = path.indexOf("@");
  return at < 0 ? path : path.slice(0, at);
};

function sendGround(list) {
  holdGround();
  groundRules = list;
  renderGroundRules();
  renderGroundPicker();

  // WRAPPED IN AN OBJECT, and it has to be. The host drops any request whose payload is not
  // a JSON object, before its switch is ever reached - a guard every other message satisfies
  // because every other message sends a record. A bare array here was accepted by the page,
  // discarded by the host without a word, and the row vanished a second later when the poll
  // re-rendered from a state that had never changed.
  bridge.send({
    type: "setGroundRules",
    payload: {
      rules: list.map((r) => ({
        path: r.path ?? "",
        enabled: r.enabled !== false,
        colour: r.colour ?? "#99FF4D00",
        radius: Number(r.radius) || 100,
        thickness: Number(r.thickness) || 2,
        filled: !!r.filled,
      })),
    },
  });
}

$("gd-add").addEventListener("click", () => {
  const path = asPrefix($("gd-pick").value ?? "");
  if (!path) return;

  // Adding the same prefix twice would draw two rings on one patch and give somebody two rows
  // to keep in step, so a second press on the same choice does nothing rather than stacking.
  if (groundRules.some((r) => (r.path ?? "").trim().toLowerCase() === path.toLowerCase())) return;

  sendGround([...groundRules, { path, enabled: true, colour: "#99FF4D00", radius: 100, thickness: 2, filled: false }]);
});

$("gd-new").addEventListener("click", () =>
  sendGround([...groundRules, { path: "", enabled: true, colour: "#99FF4D00", radius: 100, thickness: 2, filled: false }]));

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
    // Hand-written to match the wire, with the same caveat the buff list carries: driving the
    // page in a browser cannot catch a host that spells these differently. The check for that
    // is on the C# side, over the serializer these names only imitate.
    ground: {
      reading: "3 kinds remembered, 2 in the area on the last look",
      rules: [
        { path: "Metadata/Effects/Spells/ground_effects/", enabled: true, colour: "#99FF4D00", radius: 100, thickness: 2, filled: false },
      ],
      seen: [
        { path: "Metadata/Effects/Spells/ground_effects/fire/", hasComponent: true, onScreen: true, most: 4, radius: 18.67, lastSeenMs: 0 },
        { path: "Metadata/Monsters/MonsterMods/GroundOnDeath/BurningGroundDaemonParent@75", hasComponent: false, onScreen: true, most: 12, radius: 0, lastSeenMs: 0 },
        { path: "Metadata/Effects/Spells/ground_effects/chilled/", hasComponent: false, onScreen: false, most: 1, radius: 0, lastSeenMs: 0 },
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
        cooldownJitterMs: 50,
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
