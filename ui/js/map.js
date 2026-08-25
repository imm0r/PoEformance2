// The map tab: the area's layout, with the player and what is around them on top.
//
// Two rates, deliberately. The LAYOUT arrives once per area and is painted onto an
// offscreen canvas exactly once - it is a few hundred thousand pixels and re-walking it
// every second would be the whole cost of this panel. The MARKERS arrive with every state
// push and are drawn over a blit of that canvas, which is one operation however big the
// area is.

const KIND_COLOURS = {
  monster: "#ff4040",
  chest: "#ffd633",
  npc: "#99e699",
  loot: "#66ccff",
};

/**
 * What arrived, in one line, for when the map is blank.
 *
 * A blank map has three causes that look identical: no layout was sent, one was sent with no
 * dimensions, or one was sent whose runs are all empty - the last being a terrain grid that
 * produced no outline. They need different fixes, and none of them was visible from a panel
 * showing an empty rectangle.
 */
function describeLayout(raw, kept) {
  if (!raw) return "no layout has been sent";
  if (!kept) return `layout ${raw.width ?? "?"}x${raw.height ?? "?"} - nothing to draw yet`;

  let set = 0;
  let on = false;
  for (const run of kept.runs ?? []) {
    if (on) set += run;
    on = !on;
  }

  return `layout ${kept.width}x${kept.height} step ${kept.step ?? 1}, `
    + `${(kept.runs ?? []).length} runs, ${set} outline pixels`;
}

/** Expands the run-length runs into a painted offscreen canvas, once per area. */
function paintLayout(layout) {
  const surface = document.createElement("canvas");
  surface.width = layout.width;
  surface.height = layout.height;

  const context = surface.getContext("2d");
  const image = context.createImageData(layout.width, layout.height);
  const pixels = image.data;

  // Runs alternate, starting with empty. Only the set runs are written; the buffer starts
  // fully transparent, which is what the empty runs mean.
  let index = 0;
  let set = false;
  for (const run of layout.runs) {
    if (set) {
      for (let i = 0; i < run && index + i < layout.width * layout.height; i++) {
        const p = (index + i) * 4;
        pixels[p] = 150;
        pixels[p + 1] = 200;
        pixels[p + 2] = 255;
        pixels[p + 3] = 255;
      }
    }
    index += run;
    set = !set;
  }

  context.putImageData(image, 0, 0);
  return surface;
}

export class MapPanel {
  constructor(canvas, frame) {
    this.canvas = canvas;
    this.frame = frame;
    this.layout = null;
    this.surface = null;
    this.area = 0;
    this.state = null;
    this.follow = true;
    this.zoom = 1;
    this.visible = false;
    this.note = "no layout has been sent";
    this.askedAt = 0;
  }

  /**
   * True when the layout on hand is not the one for the area being played.
   *
   * ALSO TRUE WHILE THERE IS NO LAYOUT AT ALL, which the first version got wrong. Terrain
   * loads a while after the area does - the host says so in its own status line - so the
   * answer to the one question asked on entering an area is very often "nothing yet". That
   * answer used to be recorded against the area and never revisited, so an unlucky portal
   * left the map blank until the next one. Throttled rather than free, because the comment
   * on the host's side is right: a page bug here must not become a per-second cost.
   */
  needsLayout(area) {
    if (!this.visible || area === 0) return false;
    if (this.area !== area) return true;
    if (this.layout) return false;

    const now = Date.now();
    if (now - this.askedAt < 3000) return false;
    this.askedAt = now;
    return true;
  }

  setLayout(area, layout) {
    this.area = area;
    this.layout = layout && layout.width > 0 ? layout : null;
    this.surface = this.layout ? paintLayout(this.layout) : null;
    this.note = describeLayout(layout, this.layout);
    this.draw();
  }

  setState(map) {
    this.state = map;
    this.draw();
  }

  show(visible) {
    this.visible = visible;
    if (visible) this.draw();
  }

  draw() {
    if (!this.visible) return;

    // Match the canvas to its box first: drawing into a stale size is what produces a
    // blurry, offset map after a window resize.
    const width = this.frame.clientWidth;
    const height = this.frame.clientHeight;
    if (width <= 0 || height <= 0) return;
    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
    }

    const context = this.canvas.getContext("2d");
    context.clearRect(0, 0, width, height);

    // The note goes on FIRST and stays up whatever else is drawn: a map with markers and no
    // outline looked exactly like a map whose outline had not arrived, and the difference
    // between those is the difference between a drawing fault and a reading one.
    context.fillStyle = "#8a8f9c";
    context.font = "12px ui-monospace, monospace";
    context.fillText(this.note, 12, 20);

    if (!this.surface) {
      context.fillText("terrain loads a while after the area does", 12, 38);
      return;
    }

    // Fit the whole layout, then apply the zoom on top of it.
    const fit = Math.min(width / this.surface.width, height / this.surface.height);
    const scale = fit * this.zoom;
    const step = this.layout.step || 1;

    // Centre on the player when following, otherwise on the layout.
    const focusX = this.follow && this.state ? this.state.playerX / step : this.surface.width / 2;
    const focusY = this.follow && this.state ? this.state.playerY / step : this.surface.height / 2;
    const originX = (width / 2) - (focusX * scale);
    const originY = (height / 2) - (focusY * scale);

    context.imageSmoothingEnabled = false;
    context.drawImage(
      this.surface, originX, originY,
      this.surface.width * scale, this.surface.height * scale);

    if (!this.state) return;

    for (const marker of this.state.markers ?? []) {
      const x = originX + ((marker.x / step) * scale);
      const y = originY + ((marker.y / step) * scale);
      if (x < -8 || y < -8 || x > width + 8 || y > height + 8) continue;

      context.fillStyle = KIND_COLOURS[marker.kind] ?? "#cccccc";
      context.beginPath();
      context.arc(x, y, 2.5, 0, Math.PI * 2);
      context.fill();
    }

    // The player last and larger, so it is never lost among the markers.
    const px = originX + ((this.state.playerX / step) * scale);
    const py = originY + ((this.state.playerY / step) * scale);
    context.strokeStyle = "#000000";
    context.lineWidth = 2;
    context.fillStyle = "#4dff4d";
    context.beginPath();
    context.arc(px, py, 4.5, 0, Math.PI * 2);
    context.fill();
    context.stroke();
  }
}
