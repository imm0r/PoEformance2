// The rules tab: profiles, groups, rules, and the two ways of writing a condition.
//
// The same replace-not-patch rule the rest of the page follows. Every edit builds a whole new
// settings object and posts it; the host normalises it, keeps it, and answers with a state.
// So the page never shows a value the engine rejected - which matters more here than
// anywhere else on this page, because a rule the page believes in and the engine does not is
// a rule somebody watches and waits for.
//
// Nothing here knows what conditions exist. The catalogue arrives from the host, built from
// the engine's own tables, so this cannot offer a condition the engine would not understand.

import { bridge } from "./bridge.js";
import { GraphEditor } from "./graph.js";

const $ = (id) => document.getElementById(id);

/** Effect kinds, grouped as the editor lists them - and the group decides what fields show. */
const EFFECT_GROUPS = [
  ["Show", ["Text", "Bar"]],
  ["Sound", ["Sound"]],
  ["Keyboard", ["KeyPress", "KeyDown", "KeyUp", "KeySequence"]],
  ["Mouse", [
    "MouseLeftClick", "MouseRightClick",
    "MouseLeftDown", "MouseLeftUp",
    "MouseRightDown", "MouseRightUp",
    "ScrollUp", "ScrollDown",
  ]],
];

const DRAWS = new Set(["Text", "Bar"]);
const NEEDS_KEY = new Set(["KeyPress", "KeyDown", "KeyUp"]);

export class RulesPanel {
  constructor() {
    this.settings = null;
    this.shapes = {};
    this.catalogue = null;
    this.selected = null; // { group, rule }
    this.mode = new Map(); // rule id -> "graph" | "text"
    this.graph = null;
    this.editingUntil = 0;
    this.conditionError = null;

    // Which rule a pending parse belongs to. The reply carries no id of its own, and a second
    // edit landing before the first answer would otherwise write the wrong rule's condition.
    this.parsing = null;

    this.wire();
  }

  wire() {
    $("rl-enabled").addEventListener("change", () =>
      this.save({ ...this.settings, enabled: $("rl-enabled").checked }));

    $("rl-background").addEventListener("change", () =>
      this.save({ ...this.settings, noticeInBackground: $("rl-background").checked }));

    $("rl-gap").addEventListener("change", () =>
      this.save({ ...this.settings, minInputGapMs: Number($("rl-gap").value) }));

    $("rl-profile").addEventListener("change", () =>
      this.save({ ...this.settings, profile: $("rl-profile").value }));

    $("rl-profile-new").addEventListener("click", () => this.addProfile());
    $("rl-profile-delete").addEventListener("click", () => this.removeProfile());
    $("rl-group-new").addEventListener("click", () => this.addGroup());

    $("rl-preview").addEventListener("change", () => this.preview());

    bridge.on("ruleCatalogue", (message) => {
      this.catalogue = message;
      this.render();
    });

    bridge.on("condition", (message) => this.parsed(message));
  }

  /** Takes the host's state. */
  set(view) {
    this.settings = view.settings;
    this.shapes = view.shapes ?? {};
    $("rl-status").textContent = view.status;

    // The live text always, the CONTROLS only while nothing is being edited - the same rule
    // the overlay panel follows. A poll landing mid-edit would otherwise put back the value
    // that was there before the keystroke.
    if (Date.now() < this.editingUntil) return;
    if (this.root().contains(document.activeElement)) return;

    this.render();
  }

  root() {
    return $("tab-rules");
  }

  claim() {
    this.editingUntil = Date.now() + 1500;
  }

  /**
   * Keeps the settings, posts them, and redraws.
   *
   * `redraw` is off for edits the canvas has already drawn itself. Rebuilding the detail pane
   * there would replace the GraphEditor with a fresh one built from `this.shapes` - which is
   * the HOST's copy, and the host answers about a second later. So every box added would
   * vanish on the frame it was added, and the edit after it would be applied to the graph as
   * it was before both.
   */
  save(settings, redraw = true) {
    this.claim();
    this.settings = settings;
    bridge.send({ type: "setRuleSettings", payload: settings });
    if (redraw) this.render();
  }

  /**
   * Tells the host which rule's ranges to draw over the game.
   *
   * The switch and the SELECTION together: a preview with no rule chosen has nothing to draw,
   * and one left pointing at a rule nobody has open would go on painting circles on the ground
   * after the editor moved elsewhere. Sent on both, so the two cannot come apart.
   */
  preview() {
    const rule = this.chosen();
    const on = $("rl-preview").checked && rule;
    bridge.send({ type: "setRulePreview", payload: { ruleId: on ? rule.id : "" } });
  }

