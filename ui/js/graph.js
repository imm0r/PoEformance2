// The node editor: a rule's condition as boxes and wires.
//
// Boxes are absolutely positioned DOM, wires are one SVG behind them. Not a <canvas>: a box
// carries real <select> and <input> controls, and a canvas would mean reimplementing a
// dropdown - which is how the reference plugin's ImGui editor ends up with 900 lines of
// hit-testing that this file does not need.
//
// The model is exactly what the host stores: { nodes: [{id, kind, x, y, ...}], links:
// [{from, to}] }. Nothing is derived here and nothing is cached - every edit rewrites the
// model and calls back, and the host is what turns it into a condition.

const FACT_KIND = "Fact";
const OUTPUT_KIND = "Output";

/** The joins, in the order the toolbar offers them. */
const JOINS = [
  ["All", "AND", "every wire in must hold"],
  ["Any", "OR", "one wire in is enough"],
  ["ExactlyOne", "XOR", "exactly one wire in must hold"],
  ["Not", "NOT", "inverts the one wire in"],
];

const COMPARE_LABELS = {
  AtLeast: "≥",
  AtMost: "≤",
  Above: ">",
  Below: "<",
  Is: "=",
  IsNot: "≠",
};

/** Where a box's ports sit, as a share of its height. Kept here so wires and dots agree. */
const PORT_Y = 0.5;

export class GraphEditor {
  /**
   * @param {HTMLElement} host      where to build the editor
   * @param {object} catalogue      what the engine will accept - facts, comparisons
   * @param {(graph: object) => void} onChange  called after every edit
   */
  constructor(host, catalogue, onChange) {
    this.host = host;
    this.catalogue = catalogue;
    this.onChange = onChange;
    this.graph = { nodes: [], links: [] };
    this.selected = null;

    // The output port a click armed, waiting for an input port. Null when nothing is pending.
    this.linking = null;

    this.build();
  }

  build() {
    this.host.replaceChildren();
    this.host.className = "graph";

    const bar = document.createElement("div");
    bar.className = "graph-bar";

    bar.appendChild(this.button("Add condition", () => this.add(FACT_KIND)));
    for (const [kind, label, title] of JOINS) {
      bar.appendChild(this.button(label, () => this.add(kind), title));
    }

    bar.appendChild(this.spacer());
    bar.appendChild(this.button("Tidy up", () => this.tidy(),
      "Drop the boxes that reach no output, and lay the rest out"));

    this.hint = document.createElement("span");
    this.hint.className = "graph-hint dim";
    bar.appendChild(this.hint);

    this.host.appendChild(bar);

    this.surface = document.createElement("div");
    this.surface.className = "graph-surface";

    this.wires = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    this.wires.setAttribute("class", "graph-wires");
    this.surface.appendChild(this.wires);

    // Clicking empty space cancels a half-made wire. Without this an armed port stays armed
    // across every other interaction, and the next click anywhere makes a link nobody meant.
    this.surface.addEventListener("pointerdown", (event) => {
      if (event.target === this.surface || event.target === this.wires) {
        this.linking = null;
        this.selected = null;
        this.render();
      }
    });

    this.host.appendChild(this.surface);
  }

