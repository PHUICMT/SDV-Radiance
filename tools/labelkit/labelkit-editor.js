// The painting surface every label tool shares: a picture with a label per pixel over it, painted
// with the tools and keys of HF Studio as the window glass labeler had them (B brush, R box,
// L lasso, P polygon, W magic fill, T tile, S stamp; right button erases; wheel zooms; Space pans;
// Z undo; Ctrl+C/V a 16x16 tile; X x-ray; H hides the labels while held).
//
// It knows nothing about any one job. The job says what the values mean and how they look
// (palette), what erasing turns a value into, where paint may not land, and what to draw on top
// (its own tile marks), and hears about every finished change through onChange.
"use strict";
class LabelEditor {
  // options: { canvas, viewport, palette: [{ value, name, colour: [r, g, b] }], stripes,
  //            erase(oldValue) -> newValue, blocked(index, value) -> bool, others(value) -> [r, g, b] | null,
  //            decorate(context, x0, y0, zoom), onChange(), onHover([x, y] | null), cursorTone() -> css colour,
  //            onPick([x, y], button) for a read-only picture, wandTolerance, historyLimit }
  // A picture loaded with readOnly: true is only looked at: the buttons pick a point (onPick), the
  // wheel and Space still move it, and nothing paints.
  constructor(options) {
    this.options = options;
    this.canvas = options.canvas;
    this.viewport = options.viewport;
    this.palette = new Map(options.palette.map(entry => [entry.value, entry]));
    this.value = options.palette[0].value;
    this.tool = "brush"; this.size = 1; this.view = "overlay";
    this.xray = false; this.hideOverlay = false; this.overlayAlpha = 0.9; this.showOthers = false;
    this.zoom = 8; this.panX = 0; this.panY = 0;
    this.width = 0; this.height = 0; this.mask = null; this.art = null; this.artPixels = null; this.images = {};
    this.outside = null;
    this.undoStack = []; this.redoStack = [];
    this.hover = null; this.spaceHeld = false; this.panning = null;
    this.stroke = null; this.rect = null; this.lasso = null; this.polygon = null; this.wand = null;
    this.stampTrace = null; this.stampClip = null; this.stampAt = null; this.stampBox = true; this.tileClip = null;
    this.overlayCache = null; this.overlayDirty = true;
    this.wandPreviewKey = ""; this.wandPreviewPixels = [];
    this.loaded = false;
    this.wire();
  }

  static CLEAR_ALPHA = 20;
  get wandTolerance() { return this.options.wandTolerance ?? 60; }
  get historyLimit() { return this.options.historyLimit ?? 60; }

  // ---- what is being painted ----
  // art: an image (canvas or img) the size of the mask; mask: Uint8Array of values, row by row.
  load({ width, height, art, mask, outside = null, images = {}, readOnly = false }) {
    this.width = width; this.height = height; this.art = art; this.mask = mask; this.outside = outside; this.images = images;
    this.readOnly = readOnly;
    const scratch = document.createElement("canvas"); scratch.width = width; scratch.height = height;
    const context = scratch.getContext("2d");
    if (art) context.drawImage(art, 0, 0);
    this.artPixels = context.getImageData(0, 0, width, height).data;
    this.undoStack = []; this.redoStack = [];
    this.stroke = this.rect = this.lasso = this.polygon = this.wand = this.stampTrace = this.stampAt = null;
    this.wandPreviewKey = "";
    this.loaded = true;
    this.maskChanged();
  }
  replaceMask(mask) { this.mask = mask; this.maskChanged(); this.render(); }
  maskChanged() { this.overlayDirty = true; this.wandPreviewKey = ""; }
  inside(x, y) { return x >= 0 && y >= 0 && x < this.width && y < this.height; }