  /**
   * Whether this rule measures anything a ring could show.
   *
   * Read off the GRAPH rather than the condition: the page never sees the condition tree in a
   * form it can walk, and the graph is the same facts with the same arguments. A rule with no
   * radius in it draws nothing, and saying so beside the switch is cheaper than switching it
   * on and going to look.
   */
  hasRanges(rule) {
    const nodes = this.shapes[rule.id]?.graph?.nodes ?? [];
    const ranged = new Set(
      (this.catalogue?.facts ?? [])
        .filter((f) => f.argument === "Distance" || f.argument === "Pixels")
        .map((f) => f.name));

    return nodes.some((node) => node.kind === "Fact" && ranged.has(node.fact));
  }

  /** The rule the editor has open, or null. */
  chosen() {
    const group = this.profile()?.groups[this.selected?.group ?? -1];
    return group?.rules[this.selected?.rule ?? -1] ?? null;
  }

  profile() {
    if (!this.settings) return null;
    return (
      this.settings.profiles.find((p) => p.name === this.settings.profile) ??
      this.settings.profiles[0] ??
      null
    );
  }

  /** Rewrites the current profile's groups and saves. */
  withGroups(groups, redraw = true) {
    const current = this.profile();
    if (!current) return;
    this.save({
      ...this.settings,
      profiles: this.settings.profiles.map((p) =>
        p.name === current.name ? { ...p, groups } : p),
    }, redraw);
  }

  /** Rewrites one rule in place and saves. */
  withRule(groupIndex, ruleIndex, change, redraw = true) {
    const groups = this.profile().groups.map((group, gi) =>
      gi !== groupIndex
        ? group
        : { ...group, rules: group.rules.map((rule, ri) => (ri !== ruleIndex ? rule : { ...rule, ...change })) });
    this.withGroups(groups, redraw);
  }

  addProfile() {
    const name = uniqueName("Profile", this.settings.profiles.map((p) => p.name));
    this.save({
      ...this.settings,
      profile: name,
      profiles: [...this.settings.profiles, { name, groups: [] }],
    });
  }

  removeProfile() {
    if (this.settings.profiles.length < 2) return;
    const left = this.settings.profiles.filter((p) => p.name !== this.settings.profile);
    this.save({ ...this.settings, profile: left[0].name, profiles: left });
  }

  addGroup() {
    const current = this.profile();
    if (!current) return;
    this.withGroups([
      ...current.groups,
      {
        name: uniqueName("Group", current.groups.map((g) => g.name)),
        enabled: true,
        inTown: false,
        inHideout: false,
        inMaps: true,
        rules: [],
      },
    ]);
  }

  addRule(groupIndex) {
    const groups = this.profile().groups.map((group, index) =>
      index !== groupIndex
        ? group
        : {
            ...group,
            rules: [
              ...group.rules,
              {
                // Minted HERE rather than left for the host, even though the host would. The
                // id is what this page keys a rule's editing mode and its drawn graph on, so
                // one that only exists after the round trip means two rules added in the same
                // second share the blank key - and with it each other's canvas.
                id: newId(),
                name: uniqueName("Rule", group.rules.map((r) => r.name)),
                enabled: false,
                priority: 0,
                allowLower: true,
                comment: "",
                condition: { kind: "All", children: [] },
                effects: [{ kind: "Text", text: "Triggered" }],
              },
            ],
          });

    this.selected = { group: groupIndex, rule: groups[groupIndex].rules.length - 1 };
    this.withGroups(groups);
  }

  // ── Rendering ────────────────────────────────────────────────────────────

  render() {
    if (!this.settings) return;

    $("rl-enabled").checked = this.settings.enabled;
    $("rl-background").checked = this.settings.noticeInBackground;
    $("rl-gap").value = this.settings.minInputGapMs;

    const profiles = $("rl-profile");
    profiles.replaceChildren();
    for (const profile of this.settings.profiles) {
      const option = document.createElement("option");
      option.value = profile.name;
      option.textContent = profile.name;
      option.selected = profile.name === this.settings.profile;
      profiles.appendChild(option);
    }
    $("rl-profile-delete").disabled = this.settings.profiles.length < 2;

    this.renderGroups();
    this.renderDetail();
  }