  button(label, onClick, title) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    if (title) button.title = title;
    button.addEventListener("click", onClick);
    return button;
  }

  spacer() {
    const span = document.createElement("span");
    span.className = "grow";
    return span;
  }

  /** Replaces the graph shown. Does NOT call back - this is the host's copy arriving. */
  set(graph) {
    this.graph = graph && graph.nodes?.length ? structuredClone(graph) : blank();
    this.linking = null;
    this.selected = null;
    this.render();
  }

  get() {
    return structuredClone(this.graph);
  }

  changed() {
    this.render();
    this.onChange(this.get());
  }

  add(kind) {
    // Dropped where there is room rather than at a fixed point, so adding three in a row does
    // not stack them on top of each other and look like one box.
    const id = "n" + Math.random().toString(36).slice(2, 10);
    const row = this.graph.nodes.filter((n) => n.kind === kind).length;
    this.graph.nodes.push({
      id,
      kind,
      x: kind === FACT_KIND ? 20 : 260,
      y: 20 + (row % 6) * 96,
      fact: this.catalogue.facts[0]?.name ?? "InGame",
      compare: "AtMost",
      value: 0,
      argument: 0,
      text: "",
      negate: false,
    });
    this.selected = id;
    this.changed();
  }

  remove(id) {
    this.graph.nodes = this.graph.nodes.filter((node) => node.id !== id);
    this.graph.links = this.graph.links.filter((link) => link.from !== id && link.to !== id);
    if (this.selected === id) this.selected = null;
    this.changed();
  }

  /**
   * Makes a wire, if it is one the model allows.
   *
   * Refused rather than corrected: a Not with two inputs and a self-link are both things the
   * host reads as "says nothing", so allowing them here would let somebody draw a graph that
   * looks wired and fires nothing.
   */
  link(from, to) {
    if (from === to) return this.say("A box cannot feed itself.");

    const target = this.node(to);
    if (!target || target.kind === FACT_KIND) return this.say("A condition takes no input.");
    if (this.node(from)?.kind === OUTPUT_KIND) return this.say("The output feeds nothing.");

    if (this.graph.links.some((link) => link.from === from && link.to === to)) {
      return this.say("That wire is already there.");
    }

    // One input each, for the two kinds where a second would change the meaning rather than
    // add to it. Everything else joins as many as it is given.
    const single = target.kind === "Not" || target.kind === OUTPUT_KIND;
    if (single && this.graph.links.some((link) => link.to === to)) {
      this.graph.links = this.graph.links.filter((link) => link.to !== to);
    }

    if (this.reaches(to, from)) return this.say("That would make a loop.");

    this.graph.links.push({ from, to });
    this.say("");
    this.changed();
  }

  /** Whether `from` already feeds `to`, directly or through others. */
  reaches(from, to, seen = new Set()) {
    if (from === to) return true;
    if (seen.has(from)) return false;
    seen.add(from);
    return this.graph.links
      .filter((link) => link.from === from)
      .some((link) => this.reaches(link.to, to, seen));
  }

  say(message) {
    this.hint.textContent = message;
  }

  node(id) {
    return this.graph.nodes.find((node) => node.id === id) ?? null;
  }

  /** Drops what reaches no output and lays the rest out in columns. */
  tidy() {
    const output = this.graph.nodes.find((node) => node.kind === OUTPUT_KIND);
    if (!output) return;

    const keep = new Set([output.id]);
    const walk = (id) => {
      for (const link of this.graph.links) {
        if (link.to === id && !keep.has(link.from)) {
          keep.add(link.from);
          walk(link.from);
        }
      }
    };
    walk(output.id);

    this.graph.nodes = this.graph.nodes.filter((node) => keep.has(node.id));
    this.graph.links = this.graph.links.filter((l) => keep.has(l.from) && keep.has(l.to));

    // Depth from the output, taking the LONGEST path so a box feeding two branches sits left
    // of both rather than on top of a wire that passes it.
    const depth = new Map();
    const measure = (id, at) => {
      if ((depth.get(id) ?? -1) >= at) return;
      depth.set(id, at);
      for (const link of this.graph.links) {
        if (link.to === id) measure(link.from, at + 1);
      }
    };
    measure(output.id, 0);

    const deepest = Math.max(0, ...depth.values());
    const used = new Map();
    for (const node of this.graph.nodes) {
      const column = deepest - (depth.get(node.id) ?? deepest);
      const row = used.get(column) ?? 0;
      used.set(column, row + 1);
      node.x = 20 + column * 230;
      node.y = 20 + row * 96;
    }

    this.changed();
  }

  render() {
    for (const box of [...this.surface.querySelectorAll(".graph-node")]) box.remove();

    let width = 480;
    let height = 240;

    for (const node of this.graph.nodes) {
      const box = this.box(node);
      this.surface.appendChild(box);
      width = Math.max(width, node.x + 260);
      height = Math.max(height, node.y + 120);
    }

    this.surface.style.width = `${width}px`;
    this.surface.style.height = `${height}px`;
    this.wires.setAttribute("viewBox", `0 0 ${width} ${height}`);
    this.wires.setAttribute("width", width);
    this.wires.setAttribute("height", height);

    this.drawWires();
  }

  drawWires() {
    this.wires.replaceChildren();

    for (const link of this.graph.links) {
      const from = this.node(link.from);
      const to = this.node(link.to);
      if (!from || !to) continue;

      const a = this.port(from, true);
      const b = this.port(to, false);

      // A curve rather than a straight line: two boxes in the same column produce a horizontal
      // segment that vanishes behind them, and a bend is what makes crossing wires readable.
      const bend = Math.max(30, Math.abs(b.x - a.x) / 2);
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      path.setAttribute("d", `M ${a.x} ${a.y} C ${a.x + bend} ${a.y}, ${b.x - bend} ${b.y}, ${b.x} ${b.y}`);
      path.setAttribute("class", "graph-wire");
      path.addEventListener("click", () => {
        this.graph.links = this.graph.links.filter((l) => l !== link);
        this.changed();
      });
      this.wires.appendChild(path);
    }
  }

  /** Where a box's port sits, in surface coordinates. */
  port(node, out) {
    const box = this.surface.querySelector(`[data-node="${node.id}"]`);
    const w = box?.offsetWidth ?? 200;
    const h = box?.offsetHeight ?? 60;
    return { x: node.x + (out ? w : 0), y: node.y + h * PORT_Y };
  }

  box(node) {
    const box = document.createElement("div");
    box.className = `graph-node kind-${node.kind.toLowerCase()}`;
    if (this.selected === node.id) box.classList.add("selected");
    box.dataset.node = node.id;
    box.style.left = `${node.x}px`;
    box.style.top = `${node.y}px`;

    const head = document.createElement("div");
    head.className = "graph-head";
    head.appendChild(document.createTextNode(this.title(node)));

    if (node.kind !== OUTPUT_KIND) {
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "graph-remove";
      remove.textContent = "×";
      remove.title = "Remove this box";
      remove.addEventListener("click", (event) => {
        event.stopPropagation();
        this.remove(node.id);
      });
      head.appendChild(remove);
    }

    this.draggable(head, node);
    box.appendChild(head);

    if (node.kind === FACT_KIND) box.appendChild(this.factBody(node));
    if (node.kind !== FACT_KIND && node.kind !== OUTPUT_KIND) box.appendChild(this.joinBody(node));

    if (node.kind !== FACT_KIND) box.appendChild(this.portDot(node, false));
    if (node.kind !== OUTPUT_KIND) box.appendChild(this.portDot(node, true));

    box.addEventListener("pointerdown", () => {
      this.selected = node.id;
    });

    return box;
  }

  title(node) {
    if (node.kind === FACT_KIND) return node.negate ? "not …" : "condition";
    if (node.kind === OUTPUT_KIND) return "Rule fires";
    const found = JOINS.find(([kind]) => kind === node.kind);
    return found ? found[1] : node.kind;
  }

  portDot(node, out) {
    const dot = document.createElement("button");
    dot.type = "button";
    dot.className = `graph-port ${out ? "out" : "in"}`;
    dot.title = out ? "Drag a wire from here" : "Wire into here";
    dot.addEventListener("pointerdown", (event) => event.stopPropagation());
    dot.addEventListener("click", (event) => {
      event.stopPropagation();
      if (out) {
        this.linking = node.id;
        this.say("Now click the box this should feed.");
        return;
      }

      if (!this.linking) {
        this.say("Click an output dot first - the one on the right of a box.");
        return;
      }

      const from = this.linking;
      this.linking = null;
      this.link(from, node.id);
    });
    return dot;
  }

  draggable(handle, node) {
    handle.addEventListener("pointerdown", (event) => {
      if (event.target.closest("button")) return;

      // Pointer capture rather than listeners on the document: a drag that leaves the window
      // otherwise never gets its release and the box follows the cursor forever.
      handle.setPointerCapture(event.pointerId);
      const startX = event.clientX - node.x;
      const startY = event.clientY - node.y;

      const move = (moved) => {
        node.x = Math.max(0, moved.clientX - startX);
        node.y = Math.max(0, moved.clientY - startY);
        const box = this.surface.querySelector(`[data-node="${node.id}"]`);
        if (box) {
          box.style.left = `${node.x}px`;
          box.style.top = `${node.y}px`;
        }
        this.drawWires();
      };

      const up = () => {
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", up);
        handle.removeEventListener("pointercancel", up);

        // Saved on release, not per pixel: a drag is one edit, and sending per frame would
        // post a settings write for every mouse movement.
        this.changed();
      };

      handle.addEventListener("pointermove", move);
      handle.addEventListener("pointerup", up);
      handle.addEventListener("pointercancel", up);
    });
  }

  joinBody(node) {
    const body = document.createElement("div");
    body.className = "graph-body";
    body.appendChild(this.negateBox(node));
    return body;
  }

  factBody(node) {
    const body = document.createElement("div");
    body.className = "graph-body";

    const info = () => this.catalogue.facts.find((f) => f.name === node.fact) ?? null;

    const picker = document.createElement("select");
    for (const fact of this.catalogue.facts) {
      const option = document.createElement("option");
      option.value = fact.name;
      option.textContent = fact.name;
      option.title = fact.help;
      option.selected = fact.name === node.fact;
      picker.appendChild(option);
    }
    picker.addEventListener("change", () => {
      node.fact = picker.value;
      this.changed();
    });
    body.appendChild(picker);

    const chosen = info();
    if (chosen) {
      picker.title = chosen.help;

      if (chosen.argument !== "None") {
        body.appendChild(this.argumentField(node, chosen));
      }

      if (chosen.shape === "Number") {
        body.appendChild(this.comparison(node, chosen));
      }
    }

    body.appendChild(this.negateBox(node));
    return body;
  }

  argumentField(node, info) {
    const row = document.createElement("div");
    row.className = "graph-row";

    if (info.argument === "Text") {
      const input = document.createElement("input");
      input.type = "text";
      input.placeholder = info.name === "AreaContains" ? "part of an area name" : "part of a buff name";
      input.value = node.text ?? "";
      // On "change", not "input": every keystroke would otherwise be a settings write, and a
      // half-typed buff name is not a rule anybody meant to save.
      input.addEventListener("change", () => {
        node.text = input.value;
        this.changed();
      });
      row.appendChild(input);
      return row;
    }

    const label = document.createElement("span");
    label.className = "dim";
    label.textContent = info.argument === "Slot" ? "slot" : info.argument === "Seconds" ? "every" : "within";
    row.appendChild(label);

    const number = document.createElement("input");
    number.type = "number";
    number.value = node.argument ?? 0;
    if (info.argument === "Slot") {
      number.min = 1;
      number.max = 5;
      number.step = 1;
    } else {
      number.min = 0;
      number.step = info.argument === "Seconds" ? 0.1 : 1;
    }
    number.addEventListener("change", () => {
      node.argument = Number(number.value);
      this.changed();
    });
    row.appendChild(number);

    if (info.argument === "Seconds") row.appendChild(unit("s"));
    if (info.argument === "Distance") row.appendChild(unit("u"));
    return row;
  }

  comparison(node, info) {
    const row = document.createElement("div");
    row.className = "graph-row";

    const picker = document.createElement("select");
    for (const name of this.catalogue.comparisons) {
      const option = document.createElement("option");
      option.value = name;
      option.textContent = COMPARE_LABELS[name] ?? name;
      option.selected = name === node.compare;
      picker.appendChild(option);
    }
    picker.addEventListener("change", () => {
      node.compare = picker.value;
      this.changed();
    });
    row.appendChild(picker);

    const number = document.createElement("input");
    number.type = "number";
    number.value = node.value ?? 0;
    number.step = "any";
    number.addEventListener("change", () => {
      node.value = Number(number.value);
      this.changed();
    });
    row.appendChild(number);

    if (info.unit) row.appendChild(unit(info.unit));
    return row;
  }

  negateBox(node) {
    const label = document.createElement("label");
    label.className = "graph-negate";

    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = !!node.negate;
    box.addEventListener("change", () => {
      node.negate = box.checked;
      this.changed();
    });

    label.appendChild(box);
    label.appendChild(document.createTextNode("not"));
    label.title = "Invert this box's own answer";
    return label;
  }
}

function unit(text) {
  const span = document.createElement("span");
  span.className = "dim";
  span.textContent = text;
  return span;
}

/** A graph with nothing in it but somewhere for the answer to go. */
function blank() {
  return {
    nodes: [{ id: "out", kind: OUTPUT_KIND, x: 320, y: 40 }],
    links: [],
  };
}