  // ---- view ----
  sizeCanvas() {
    const box = this.viewport.getBoundingClientRect();
    const ratio = window.devicePixelRatio || 1;
    const width = Math.round(box.width * ratio), height = Math.round(box.height * ratio);
    if (this.canvas.width !== width || this.canvas.height !== height) { this.canvas.width = width; this.canvas.height = height; }
    return { width: box.width, height: box.height, ratio };
  }
  fit() {
    if (!this.loaded) return;
    const box = this.viewport.getBoundingClientRect();
    const fitted = Math.min((box.width - 40) / this.width, (box.height - 40) / this.height);
    this.zoom = fitted >= 1 ? Math.floor(fitted) : Math.max(0.25, Math.floor(fitted * 4) / 4);
    this.panX = Math.round((box.width - this.width * this.zoom) / 2);
    this.panY = Math.round((box.height - this.height * this.zoom) / 2);
  }
  // Brings a region of the picture to the middle of the view, at a zoom where it reads well.
  centreOn(x, y, w = 16, h = 16, zoom = null) {
    const box = this.viewport.getBoundingClientRect();
    if (zoom) this.zoom = zoom;
    this.panX = Math.round(box.width / 2 - (x + w / 2) * this.zoom);
    this.panY = Math.round(box.height / 2 - (y + h / 2) * this.zoom);
  }
  toPixel(event) {
    const box = this.canvas.getBoundingClientRect();
    return [(event.clientX - box.left - this.panX) / this.zoom, (event.clientY - box.top - this.panY) / this.zoom];
  }
  colourOf(value) {
    const entry = this.palette.get(value);
    if (entry) return entry.colour;
    return this.showOthers && this.options.others ? this.options.others(value) : null;
  }
  // The labels as a picture at the picture's own size, made again only when they change.
  overlayCanvas(mono = null) {
    const key = `${mono ? "mono" : "colour"}:${this.showOthers}`;
    if (!this.overlayDirty && this.overlayCache && this.overlayCache.key === key) return this.overlayCache.canvas;
    const canvas = document.createElement("canvas"); canvas.width = this.width; canvas.height = this.height;
    const context = canvas.getContext("2d"); const image = context.createImageData(this.width, this.height);
    const colours = new Map();
    for (let i = 0; i < this.mask.length; i++) {
      const value = this.mask[i]; if (!value) continue;
      let paint = colours.get(value);
      if (paint === undefined) { paint = mono ? (this.palette.has(value) ? mono : null) : this.colourOf(value); colours.set(value, paint); }
      if (!paint) continue;
      const o = i * 4; image.data[o] = paint[0]; image.data[o + 1] = paint[1]; image.data[o + 2] = paint[2]; image.data[o + 3] = 255;
    }
    context.putImageData(image, 0, 0);
    this.overlayCache = { key, canvas };
    this.overlayDirty = false;
    return canvas;
  }
  stripePattern(context) {
    if (!this.stripeTile) {
      this.stripeTile = document.createElement("canvas"); this.stripeTile.width = this.stripeTile.height = 8;
      const t = this.stripeTile.getContext("2d");
      t.strokeStyle = "rgba(255,200,200,0.9)"; t.lineWidth = 2.6;
      t.beginPath(); t.moveTo(-2, 10); t.lineTo(10, -2); t.moveTo(-2, 2); t.lineTo(2, -2); t.moveTo(6, 10); t.lineTo(10, 6); t.stroke();
    }
    return context.createPattern(this.stripeTile, "repeat");
  }
  render() {
    if (!this.loaded) return;
    const { width, height, ratio } = this.sizeCanvas();
    const c = this.canvas.getContext("2d");
    c.setTransform(ratio, 0, 0, ratio, 0, 0);
    c.imageSmoothingEnabled = false;
    c.fillStyle = "#101216"; c.fillRect(0, 0, width, height);
    const z = this.zoom, W = this.width * z, H = this.height * z, x0 = this.panX, y0 = this.panY;
    c.fillStyle = "#23262e"; c.fillRect(x0, y0, W, H);
    if (this.view === "mask") {
      c.fillStyle = "#000"; c.fillRect(x0, y0, W, H);
      c.drawImage(this.overlayCanvas([255, 255, 255]), x0, y0, W, H);
    } else {
      const image = this.view === "overlay" || this.view === "art" ? this.art : this.images[this.view];
      if (image) c.drawImage(image, x0, y0, W, H);
      else { c.fillStyle = "#888"; c.font = "13px sans-serif"; c.fillText("no such picture here", x0 + 6, y0 + 18); }
      if (this.xray) { c.fillStyle = "rgba(6,8,14,.75)"; c.fillRect(x0, y0, W, H); }
      if (this.view !== "art" && !this.hideOverlay) this.drawOverlay(c, x0, y0, W, H, width, height);
    }
    if (this.options.decorate) this.options.decorate(c, x0, y0, z);
    // the tile under the cursor, outlined as HF Studio does
    if (this.hover && this.inside(...this.hover)) {
      const [hx, hy] = this.hover.map(Math.floor);
      c.strokeStyle = "rgba(63,182,255,.85)"; c.lineWidth = 1;
      c.strokeRect(x0 + ((hx >> 4) << 4) * z + .5, y0 + ((hy >> 4) << 4) * z + .5, 16 * z - 1, 16 * z - 1);
      if (!this.panning && !this.spaceHeld && !this.readOnly) this.drawPaintPreview(c, x0, y0, z, hx, hy);
    }
    this.drawToolPreview(c, x0, y0, z);
  }
  drawOverlay(c, x0, y0, W, H, viewWidth, viewHeight) {
    // only the part on screen is laid out at screen size: a sheet can be thousands of pixels tall
    const left = Math.max(0, x0), top = Math.max(0, y0);
    const right = Math.min(viewWidth, x0 + W), bottom = Math.min(viewHeight, y0 + H);
    if (right <= left || bottom <= top) return;
    const layer = document.createElement("canvas");
    layer.width = Math.max(1, Math.round(right - left)); layer.height = Math.max(1, Math.round(bottom - top));
    const l = layer.getContext("2d"); l.imageSmoothingEnabled = false;
    l.drawImage(this.overlayCanvas(), x0 - left, y0 - top, W, H);
    if (this.options.stripes && !this.xray) { l.globalCompositeOperation = "source-atop"; l.fillStyle = this.stripePattern(l); l.fillRect(0, 0, layer.width, layer.height); }
    c.globalAlpha = this.xray ? 0.95 : Math.min(1, 0.8 * this.overlayAlpha);
    c.drawImage(layer, left, top);
    c.globalAlpha = 1;
  }
  // The pixels the tool in hand would paint if clicked here: a pale fill with a crisp outline.
  outlinePixels(c, x0, y0, z, pixels) {
    const paint = this.colourOf(this.value) || [255, 255, 255];
    c.fillStyle = `rgba(${paint[0]}, ${paint[1]}, ${paint[2]}, .38)`;
    for (const [px, py] of pixels) c.fillRect(x0 + px * z, y0 + py * z, z, z);
    const set = new Set(pixels.map(([px, py]) => py * this.width + px));
    c.beginPath();
    for (const [px, py] of pixels) {
      const left = x0 + px * z, top = y0 + py * z;
      if (!set.has(py * this.width + px - 1) || px === 0) { c.moveTo(left, top); c.lineTo(left, top + z); }
      if (!set.has(py * this.width + px + 1) || px === this.width - 1) { c.moveTo(left + z, top); c.lineTo(left + z, top + z); }
      if (!set.has((py - 1) * this.width + px)) { c.moveTo(left, top); c.lineTo(left + z, top); }
      if (!set.has((py + 1) * this.width + px)) { c.moveTo(left, top + z); c.lineTo(left + z, top + z); }
    }
    c.strokeStyle = "rgba(0, 0, 0, .85)"; c.lineWidth = 3; c.stroke();
    c.strokeStyle = "#ffffff"; c.lineWidth = 1.2; c.stroke();
  }
  drawPaintPreview(c, x0, y0, z, hx, hy) {
    if (this.stroke || this.rect || this.lasso || this.stampTrace || this.wand) return;
    let pixels = [];
    if (this.tool === "brush") {
      for (let dy = 0; dy < this.size; dy++) for (let dx = 0; dx < this.size; dx++) if (this.inside(hx + dx, hy + dy)) pixels.push([hx + dx, hy + dy]);
    } else if (this.tool === "tile") {
      const ox = (hx >> 4) << 4, oy = (hy >> 4) << 4;
      for (let y = oy; y < Math.min(oy + 16, this.height); y++) for (let x = ox; x < Math.min(ox + 16, this.width); x++) pixels.push([x, y]);
    } else if (this.tool === "wand") {
      const key = `${hx}:${hy}`;
      if (key !== this.wandPreviewKey) { this.wandPreviewKey = key; this.wandPreviewPixels = this.wandRegion(hx, hy); }
      pixels = this.wandPreviewPixels;
    } else if (this.tool === "stamp" && this.stampClip) {
      return;
    } else {
      pixels = [[hx, hy]];
    }
    if (pixels.length) this.outlinePixels(c, x0, y0, z, pixels);
  }
  tracePath(c, points, x0, y0, z, close) {
    c.beginPath();
    points.forEach(([px, py], k) => k ? c.lineTo(x0 + px * z, y0 + py * z) : c.moveTo(x0 + px * z, y0 + py * z));
    if (close) c.closePath();
  }
  drawToolPreview(c, x0, y0, z) {
    c.setLineDash([5, 4]); c.lineWidth = 1.5;
    if (this.lasso) { c.strokeStyle = "#3fb6ff"; this.tracePath(c, this.lasso.points, x0, y0, z, true); c.stroke(); }
    if (this.rect) {
      const r = this.normalisedRect(this.rect);
      c.strokeStyle = this.rect.erasing ? "#ffd23f" : "#3fb6ff";
      c.strokeRect(x0 + r.x * z, y0 + r.y * z, r.w * z, r.h * z);
    }
    if (this.polygon) {
      c.strokeStyle = "#ffd23f"; this.tracePath(c, this.hover ? [...this.polygon.points, this.hover] : this.polygon.points, x0, y0, z, false); c.stroke();
      const [fx, fy] = this.polygon.points[0];
      c.setLineDash([]); c.fillStyle = "#fff"; c.beginPath(); c.arc(x0 + fx * z, y0 + fy * z, 4, 0, Math.PI * 2); c.fill(); c.setLineDash([5, 4]);
    }
    if (this.stampTrace) {
      c.strokeStyle = "#7ee787";
      if (this.stampTrace.box) { const r = this.normalisedRect(this.stampTrace.box); c.strokeRect(x0 + r.x * z, y0 + r.y * z, r.w * z, r.h * z); }
      else { this.tracePath(c, this.stampTrace.points, x0, y0, z, true); c.stroke(); }
    }
    if (this.tool === "stamp" && this.stampClip && !this.stampTrace && (this.stampAt || this.hover)) {
      const clip = this.stampClip, [ax, ay] = this.stampCorner(this.stampAt || this.hover);
      c.setLineDash([]); c.globalAlpha = 0.75;
      for (let y = 0; y < clip.h; y++) for (let x = 0; x < clip.w; x++) {
        const k = y * clip.w + x;
        if (!clip.shape[k]) continue;
        const paint = clip.data[k] ? this.colourOf(clip.data[k]) : null;
        c.fillStyle = paint ? `rgb(${paint[0]}, ${paint[1]}, ${paint[2]})` : "rgba(0,0,0,.6)";
        c.fillRect(x0 + (ax + x) * z, y0 + (ay + y) * z, z, z);
      }
      c.globalAlpha = 1; c.setLineDash([5, 4]); c.strokeStyle = "#7ee787";
      c.strokeRect(x0 + ax * z, y0 + ay * z, clip.w * z, clip.h * z);
    }
    c.setLineDash([]);
  }
  // ---- the cursor: a crosshair on the exact point, with the tool in hand beside it ----
  toolCursor() {
    const symbol = LabelKit.ICON_PATHS[{ brush: "brush", rect: "box", lasso: "lasso", polygon: "polygon", wand: "wand", tile: "tile", stamp: "stamp" }[this.tool]] || "";
    const tone = this.options.cursorTone ? this.options.cursorTone() : "#3fb6ff";
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="34" height="34" viewBox="0 0 34 34">
      <g stroke-linecap="round"><path d="M9 1v5M9 12v5M1 9h5M12 9h5" stroke="#000" stroke-width="3.4"/><path d="M9 1v5M9 12v5M1 9h5M12 9h5" stroke="#fff" stroke-width="1.5"/></g>
      <circle cx="9" cy="9" r="1.3" fill="#ff4646" stroke="#000" stroke-width=".6"/>
      <g transform="translate(15 15) scale(.75)" fill="none" stroke-linecap="round" stroke-linejoin="round">
        <rect x="-2" y="-2" width="28" height="28" rx="7" fill="#1d2027" stroke="${tone}" stroke-width="1.6"/>
        <g stroke="#fff" stroke-width="2">${symbol}</g></g></svg>`;
    return `url("data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}") 9 9, crosshair`;
  }
  updateCursor() { this.canvas.style.cursor = this.spaceHeld ? "grab" : this.panning ? "grabbing" : this.readOnly ? "pointer" : this.toolCursor(); }

  // ---- writing pixels: the one gate every tool goes through, like HF Studio's setPxI ----
  setPixel(i, value, erasing) {
    if (erasing) { this.mask[i] = this.options.erase ? this.options.erase(this.mask[i]) : 0; return; }
    if (this.options.blocked && this.options.blocked(i, value)) return;
    this.mask[i] = value;
  }
  remember() {
    this.undoStack.push(this.mask.slice());
    if (this.undoStack.length > this.historyLimit) this.undoStack.shift();
    this.redoStack = [];
  }
  changed() { this.maskChanged(); this.render(); if (this.options.onChange) this.options.onChange(); }
  paintSquare(ix, iy, value, erasing) {
    for (let dy = 0; dy < this.size; dy++) for (let dx = 0; dx < this.size; dx++) {
      const x = ix + dx, y = iy + dy;
      if (this.inside(x, y)) this.setPixel(y * this.width + x, value, erasing);
    }
  }
  paintLine(ax, ay, bx, by, value, erasing) {
    const steps = Math.max(Math.abs(bx - ax), Math.abs(by - ay), 1);
    for (let k = 0; k <= steps; k++) this.paintSquare(Math.round(ax + (bx - ax) * k / steps), Math.round(ay + (by - ay) * k / steps), value, erasing);
  }
  fillTile(ix, iy, value, erasing) {
    const ox = (ix >> 4) << 4, oy = (iy >> 4) << 4;
    for (let y = oy; y < Math.min(oy + 16, this.height); y++) for (let x = ox; x < Math.min(ox + 16, this.width); x++) this.setPixel(y * this.width + x, value, erasing);
  }
  isClear(i) { return this.artPixels[i * 4 + 3] < LabelEditor.CLEAR_ALPHA; }
  isOutside(i) { return !!(this.outside && this.outside[i]); }
  // the same colour around the click, stopping at see-through pixels
  wandRegion(sx, sy) {
    const W = this.width, H = this.height, p = this.artPixels, out = [];
    const start = sy * W + sx, o0 = start * 4;
    if (this.isClear(start)) return this.isOutside(start) ? [] : this.clearRegion(sx, sy);
    const r0 = p[o0], g0 = p[o0 + 1], b0 = p[o0 + 2], seen = new Uint8Array(W * H), stack = [start];
    seen[start] = 1;
    while (stack.length) {
      const i = stack.pop(), x = i % W, y = (i / W) | 0;
      out.push([x, y]);
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        const ni = ny * W + nx; if (seen[ni]) continue; seen[ni] = 1;
        const no = ni * 4;
        if (p[no + 3] < LabelEditor.CLEAR_ALPHA) continue;
        if (Math.abs(p[no] - r0) + Math.abs(p[no + 1] - g0) + Math.abs(p[no + 2] - b0) <= this.wandTolerance) stack.push(ni);
      }
    }
    return out;
  }
  clearRegion(sx, sy) {
    const W = this.width, H = this.height, out = [], seen = new Uint8Array(W * H), stack = [sy * W + sx];
    while (stack.length) {
      const i = stack.pop();
      if (seen[i] || !this.isClear(i) || this.isOutside(i)) continue;
      seen[i] = 1; out.push([i % W, (i / W) | 0]);
      const x = i % W, y = (i / W) | 0;
      if (x > 0) stack.push(i - 1); if (x < W - 1) stack.push(i + 1);
      if (y > 0) stack.push(i - W); if (y < H - 1) stack.push(i + W);
    }
    return out;
  }
  wandFill(sx, sy, value, erasing) {
    for (const [x, y] of this.wandRegion(sx, sy)) this.setPixel(y * this.width + x, value, erasing);
  }
  insideLoop(points, visit) {
    let minY = 1e9, maxY = -1e9;
    for (const [, y] of points) { minY = Math.min(minY, y); maxY = Math.max(maxY, y); }
    minY = Math.max(0, Math.floor(minY)); maxY = Math.min(this.height - 1, Math.ceil(maxY));
    for (let y = minY; y <= maxY; y++) {
      const sy = y + 0.5, crossings = [];
      for (let i = 0; i < points.length; i++) {
        const [ax, ay] = points[i], [bx, by] = points[(i + 1) % points.length];
        if ((ay <= sy) !== (by <= sy)) crossings.push(ax + (sy - ay) * (bx - ax) / (by - ay));
      }
      crossings.sort((a, b) => a - b);
      for (let k = 0; k + 1 < crossings.length; k += 2) {
        const a = Math.max(0, Math.ceil(crossings[k] - 0.5)), b = Math.min(this.width - 1, Math.floor(crossings[k + 1] - 0.5));
        for (let x = a; x <= b; x++) visit(x, y);
      }
    }
  }
  normalisedRect(r) {
    const x = Math.max(0, Math.floor(Math.min(r.ax, r.bx))), y = Math.max(0, Math.floor(Math.min(r.ay, r.by)));
    const x2 = Math.min(this.width, Math.floor(Math.max(r.ax, r.bx)) + 1), y2 = Math.min(this.height, Math.floor(Math.max(r.ay, r.by)) + 1);
    return { x, y, w: Math.max(0, x2 - x), h: Math.max(0, y2 - y) };
  }
  // Alt at release keeps labels only inside the loop: everything of the painted value outside it goes.
  fillLoop(points, value, erasing, keepOnlyInside) {
    if (points.length < 3) return;
    if (keepOnlyInside) {
      const inLoop = new Uint8Array(this.mask.length);
      this.insideLoop(points, (x, y) => inLoop[y * this.width + x] = 1);
      for (let i = 0; i < this.mask.length; i++) if (this.mask[i] === value && !inLoop[i]) this.setPixel(i, 0, true);
    } else this.insideLoop(points, (x, y) => this.setPixel(y * this.width + x, value, erasing));
  }

  // ---- stamp: copy a shape of the labels, place it, paste it ----
  stampLift(shapeOf) {
    let minX = 1e9, minY = 1e9, maxX = -1, maxY = -1;
    const cells = [];
    shapeOf((x, y) => { cells.push([x, y]); minX = Math.min(minX, x); minY = Math.min(minY, y); maxX = Math.max(maxX, x); maxY = Math.max(maxY, y); });
    if (!cells.length) return;
    const w = maxX - minX + 1, h = maxY - minY + 1, shape = new Uint8Array(w * h), data = new Uint8Array(w * h);
    for (const [x, y] of cells) { const k = (y - minY) * w + (x - minX); shape[k] = 1; data[k] = this.mask[y * this.width + x]; }
    this.stampClip = { w, h, shape, data };
    LabelKit.toast(`copied ${w}×${h}: left click pastes, right click drops`);
  }
  stampCorner(point) { const clip = this.stampClip; return [Math.round(point[0] - clip.w / 2), Math.round(point[1] - clip.h / 2)]; }
  stampPaste() {
    const clip = this.stampClip; if (!clip || !this.stampAt) return;
    const [ax, ay] = this.stampCorner(this.stampAt);
    this.remember();
    for (let y = 0; y < clip.h; y++) for (let x = 0; x < clip.w; x++) {
      const k = y * clip.w + x, tx = ax + x, ty = ay + y;
      if (!clip.shape[k] || !this.inside(tx, ty)) continue;
      // the copy's own labels land as they are; where paint may not land, it does not land here either
      if (clip.data[k]) this.setPixel(ty * this.width + tx, clip.data[k], false); else this.mask[ty * this.width + tx] = 0;
    }
    this.stampAt = null;
    this.changed();
  }
  finishPolygon() {
    const { points, value, erasing } = this.polygon; this.polygon = null;
    if (points.length >= 3) { this.remember(); this.fillLoop(points, value, erasing, false); this.changed(); } else this.render();
  }

  // ---- history and the tile clipboard ----
  undo() { if (!this.undoStack.length) return false; this.redoStack.push(this.mask); this.mask = this.undoStack.pop(); this.changed(); return true; }
  redo() { if (!this.redoStack.length) return false; this.undoStack.push(this.mask); this.mask = this.redoStack.pop(); this.changed(); return true; }
  hoveredTileOrigin() {
    if (!this.hover || !this.inside(...this.hover)) { LabelKit.toast("point at a tile first"); return null; }
    const [ix, iy] = this.hover.map(Math.floor);
    return [(ix >> 4) << 4, (iy >> 4) << 4];
  }
  copyTile() {
    const origin = this.hoveredTileOrigin(); if (!origin) return;
    const [ox, oy] = origin, clip = new Uint8Array(256);
    for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) if (this.inside(ox + x, oy + y)) clip[y * 16 + x] = this.mask[(oy + y) * this.width + ox + x];
    this.tileClip = clip; LabelKit.toast("tile copied");
  }
  pasteTile() {
    if (!this.tileClip) return LabelKit.toast("nothing copied");
    const origin = this.hoveredTileOrigin(); if (!origin) return;
    const [ox, oy] = origin;
    this.remember();
    for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) if (this.inside(ox + x, oy + y)) this.mask[(oy + y) * this.width + ox + x] = this.tileClip[y * 16 + x];
    this.changed();
  }
  // Esc: drop the stamp copy, cancel a polygon or lasso. True when it had something to cancel.
  escape() {
    if (this.stampAt) this.stampAt = null;
    else if (this.stampTrace) this.stampTrace = null;
    else if (this.stampClip) { this.stampClip = null; LabelKit.toast("stamp copy dropped"); }
    else if (this.polygon) this.polygon = null;
    else if (this.lasso) this.lasso = null;
    else return false;
    this.render();
    return true;
  }

  // ---- keys: physical keys (event.code), so they work with a Thai layout too ----
  static TOOL_KEYS = { KeyB: "brush", KeyR: "rect", KeyL: "lasso", KeyP: "polygon", KeyW: "wand", KeyT: "tile", KeyS: "stamp" };
  // True when the key was the editor's; the job handles the rest.
  handleKey(event) {
    const code = event.code, ctrl = event.ctrlKey || event.metaKey;
    if (code === "Space") { event.preventDefault(); this.spaceHeld = true; this.updateCursor(); return true; }
    if (code === "KeyH" && !ctrl) { if (!this.hideOverlay) { this.hideOverlay = true; this.render(); } return true; }
    if (!this.loaded) return false;
    if (this.readOnly) {
      if (code === "KeyX" && !ctrl) { this.xray = !this.xray; this.render(); return true; }
      if (code === "KeyF" && !ctrl) { this.fit(); this.render(); return true; }
      return false;
    }
    if (ctrl && (code === "KeyY" || (code === "KeyZ" && event.shiftKey))) { event.preventDefault(); this.redo(); return true; }
    if (code === "KeyZ" && !event.shiftKey) { event.preventDefault(); this.undo(); return true; }
    if (ctrl && code === "KeyC" && !event.shiftKey) { event.preventDefault(); this.copyTile(); return true; }
    if (ctrl && code === "KeyV" && !event.shiftKey) { event.preventDefault(); this.pasteTile(); return true; }
    if (ctrl || event.altKey) return false;
    if (code === "Enter") { if (this.polygon) this.finishPolygon(); else if (this.stampAt) this.stampPaste(); return true; }
    if (event.shiftKey) return false;
    if (LabelEditor.TOOL_KEYS[code]) { this.tool = LabelEditor.TOOL_KEYS[code]; this.polygon = null; }
    else if (code === "BracketLeft") this.size = this.size === 4 ? 2 : 1;
    else if (code === "BracketRight") this.size = this.size === 1 ? 2 : 4;
    else if (code === "KeyX") this.xray = !this.xray;
    else if (code === "KeyF") this.fit();
    else return false;
    this.updateCursor(); this.render();
    return true;
  }
  handleKeyUp(event) {
    if (event.code === "Space") { this.spaceHeld = false; this.updateCursor(); }
    if (event.code === "KeyH" && this.hideOverlay) { this.hideOverlay = false; this.render(); }
  }

  // ---- pointer ----
  wire() {
    const big = this.canvas;
    big.addEventListener("contextmenu", event => event.preventDefault());
    big.addEventListener("mousedown", event => {
      if (!this.loaded) return;
      event.preventDefault();
      if (event.button === 1 || this.spaceHeld) { this.panning = { x: event.clientX, y: event.clientY, panX: this.panX, panY: this.panY }; this.updateCursor(); return; }
      const [px, py] = this.toPixel(event), ix = Math.floor(px), iy = Math.floor(py);
      if (this.readOnly) { if (this.inside(ix, iy) && this.options.onPick) this.options.onPick([ix, iy], event.button); return; }
      const erasing = event.button === 2, value = this.value;
      switch (this.tool) {
        case "brush":
          if (!this.inside(ix, iy)) return;
          this.remember();
          this.stroke = { value, erasing, lastX: ix, lastY: iy, ruler: event.shiftKey ? { x: ix, y: iy, axis: null } : null };
          this.paintSquare(ix, iy, value, erasing); this.maskChanged(); this.render(); break;
        case "tile":
          if (!this.inside(ix, iy)) return;
          this.remember(); this.stroke = { value, erasing, tile: true }; this.fillTile(ix, iy, value, erasing); this.maskChanged(); this.render(); break;
        case "rect":
          this.rect = { ax: px, ay: py, bx: px, by: py, value, erasing }; this.render(); break;
        case "lasso":
          this.lasso = { points: [[px, py]], value, erasing }; break;
        case "polygon": {
          if (!this.polygon) { this.polygon = { points: [[px, py]], value, erasing }; this.render(); break; }
          const [fx, fy] = this.polygon.points[0];
          if (this.polygon.points.length >= 3 && Math.hypot((px - fx) * this.zoom, (py - fy) * this.zoom) <= 6) this.finishPolygon();
          else { this.polygon.points.push([px, py]); this.render(); }
          break;
        }
        case "wand": {
          if (!this.inside(ix, iy)) return;
          const o = (iy * this.width + ix) * 4, p = this.artPixels;
          this.remember(); this.wand = { value, erasing, seed: [p[o], p[o + 1], p[o + 2]], clear: p[o + 3] < LabelEditor.CLEAR_ALPHA };
          this.wandFill(ix, iy, value, erasing); this.maskChanged(); this.render(); break;
        }
        case "stamp": {
          // Right button: drop the copy held (or the shape being picked); paints nothing.
          if (erasing) {
            if (this.stampClip || this.stampTrace) { this.stampClip = null; this.stampTrace = null; this.stampAt = null; LabelKit.toast("stamp copy dropped"); this.render(); }
            break;
          }
          // Holding a copy, a left click pastes it there; the copy stays held for the next click.
          if (this.stampClip && !event.shiftKey) { this.stampAt = [px, py]; this.stampPaste(); break; }
          const box = this.stampClip ? this.stampBox : this.stampBox !== event.shiftKey;
          this.stampTrace = box ? { box: { ax: px, ay: py, bx: px, by: py } } : { points: [[px, py]] };
          break;
        }
      }
    });
    big.addEventListener("dblclick", () => { if (this.polygon) this.finishPolygon(); });
    window.addEventListener("mousemove", event => {
      if (!this.loaded) return;
      if (this.panning) {
        this.panX = this.panning.panX + event.clientX - this.panning.x;
        this.panY = this.panning.panY + event.clientY - this.panning.y;
        this.render(); return;
      }
      const [px, py] = this.toPixel(event), ix = Math.floor(px), iy = Math.floor(py);
      const overCanvas = event.target === big;
      const before = this.hover;
      this.hover = overCanvas ? [px, py] : null;
      if (this.options.onHover && (overCanvas || before)) this.options.onHover(this.hover);
      let painted = false;
      if (this.stroke && !this.stroke.tile) {
        let [tx, ty] = [ix, iy];
        const ruler = this.stroke.ruler;
        if (ruler) {
          if (!ruler.axis && Math.max(Math.abs(tx - ruler.x), Math.abs(ty - ruler.y)) > 2) ruler.axis = Math.abs(tx - ruler.x) >= Math.abs(ty - ruler.y) ? "x" : "y";
          if (ruler.axis === "x") ty = ruler.y; else if (ruler.axis === "y") tx = ruler.x;
        }
        this.paintLine(this.stroke.lastX, this.stroke.lastY, tx, ty, this.stroke.value, this.stroke.erasing);
        this.stroke.lastX = tx; this.stroke.lastY = ty; painted = true;
      } else if (this.stroke && this.stroke.tile && this.inside(ix, iy)) { this.fillTile(ix, iy, this.stroke.value, this.stroke.erasing); painted = true; }
      else if (this.rect) { this.rect.bx = px; this.rect.by = py; }
      else if (this.lasso) { const last = this.lasso.points.at(-1); if (Math.abs(px - last[0]) >= 0.5 || Math.abs(py - last[1]) >= 0.5) this.lasso.points.push([px, py]); }
      else if (this.stampTrace) {
        if (this.stampTrace.box) { this.stampTrace.box.bx = px; this.stampTrace.box.by = py; }
        else this.stampTrace.points.push([px, py]);
      } else if (this.wand && this.wand.clear && this.inside(ix, iy)) {
        if (this.isClear(iy * this.width + ix)) { this.wandFill(ix, iy, this.wand.value, this.wand.erasing); painted = true; }
      } else if (this.wand && this.inside(ix, iy)) {
        // keep filling only while the cursor stays on the material the fill started on
        const o = (iy * this.width + ix) * 4, p = this.artPixels, seed = this.wand.seed;
        if (p[o + 3] >= LabelEditor.CLEAR_ALPHA && Math.abs(p[o] - seed[0]) + Math.abs(p[o + 1] - seed[1]) + Math.abs(p[o + 2] - seed[2]) <= 80) {
          this.wandFill(ix, iy, this.wand.value, this.wand.erasing); painted = true;
        }
      }
      if (painted) this.maskChanged();
      if (overCanvas || this.stroke || this.rect || this.lasso || this.stampTrace) this.render();
    });
    window.addEventListener("mouseup", event => {
      if (this.panning) { this.panning = null; this.updateCursor(); return; }
      if (this.stroke || this.wand) { this.stroke = null; this.wand = null; this.changed(); return; }
      if (this.rect) {
        const r = this.normalisedRect(this.rect), { value, erasing } = this.rect;
        this.rect = null;
        if (r.w && r.h) { this.remember(); for (let y = r.y; y < r.y + r.h; y++) for (let x = r.x; x < r.x + r.w; x++) this.setPixel(y * this.width + x, value, erasing); this.changed(); }
        else this.render();
        return;
      }
      if (this.lasso) {
        const { points, value, erasing } = this.lasso; this.lasso = null;
        if (points.length >= 3) { this.remember(); this.fillLoop(points, value, erasing, event.altKey && !erasing); this.changed(); } else this.render();
        return;
      }
      if (this.stampTrace) {
        const trace = this.stampTrace; this.stampTrace = null;
        if (trace.box) { const r = this.normalisedRect(trace.box); this.stampLift(visit => { for (let y = r.y; y < r.y + r.h; y++) for (let x = r.x; x < r.x + r.w; x++) visit(x, y); }); }
        else if (trace.points.length >= 3) this.stampLift(visit => this.insideLoop(trace.points, visit));
        this.render();
      }
    });
    big.addEventListener("mouseleave", () => { this.hover = null; if (this.options.onHover) this.options.onHover(null); this.render(); });
    big.addEventListener("wheel", event => {
      if (!this.loaded) return;
      event.preventDefault();
      const box = big.getBoundingClientRect(), mx = event.clientX - box.left, my = event.clientY - box.top;
      const before = this.zoom;
      let zoom = before * (event.deltaY < 0 ? 1.25 : 0.8);
      zoom = Math.max(0.25, Math.min(64, Math.round(zoom * 4) / 4));
      this.panX = mx - (mx - this.panX) * zoom / before;
      this.panY = my - (my - this.panY) * zoom / before;
      this.zoom = zoom; this.render();
    }, { passive: false });
  }
}