  renderGroups() {
    const host = $("rl-groups");
    host.replaceChildren();

    const current = this.profile();
    if (!current) return;

    current.groups.forEach((group, groupIndex) => {
      const card = document.createElement("div");
      card.className = "rl-group";

      const head = document.createElement("div");
      head.className = "rl-group-head";

      head.appendChild(check(group.enabled, (on) =>
        this.withGroups(current.groups.map((g, i) => (i === groupIndex ? { ...g, enabled: on } : g)))));

      const name = document.createElement("input");
      name.type = "text";
      name.value = group.name;
      name.className = "rl-group-name";
      name.addEventListener("focus", () => this.claim());
      name.addEventListener("change", () =>
        this.withGroups(current.groups.map((g, i) => (i === groupIndex ? { ...g, name: name.value } : g))));
      head.appendChild(name);

      head.appendChild(iconButton("×", "Delete this group", () =>
        this.withGroups(current.groups.filter((_, i) => i !== groupIndex))));

      card.appendChild(head);

      // Where the group applies. Three boxes rather than a dropdown, because "maps and
      // hideout but not town" is an ordinary thing to want.
      const where = document.createElement("div");
      where.className = "rl-where";
      for (const [key, label] of [["inMaps", "maps"], ["inHideout", "hideout"], ["inTown", "town"]]) {
        where.appendChild(labelled(label, check(group[key], (on) =>
          this.withGroups(current.groups.map((g, i) => (i === groupIndex ? { ...g, [key]: on } : g))))));
      }
      card.appendChild(where);

      const list = document.createElement("ul");
      list.className = "rl-rules";
      group.rules.forEach((rule, ruleIndex) => {
        const item = document.createElement("li");
        const chosen =
          this.selected?.group === groupIndex && this.selected?.rule === ruleIndex;
        item.className = chosen ? "chosen" : "";

        item.appendChild(check(rule.enabled, (on) =>
          this.withRule(groupIndex, ruleIndex, { enabled: on })));

        const open = document.createElement("button");
        open.type = "button";
        open.className = "rl-rule-name";
        open.textContent = rule.name;
        if (!rule.enabled) open.classList.add("dim");
        open.addEventListener("click", () => {
          this.selected = { group: groupIndex, rule: ruleIndex };
          this.render();

          // The preview follows the selection, or it goes on drawing the ranges of a rule
          // nobody is looking at any more.
          this.preview();
        });
        item.appendChild(open);

        list.appendChild(item);
      });
      card.appendChild(list);

      const add = document.createElement("button");
      add.type = "button";
      add.className = "rl-add-rule";
      add.textContent = "Add rule";
      add.addEventListener("click", () => this.addRule(groupIndex));
      card.appendChild(add);

      host.appendChild(card);
    });
  }

  renderDetail() {
    const host = $("rl-detail");
    host.replaceChildren();

    const current = this.profile();
    const group = current?.groups[this.selected?.group ?? -1];
    const rule = group?.rules[this.selected?.rule ?? -1];

    if (!rule) {
      host.appendChild(hint("Pick a rule on the left, or add one."));
      return;
    }

    if (!this.catalogue) {
      host.appendChild(hint("Waiting for the host to say which conditions exist…"));
      return;
    }

    const { group: gi, rule: ri } = this.selected;

    $("rl-preview-label").classList.toggle("dim", !this.hasRanges(rule));

    host.appendChild(this.ruleHeader(rule, gi, ri, group));
    host.appendChild(this.conditionSection(rule, gi, ri));
    host.appendChild(this.effectsSection(rule, gi, ri));
  }

