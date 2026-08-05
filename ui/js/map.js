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
  }

  /** True when the layout on hand is not the one for the area being played. */
  needsLayout(area) {
    return this.visible && area !== 0 && this.area !== area;
  }

  setLayout(area, layout) {
    this.area = area;
    this.layout = layout && layout.width > 0 ? layout : null;
    this.surface = this.layout ? paintLayout(this.layout) : null;
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

    if (!this.surface) {
      context.fillStyle = "#8a8f9c";
      context.font = "13px system-ui, sans-serif";
      context.fillText("no layout yet — terrain loads a while after the area does", 16, 28);
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