  ruleHeader(rule, gi, ri, group) {
    const box = document.createElement("section");
    box.className = "card";

    const row = document.createElement("div");
    row.className = "rl-detail-head";

    const name = document.createElement("input");
    name.type = "text";
    name.className = "rl-name";
    name.value = rule.name;
    name.addEventListener("focus", () => this.claim());
    name.addEventListener("change", () => this.withRule(gi, ri, { name: name.value }));
    row.appendChild(name);

    row.appendChild(iconButton("×", "Delete this rule", () => {
      this.selected = null;
      this.withGroups(this.profile().groups.map((g, i) =>
        i !== gi ? g : { ...g, rules: g.rules.filter((_, r) => r !== ri) }));
    }));
    box.appendChild(row);

    const options = document.createElement("div");
    options.className = "cfg-row";

    const priority = document.createElement("input");
    priority.type = "number";
    priority.value = rule.priority;
    priority.step = 10;
    priority.className = "rl-priority";
    priority.addEventListener("focus", () => this.claim());
    priority.addEventListener("change", () => this.withRule(gi, ri, { priority: Number(priority.value) }));
    options.appendChild(labelled("Priority", priority));

    options.appendChild(labelled(
      "Let lower rules act too",
      check(rule.allowLower, (on) => this.withRule(gi, ri, { allowLower: on })),
      "Clear this and, while this rule is firing, nothing of lower priority does."));

    box.appendChild(options);

    const comment = document.createElement("input");
    comment.type = "text";
    comment.className = "rl-comment";
    comment.placeholder = "What is this rule for?";
    comment.value = rule.comment ?? "";
    comment.addEventListener("focus", () => this.claim());
    comment.addEventListener("change", () => this.withRule(gi, ri, { comment: comment.value }));
    box.appendChild(comment);

    if (!group.enabled) box.appendChild(hint("This rule's group is switched off."));
    return box;
  }

  conditionSection(rule, gi, ri) {
    const box = document.createElement("section");
    box.className = "card";

    const head = document.createElement("div");
    head.className = "rl-section-head";
    head.appendChild(heading("When"));

    const mode = this.mode.get(rule.id) ?? "graph";
    const toggle = document.createElement("div");
    toggle.className = "rl-modes";
    for (const [key, label] of [["graph", "Boxes"], ["text", "Text"]]) {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = label;
      button.className = mode === key ? "active" : "";
      button.addEventListener("click", () => {
        this.mode.set(rule.id, key);
        this.conditionError = null;
        this.render();
      });
      toggle.appendChild(button);
    }
    head.appendChild(toggle);
    box.appendChild(head);

    const shape = this.shapes[rule.id] ?? { text: "", graph: null };

    if (mode === "text") {
      box.appendChild(this.conditionText(shape, gi, ri));
    } else {
      box.appendChild(this.conditionGraph(rule, shape, gi, ri));
    }

    return box;
  }

  conditionText(shape, gi, ri) {
    const wrap = document.createElement("div");

    const field = document.createElement("textarea");
    field.className = "rl-expression";
    field.rows = 3;
    field.spellcheck = false;
    field.value = shape.text;
    field.addEventListener("focus", () => this.claim());

    // On "change": every keystroke would otherwise be a round trip AND a settings write, and
    // a half-typed condition is not one anybody meant to save.
    field.addEventListener("change", () => {
      this.claim();
      this.parsing = { group: gi, rule: ri };
      bridge.send({ type: "parseCondition", payload: { text: field.value } });
    });
    wrap.appendChild(field);

    if (this.conditionError) {
      const error = document.createElement("p");
      error.className = "bad";
      error.textContent = `Column ${this.conditionError.column}: ${this.conditionError.error}`;
      wrap.appendChild(error);
    }

    wrap.appendChild(hint(
      "Conditions join with && and ||, ! inverts, and brackets group. "
      + "Numbers take a comparison: LifePercent <= 35."));
    return wrap;
  }

  conditionGraph(rule, shape, gi, ri) {
    const host = document.createElement("div");

    // Rebuilt per render rather than kept: the detail pane is replaced whole when the
    // selection changes, so a retained editor would be attached to a node that is gone.
    this.graph = new GraphEditor(
      host,
      this.catalogue,
      (graph) => {
        // The local shape moves with the edit. Everything that redraws reads shapes, and until
        // the host answers this is the only copy that has the new box in it.
        if (this.shapes[rule.id]) this.shapes[rule.id] = { ...this.shapes[rule.id], graph };
        this.withRule(gi, ri, { graph }, false);
      },

      // Interaction started. Holds off the once-a-second poll for as long as it goes on, so a
      // slow drag is not undone by the host answering with the positions from before it.
      () => this.claim());
    this.graph.set(shape.graph);
    return host;
  }

  /** The host's answer to a typed condition. */
  parsed(message) {
    const at = this.parsing;
    this.parsing = null;
    if (!at) return;

    if (!message.ok) {
      this.conditionError = { error: message.error, column: message.column };
      this.render();
      return;
    }

    this.conditionError = null;

    // The graph goes with it. The tree changed, so a layout arranged for the old one describes
    // a rule that no longer exists - keeping it would show boxes that are not this condition.
    this.withRule(at.group, at.rule, { condition: message.condition, graph: null });
  }

  effectsSection(rule, gi, ri) {
    const box = document.createElement("section");
    box.className = "card";
    box.appendChild(heading("Then"));

    rule.effects.forEach((effect, index) => {
      box.appendChild(this.effectRow(rule, effect, index, gi, ri));
    });

    const add = document.createElement("button");
    add.type = "button";
    add.textContent = "Add effect";
    add.addEventListener("click", () =>
      this.withRule(gi, ri, { effects: [...rule.effects, { kind: "Text", text: "Triggered" }] }));
    box.appendChild(add);

    if (rule.effects.some((e) => !DRAWS.has(e.kind) && e.kind !== "Sound")) {
      box.appendChild(hint(
        "Input only goes out while the game is in front and no panel is open. "
        + "That is not a setting - keystrokes land wherever focus is."));
    }

    return box;
  }

  effectRow(rule, effect, index, gi, ri) {
    const change = (patch) =>
      this.withRule(gi, ri, {
        effects: rule.effects.map((e, i) => (i === index ? { ...e, ...patch } : e)),
      });

    const row = document.createElement("div");
    row.className = "rl-effect";

    const kind = document.createElement("select");
    for (const [label, kinds] of EFFECT_GROUPS) {
      const optionGroup = document.createElement("optgroup");
      optionGroup.label = label;
      for (const name of kinds) {
        const option = document.createElement("option");
        option.value = name;
        option.textContent = spaced(name);
        option.selected = name === effect.kind;
        optionGroup.appendChild(option);
      }
      kind.appendChild(optionGroup);
    }
    kind.addEventListener("change", () => change({ kind: kind.value }));
    row.appendChild(kind);

    row.appendChild(this.effectFields(effect, change));

    row.appendChild(iconButton("×", "Remove this effect", () =>
      this.withRule(gi, ri, { effects: rule.effects.filter((_, i) => i !== index) })));

    return row;
  }

  effectFields(effect, change) {
    const fields = document.createElement("div");
    fields.className = "rl-effect-fields";

    if (DRAWS.has(effect.kind)) {
      fields.appendChild(labelled(effect.kind === "Bar" ? "Label" : "Text",
        this.textField(effect.text ?? "", (value) => change({ text: value }), "{LifePercent}% left")));

      if (effect.kind === "Bar") {
        const watching = document.createElement("select");
        for (const fact of this.catalogue.facts.filter((f) => f.shape === "Number")) {
          const option = document.createElement("option");
          option.value = fact.name;
          option.textContent = fact.name;
          option.selected = fact.name === (effect.watching ?? "LifePercent");
          watching.appendChild(option);
        }
        watching.addEventListener("change", () => change({ watching: watching.value }));
        fields.appendChild(labelled("Filled by", watching));
      }

      fields.appendChild(labelled("At", this.pair(effect, change)));
      fields.appendChild(labelled("Colour", this.colour(effect.colour, (v) => change({ colour: v }))));
      fields.appendChild(labelled("Stays", this.number(effect.lingerMs ?? 400, 0, 60000, 50,
        (v) => change({ lingerMs: v })), "How long it remains after the condition stops"));
      return fields;
    }

    if (effect.kind === "Sound") {
      fields.appendChild(labelled("Pitch", this.number(effect.pitch ?? 900, 37, 32767, 10,
        (v) => change({ pitch: v }))));
      fields.appendChild(labelled("For", this.number(effect.soundMs ?? 120, 1, 5000, 10,
        (v) => change({ soundMs: v }))));
      fields.appendChild(this.cooldown(effect, change));
      return fields;
    }

    if (effect.kind === "KeySequence") {
      fields.appendChild(labelled("Keys",
        this.textField(effect.keys ?? "", (v) => change({ keys: v }), "Q, W, F1")));
      fields.appendChild(this.cooldown(effect, change));
      return fields;
    }

    if (NEEDS_KEY.has(effect.kind)) {
      const source = document.createElement("select");
      for (const [value, label] of [["Named", "this key"], ["FlaskSlot", "the game's flask key"]]) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        option.selected = value === (effect.keySource ?? "Named");
        source.appendChild(option);
      }
      source.addEventListener("change", () => change({ keySource: source.value }));
      fields.appendChild(labelled("Press", source));

      if ((effect.keySource ?? "Named") === "FlaskSlot") {
        fields.appendChild(labelled("Slot", this.number(effect.slot || 1, 1, 5, 1,
          (v) => change({ slot: v })),
          "Read live from the game's own bindings, so a rebind follows"));
      } else {
        const key = document.createElement("select");
        for (const name of this.catalogue.keys) {
          const option = document.createElement("option");
          option.value = name;
          option.textContent = name;
          option.selected = name === effect.key;
          key.appendChild(option);
        }
        if (!effect.key) {
          const none = document.createElement("option");
          none.value = "";
          none.textContent = "— pick a key —";
          none.selected = true;
          key.prepend(none);
        }
        key.addEventListener("change", () => change({ key: key.value }));
        fields.appendChild(key);
      }

      fields.appendChild(this.cooldown(effect, change));
      return fields;
    }

    // Mouse and wheel: nothing to aim, so a cooldown is the only thing worth setting.
    fields.appendChild(this.cooldown(effect, change));
    return fields;
  }

  cooldown(effect, change) {
    return labelled("No sooner than every",
      this.number(effect.cooldownMs ?? 2000, 0, 600000, 50, (v) => change({ cooldownMs: v })),
      "Milliseconds between two firings of this effect");
  }

  pair(effect, change) {
    const wrap = document.createElement("span");
    wrap.className = "rl-pair";
    wrap.appendChild(this.number(round(effect.x ?? 0.5), 0, 1, 0.01, (v) => change({ x: v })));
    wrap.appendChild(this.number(round(effect.y ?? 0.35), 0, 1, 0.01, (v) => change({ y: v })));
    wrap.title = "Across and down the screen, as a share of it";
    return wrap;
  }

  number(value, min, max, step, onChange) {
    const input = document.createElement("input");
    input.type = "number";
    input.value = value;
    input.min = min;
    input.max = max;
    input.step = step;
    input.addEventListener("focus", () => this.claim());
    input.addEventListener("change", () => onChange(Number(input.value)));
    return input;
  }

  textField(value, onChange, placeholder) {
    const input = document.createElement("input");
    input.type = "text";
    input.value = value;
    if (placeholder) input.placeholder = placeholder;
    input.addEventListener("focus", () => this.claim());
    input.addEventListener("change", () => onChange(input.value));
    return input;
  }

  /**
   * A colour picker over the tool's #AARRGGBB.
   *
   * The native input only understands #RRGGBB, so the alpha is carried around it rather than
   * dropped: a caption somebody made half transparent must not become opaque because they
   * touched the hue.
   */
  colour(value, onChange) {
    const text = (value ?? "#FF33FF40").replace("#", "");
    const alpha = text.length === 8 ? text.slice(0, 2) : "FF";
    const rgb = text.length === 8 ? text.slice(2) : text;

    const input = document.createElement("input");
    input.type = "color";
    input.value = `#${rgb}`;
    input.addEventListener("input", () => this.claim());
    input.addEventListener("change", () =>
      onChange(`#${alpha}${input.value.replace("#", "")}`));
    return input;
  }
}

// ── Small builders ─────────────────────────────────────────────────────────

function heading(text) {
  const h = document.createElement("h2");
  h.textContent = text;
  return h;
}

function hint(text) {
  const p = document.createElement("p");
  p.className = "dim";
  p.textContent = text;
  return p;
}

function check(checked, onChange) {
  const box = document.createElement("input");
  box.type = "checkbox";
  box.checked = !!checked;
  box.addEventListener("change", () => onChange(box.checked));
  return box;
}

function labelled(text, control, title) {
  const label = document.createElement("label");
  label.className = "rl-field";
  if (title) label.title = title;
  const span = document.createElement("span");
  span.textContent = text;
  label.appendChild(span);
  label.appendChild(control);
  return label;
}

function iconButton(text, title, onClick) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "rl-icon";
  button.textContent = text;
  button.title = title;
  button.addEventListener("click", onClick);
  return button;
}

function spaced(name) {
  return name.replace(/([a-z])([A-Z])/g, "$1 $2");
}

function round(value) {
  return Math.round(value * 100) / 100;
}

function newId() {
  return "r" + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
}

function uniqueName(base, taken) {
  if (!taken.includes(base)) return base;
  for (let n = 2; ; n++) {
    const candidate = `${base} ${n}`;
    if (!taken.includes(candidate)) return candidate;
  }
}
