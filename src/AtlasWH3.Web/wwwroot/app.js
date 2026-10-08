'use strict';
// AtlasWH3 web editor. World units = tile-map pixels (2 x 2 per hex, image rows top = north); the blend is K times
// finer (K = 4 on 3K maps). Apple Pencil / mouse draw, fingers pan and pinch, two-finger tap undoes.

const $ = id => document.getElementById(id);
const canvas = $('view'), ctx = canvas.getContext('2d');
const S = {
  info: null, mode: 'tiles', K: 4,
  tool: { tiles: 'paint', blend: 'paint' },
  sel: { tiles: null, blend: 0 },
  view: { ox: 0, oy: 0, s: 1 },            // screen(device px) = (world - o) * s
  dpr: 1, dirty: true,
  // tile map
  tileCanvas: null, tileCtx: null, tileData: null, Hh: 0, Wh: 0,
  pending: [], issues: [], simHoles: [], simEdited: new Set(), linePts: [], strokeHexes: null, strokeKind: null,
  tileStrokes: 0,
  // blend
  tiles: new Map(), queue: [], inflight: 0, lut: null, blendState: { unsaved: 0, canUndo: false, canRedo: false },
  strokePts: null, previewDots: [],
  hover: null, busy: false,
};
const TOOLS = {
  tiles: [['pan', '✋'], ['paint', 'Paint'], ['erase', 'Remove'], ['line', 'Line'], ['fill', 'Fill'], ['pick', 'Pick']],
  blend: [['pan', '✋'], ['paint', 'Paint'], ['pick', 'Pick']],
};

// ---------------------------------------------------------------- utils

async function api(path, body, method) {
  const opt = { method: method || (body === undefined ? 'GET' : 'POST'), headers: {} };
  if (body !== undefined && body !== null) { opt.body = JSON.stringify(body); opt.headers['Content-Type'] = 'application/json'; }
  const r = await fetch(path, opt);
  const ct = r.headers.get('content-type') || '';
  const data = ct.includes('json') ? await r.json() : await r.arrayBuffer();
  if (!r.ok) { const e = new Error((data && data.error) || r.statusText); e.status = r.status; throw e; }
  return data;
}
let toastTimer;
function toast(msg, err) {
  const t = $('toast'); t.textContent = msg; t.className = err ? 'err' : ''; t.style.display = 'block';
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.style.display = 'none', err ? 6000 : 2500);
}
const hexToRgb = h => [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
const redraw = () => { S.dirty = true; };

// ---------------------------------------------------------------- hex geometry (see HexTileMap / TileMapOps)

const hexTop = (c, r) => 2 * S.Hh - (2 * r + (c & 1)) - 1;            // image row of the hex's upper pixel row
function hexAt(px, py) {
  if (px < 0 || py < 0 || px >= S.Wh * 2 || py >= S.Hh * 2 + 1) return null;
  const c = Math.floor(px / 2), n = 2 * S.Hh - Math.floor(py) - (c & 1);
  if (n < 0) return null;
  const r = Math.floor(n / 2);
  return c < S.Wh && r < S.Hh ? [c, r] : null;
}
const cube = (c, r) => [c, r - (c - (c & 1)) / 2];
const offset = (x, z) => [x, z + (x - (x & 1)) / 2];
function circle(c, r, R) {
  const [cx, cz] = cube(c, r), out = [];
  for (let dx = -R; dx <= R; dx++)
    for (let dz = Math.max(-R, -dx - R); dz <= Math.min(R, -dx + R); dz++) {
      const [hc, hr] = offset(cx + dx, cz + dz);
      if (hc >= 0 && hr >= 0 && hc < S.Wh && hr < S.Hh) out.push([hc, hr]);
    }
  return out;
}

// ---------------------------------------------------------------- view

function resize() {
  S.dpr = window.devicePixelRatio || 1;
  const w = Math.round(canvas.clientWidth * S.dpr), h = Math.round(canvas.clientHeight * S.dpr);
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
  redraw();
}
function fit() {
  if (!S.info) return;
  const [W, H] = S.info.pixels;
  const s = Math.min(canvas.width / W, canvas.height / H);
  S.view = { s, ox: W / 2 - canvas.width / 2 / s, oy: H / 2 - canvas.height / 2 / s };
  redraw();
}
const toWorld = (cx, cy) => [S.view.ox + cx * S.dpr / S.view.s, S.view.oy + cy * S.dpr / S.view.s];
const toScreen = (wx, wy) => [(wx - S.view.ox) * S.view.s, (wy - S.view.oy) * S.view.s];
function zoomAt(cx, cy, factor) {
  const [wx, wy] = toWorld(cx, cy);
  S.view.s = Math.min(200, Math.max(0.02, S.view.s * factor));
  S.view.ox = wx - cx * S.dpr / S.view.s; S.view.oy = wy - cy * S.dpr / S.view.s;
  redraw();
}
function centreOn(c, r) {
  const s = Math.max(S.view.s, 8 * S.dpr);
  S.view = { s, ox: 2 * c + 1 - canvas.width / 2 / s, oy: hexTop(c, r) + 1 - canvas.height / 2 / s };
  redraw();
}

// ---------------------------------------------------------------- loading

async function load() {
  S.info = await api('/api/info');
  [S.Wh, S.Hh] = S.info.hexes;
  S.K = S.info.blendSize[0] / S.info.pixels[0];
  S.colourSets = S.info.colourSets;
  S.sel.tiles = (S.info.sets.find(s => s.name === 'mountains_temperate') || S.info.sets[0]).name;
  const only = $('onlyReplace');
  for (const t of S.info.textures) only.add(new Option(`${t.index} ${t.name}`, t.index));
  buildLut();
  await loadTilePng();
  const st = await api('/api/tiles/state'); applyTileState(st);
  S.blendState = await api('/api/blend/state');
  resize(); fit(); setMode('tiles'); refreshHistory();
  $('status').textContent = `${S.info.map}   tile map ${S.info.tileMap}   blend ${S.info.blendFile}`;
}

async function loadTilePng() {
  const blob = new Blob([await api('/api/tiles/png')], { type: 'image/png' });
  // exact colours matter (tile sets are matched by RGB): no colour management or premultiplication
  const img = await createImageBitmap(blob, { colorSpaceConversion: 'none', premultiplyAlpha: 'none' });
  S.tileCanvas = document.createElement('canvas');
  S.tileCanvas.width = img.width; S.tileCanvas.height = img.height;
  S.tileCtx = S.tileCanvas.getContext('2d', { willReadFrequently: true });
  S.tileCtx.drawImage(img, 0, 0);
  S.tileData = S.tileCtx.getImageData(0, 0, img.width, img.height);
  redraw();
}

function setHexColour(c, r, rgb) {
  const [R, G, B] = typeof rgb === 'string' ? hexToRgb(rgb) : rgb, d = S.tileData.data, w = S.tileData.width;
  const x = 2 * c, y = hexTop(c, r);
  for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) {
    const i = ((y + dy) * w + x + dx) * 4; d[i] = R; d[i + 1] = G; d[i + 2] = B; d[i + 3] = 255;
  }
}
function flushHexes(hexes) {
  if (!hexes.length) return;
  let x0 = 1e9, y0 = 1e9, x1 = -1, y1 = -1;
  for (const [c, r] of hexes) { const x = 2 * c, y = hexTop(c, r); x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x + 1); y1 = Math.max(y1, y + 1); }
  S.tileCtx.putImageData(S.tileData, 0, 0, x0, y0, x1 - x0 + 1, y1 - y0 + 1);
  redraw();
}
function colourAt(c, r) {
  const d = S.tileData.data, i = (hexTop(c, r) * S.tileData.width + 2 * c) * 4;
  return [d[i], d[i + 1], d[i + 2]].map(v => v.toString(16).padStart(2, '0')).join('');
}

// ---------------------------------------------------------------- tile map edits

function applyTileState(st) {
  if (st.changed) { for (const [c, r, rgb] of st.changed) setHexColour(c, r, rgb); flushHexes(st.changed); }
  S.pending = st.pendingHexes || S.pending;
  S.issues = st.issues || [];
  S.tileStrokes = st.strokes;
  S.pendingCount = st.pending;
  renderIssues(); updateButtons(); redraw();
}

async function tileOp(op) {
  try { applyTileState(await api('/api/tiles/op', op)); }
  catch (e) { toast(e.message, true); await loadTilePng(); applyTileState(await api('/api/tiles/state')); }
}

function tileDab(px, py) {
  const h = hexAt(px, py); if (!h) return;
  const R = +$('size').value;
  const rgb = S.strokeKind === 'paint' ? S.info.sets.find(s => s.name === S.sel.tiles).rgb : null;
  const added = [];
  for (const [c, r] of circle(h[0], h[1], R)) {
    const k = c + ',' + r;
    if (S.strokeHexes.has(k)) continue;
    S.strokeHexes.set(k, [c, r]); added.push([c, r]);
    if (rgb) setHexColour(c, r, rgb);
  }
  if (rgb) flushHexes(added); else redraw();
}

function tileDown(px, py) {
  const tool = S.tool.tiles, h = hexAt(px, py);
  if (tool === 'paint' || tool === 'erase') { S.strokeHexes = new Map(); S.strokeKind = tool; tileDab(px, py); return true; }
  if (!h) return false;
  if (tool === 'line') { S.linePts.push(h); redraw(); }
  else if (tool === 'fill') tileOp({ op: 'fill', set: S.sel.tiles, at: h });
  else if (tool === 'pick') { const set = S.colourSets[colourAt(h[0], h[1])]; if (set) { S.sel.tiles = set; renderPalette(); toast(set); } }
  return false;
}
function tileUp() {
  if (!S.strokeHexes) return;
  const hexes = [...S.strokeHexes.values()], kind = S.strokeKind;
  S.strokeHexes = null; S.strokeKind = null;
  if (!hexes.length) return;
  tileOp(kind === 'paint' ? { op: 'paint', set: S.sel.tiles, hexes } : { op: 'erase', hexes });
}
function lineDone() {
  if (S.linePts.length >= 2) tileOp({ op: 'line', set: S.sel.tiles, points: S.linePts });
  S.linePts = []; redraw();
}

// ---------------------------------------------------------------- blend tiles

function buildLut() {
  S.lut = new Uint32Array(256);
  const natural = $('natural').checked;
  for (const t of S.info.textures) {
    const [r, g, b] = hexToRgb(natural ? t.natural : t.rgb);
    S.lut[t.index] = (255 << 24) | (b << 16) | (g << 8) | r;
  }
  for (const t of S.tiles.values()) if (t.bytes) paintTile(t);
  redraw();
}
const maxLevel = () => Math.max(0, Math.ceil(Math.log2(Math.max(...S.info.blendSize) / 256)));
function level() {
  const blendPerDevicePx = S.K / S.view.s;
  return Math.max(0, Math.min(maxLevel(), Math.floor(Math.log2(Math.max(1, blendPerDevicePx)))));
}
function paintTile(t) {
  const img = new ImageData(256, 256), u32 = new Uint32Array(img.data.buffer);
  for (let i = 0; i < 65536; i++) u32[i] = S.lut[t.bytes[i]];
  if (!t.canvas) { t.canvas = document.createElement('canvas'); t.canvas.width = t.canvas.height = 256; }
  t.canvas.getContext('2d').putImageData(img, 0, 0);
}
function requestTile(key, L, tx, ty, prio) {
  let t = S.tiles.get(key);
  if (t && !t.stale) return t;
  if (t && t.loading) return t;
  if (!t) { t = { L, tx, ty }; S.tiles.set(key, t); }
  t.loading = true;
  S.queue.push({ t, prio });
  pump();
  return t;
}
function pump() {
  S.queue.sort((a, b) => a.prio - b.prio);
  while (S.inflight < 6 && S.queue.length) {
    const { t } = S.queue.shift();
    S.inflight++;
    const epoch = t.epoch = (t.epoch || 0) + 1;
    fetch(`/api/blend/tile/${t.L}/${t.tx}/${t.ty}`).then(r => r.arrayBuffer()).then(buf => {
      if (epoch !== t.epoch) return;
      t.bytes = new Uint8Array(buf); t.stale = false; paintTile(t); redraw();
    }).catch(() => { }).finally(() => { t.loading = false; S.inflight--; pump(); });
  }
}
function invalidate(rect) {           // blend px rect [x0,y0,x1,y1], or all
  for (const t of S.tiles.values()) {
    if (rect) {
      const span = 256 << t.L, x0 = t.tx * span, y0 = t.ty * span;
      if (x0 > rect[2] || y0 > rect[3] || x0 + span <= rect[0] || y0 + span <= rect[1]) continue;
    }
    t.stale = true; t.loading = false; t.epoch = (t.epoch || 0) + 1;
  }
  S.queue = [];
  redraw();
}
function blendValueAt(px, py) {     // texture under a world point, from the finest cached tile
  const bx = px * S.K, by = py * S.K;
  for (let L = 0; L <= maxLevel(); L++) {
    const span = 256 << L, t = S.tiles.get(`${L}/${Math.floor(bx / span)}/${Math.floor(by / span)}`);
    if (t && t.bytes) {
      const x = Math.floor((bx - t.tx * span) / (1 << L)), y = Math.floor((by - t.ty * span) / (1 << L));
      return t.bytes[y * 256 + x];
    }
  }
  return null;
}

// ---------------------------------------------------------------- blend edits

const blendRadius = () => { const v = +$('size').value; return Math.round(v * v / 25) + 1; };   // blend px
function applyBlendState(st) { S.blendState = st; updateButtons(); renderIssues(); }
function blendDown(px, py, pressure) {
  const tool = S.tool.blend;
  if (tool === 'paint') { S.strokePts = []; blendMove(px, py, pressure); return true; }
  if (tool === 'pick') {
    const v = blendValueAt(px, py);
    if (v != null && v < 255) { S.sel.blend = v; renderPalette(); toast(S.info.textures[v].name); }
  }
  return false;
}
function blendMove(px, py, pressure) {
  const p = $('pressure').checked ? (pressure || 0.5) : 1;
  S.strokePts.push([+(px * S.K).toFixed(1), +(py * S.K).toFixed(1), +p.toFixed(3)]);
  S.previewDots.push([px, py, p]);
  redraw();
}
async function blendUp() {
  const pts = S.strokePts; S.strokePts = null;
  if (!pts || !pts.length) return;
  try {
    const st = await api('/api/blend/stroke', {
      group: S.sel.blend, radius: blendRadius(), softness: $('softness').value / 100, strength: $('strength').value / 100,
      onlyReplace: +$('onlyReplace').value, points: pts,
    });
    applyBlendState(st);
    if (st.dirty) invalidate(st.dirty);
  } catch (e) { toast(e.message, true); }
  setTimeout(() => { S.previewDots = []; redraw(); }, 400);
}

// ---------------------------------------------------------------- rendering

function draw() {
  S.dirty = false;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.fillStyle = '#202020'; ctx.fillRect(0, 0, canvas.width, canvas.height);
  if (!S.info) return;
  const v = S.view;
  ctx.imageSmoothingEnabled = false;
  if (S.mode === 'tiles') drawTiles(); else drawBlend();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  drawCursor();
}

function drawTiles() {
  const v = S.view;
  ctx.setTransform(v.s, 0, 0, v.s, -v.ox * v.s, -v.oy * v.s);
  if (S.tileCanvas) ctx.drawImage(S.tileCanvas, 0, 0);
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  const size = Math.max(2, 2 * v.s);
  const vis = (c, r) => { const [x, y] = toScreen(2 * c, hexTop(c, r)); return x > -size && y > -size && x < canvas.width && y < canvas.height ? [x, y] : null; };
  // unsaved hexes: white tint
  ctx.fillStyle = 'rgba(255,255,255,0.22)';
  for (const [c, r] of S.pending) { const p = vis(c, r); if (p) ctx.fillRect(p[0], p[1], size, size); }
  if (S.strokeHexes && S.strokeKind === 'erase') {
    ctx.fillStyle = 'rgba(0,255,255,0.5)';
    for (const [c, r] of S.strokeHexes.values()) { const p = vis(c, r); if (p) ctx.fillRect(p[0], p[1], size, size); }
  }
  // issues: red (blocking) / orange rings
  const allow = $('allowWarnings').checked;
  ctx.lineWidth = Math.max(2, size / 6);
  for (const f of S.issues) {
    ctx.strokeStyle = (f.severity === 'error' || (!allow && f.blocking)) ? '#ff3020' : '#ffa000';
    for (const [c, r] of f.hexes) { const p = vis(c, r); if (p) ctx.strokeRect(p[0] - 1, p[1] - 1, size + 2, size + 2); }
  }
  for (const [c, r] of S.simHoles) {
    const p = vis(c, r); if (!p) continue;
    ctx.strokeStyle = S.simEdited.has(c + ',' + r) ? '#ff3020' : '#ffa000';
    ctx.beginPath(); ctx.arc(p[0] + size / 2, p[1] + size / 2, Math.max(6, size), 0, 7); ctx.stroke();
  }
  if (S.linePts.length) {
    ctx.strokeStyle = '#0ff'; ctx.lineWidth = 2 * S.dpr; ctx.setLineDash([6 * S.dpr, 4 * S.dpr]); ctx.beginPath();
    S.linePts.forEach(([c, r], i) => { const [x, y] = toScreen(2 * c + 1, hexTop(c, r) + 1); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
    ctx.stroke(); ctx.setLineDash([]);
    ctx.fillStyle = '#0ff';
    for (const [c, r] of S.linePts) { const [x, y] = toScreen(2 * c + 1, hexTop(c, r) + 1); ctx.beginPath(); ctx.arc(x, y, 4 * S.dpr, 0, 7); ctx.fill(); }
  }
}

function drawBlend() {
  const v = S.view, L = level(), span = 256 << L, worldSpan = span / S.K;
  const [wx0, wy0] = [v.ox, v.oy], wx1 = v.ox + canvas.width / v.s, wy1 = v.oy + canvas.height / v.s;
  const tx0 = Math.max(0, Math.floor(wx0 / worldSpan)), ty0 = Math.max(0, Math.floor(wy0 / worldSpan));
  const tx1 = Math.min(Math.ceil(S.info.blendSize[0] / span) - 1, Math.floor(wx1 / worldSpan));
  const ty1 = Math.min(Math.ceil(S.info.blendSize[1] / span) - 1, Math.floor(wy1 / worldSpan));
  const cx = (tx0 + tx1) / 2, cy = (ty0 + ty1) / 2;
  ctx.imageSmoothingEnabled = L > 0;
  for (let ty = ty0; ty <= ty1; ty++)
    for (let tx = tx0; tx <= tx1; tx++) {
      const t = requestTile(`${L}/${tx}/${ty}`, L, tx, ty, Math.abs(tx - cx) + Math.abs(ty - cy));
      const [sx, sy] = toScreen(tx * worldSpan, ty * worldSpan), sw = worldSpan * v.s;
      if (t.canvas) { ctx.drawImage(t.canvas, sx, sy, sw + 0.5, sw + 0.5); continue; }
      for (let d = 1; d <= 5; d++) {        // a coarser tile as placeholder
        const f = 1 << d, p = S.tiles.get(`${L + d}/${tx >> d}/${ty >> d}`);
        if (p && p.canvas) {
          const sub = 256 / f;
          ctx.drawImage(p.canvas, (tx % f) * sub, (ty % f) * sub, sub, sub, sx, sy, sw + 0.5, sw + 0.5);
          break;
        }
      }
    }
  // live stroke preview
  if (S.previewDots.length) {
    const t = S.info.textures[S.sel.blend], [r, g, b] = hexToRgb($('natural').checked ? t.natural : t.rgb);
    ctx.fillStyle = `rgba(${r},${g},${b},0.75)`;
    const R = blendRadius() / S.K * v.s;
    for (const [x, y, p] of S.previewDots) {
      const [sx, sy] = toScreen(x, y);
      ctx.beginPath(); ctx.arc(sx, sy, Math.max(1, R * (0.35 + 0.65 * p)), 0, 7); ctx.fill();
    }
  }
}

function drawCursor() {
  if (!S.hover || S.panning) return;
  const [x, y] = toScreen(S.hover[0], S.hover[1]);
  let R = 0;
  if (S.mode === 'tiles' && (S.tool.tiles === 'paint' || S.tool.tiles === 'erase')) R = (+$('size').value + 0.5) * 2 * S.view.s;
  else if (S.mode === 'blend' && S.tool.blend === 'paint') R = blendRadius() / S.K * S.view.s;
  if (R <= 0) return;
  ctx.lineWidth = 2.5 * S.dpr; ctx.strokeStyle = '#000'; ctx.beginPath(); ctx.arc(x, y, Math.max(3, R), 0, 7); ctx.stroke();
  ctx.lineWidth = 1 * S.dpr; ctx.strokeStyle = '#fff'; ctx.beginPath(); ctx.arc(x, y, Math.max(3, R), 0, 7); ctx.stroke();
}

function loop() { if (S.dirty) draw(); requestAnimationFrame(loop); }

// ---------------------------------------------------------------- input

const pointers = new Map();   // touch pointers: id -> {x, y}
let pinch = null, penDown = null, panStart = null, tapStart = null;

function pinchState() {
  const [a, b] = [...pointers.values()];
  return { cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2, d: Math.hypot(a.x - b.x, a.y - b.y) };
}

canvas.addEventListener('pointerdown', e => {
  canvas.setPointerCapture(e.pointerId);
  const drawing = e.pointerType === 'pen' || (e.pointerType === 'mouse' && e.button === 0);
  if (e.pointerType === 'touch') {
    if (penDown) return;                                  // palm rejection while the pencil draws
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.size === 2) { pinch = { ...pinchState(), view: { ...S.view } }; panStart = null; tapStart = { t: Date.now(), moved: false }; }
    else if (pointers.size === 1) panStart = { x: e.clientX, y: e.clientY, view: { ...S.view } };
    S.panning = true;
    return;
  }
  if (!drawing || S.tool[S.mode] === 'pan') {             // mouse right/middle, or the hand tool
    panStart = { x: e.clientX, y: e.clientY, view: { ...S.view } }; S.panning = true; return;
  }
  const [wx, wy] = toWorld(e.offsetX, e.offsetY);
  const stroke = S.mode === 'tiles' ? tileDown(wx, wy) : blendDown(wx, wy, e.pressure);
  if (stroke) penDown = e.pointerId;
});

canvas.addEventListener('pointermove', e => {
  if (e.pointerType === 'touch') {
    if (!pointers.has(e.pointerId)) return;
    pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pinch && pointers.size >= 2) {
      const p = pinchState(), f = p.d / Math.max(1, pinch.d);
      if (Math.abs(f - 1) > 0.05 || Math.hypot(p.cx - pinch.cx, p.cy - pinch.cy) > 10) tapStart && (tapStart.moved = true);
      const s = Math.min(200, Math.max(0.02, pinch.view.s * f));
      const rect = canvas.getBoundingClientRect();
      const wx = pinch.view.ox + (pinch.cx - rect.left) * S.dpr / pinch.view.s, wy = pinch.view.oy + (pinch.cy - rect.top) * S.dpr / pinch.view.s;
      S.view = { s, ox: wx - (p.cx - rect.left) * S.dpr / s, oy: wy - (p.cy - rect.top) * S.dpr / s };
      redraw();
    } else if (panStart) panBy(e);
    return;
  }
  const [wx, wy] = toWorld(e.offsetX, e.offsetY);
  S.hover = [wx, wy]; hoverStatus(wx, wy);
  if (panStart) { panBy(e); return; }
  if (penDown === e.pointerId) {
    const evs = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
    for (const ce of evs) {
      const [x, y] = toWorld(ce.offsetX, ce.offsetY);
      if (S.mode === 'tiles') tileDab(x, y); else blendMove(x, y, ce.pressure);
    }
  }
  redraw();
});

function panBy(e) {
  S.view.ox = panStart.view.ox - (e.clientX - panStart.x) * S.dpr / S.view.s;
  S.view.oy = panStart.view.oy - (e.clientY - panStart.y) * S.dpr / S.view.s;
  redraw();
}

function pointerEnd(e) {
  if (e.pointerType === 'touch') {
    pointers.delete(e.pointerId);
    if (pinch && pointers.size < 2) {
      if (tapStart && !tapStart.moved && Date.now() - tapStart.t < 300 && e.type === 'pointerup') undo();   // two-finger tap
      pinch = null; tapStart = null;
      const rest = [...pointers.values()][0];
      panStart = rest ? { x: rest.x, y: rest.y, view: { ...S.view } } : null;
    }
    if (!pointers.size) { panStart = null; S.panning = false; }
    return;
  }
  if (panStart) { panStart = null; S.panning = false; return; }
  if (penDown === e.pointerId) {
    penDown = null;
    if (S.mode === 'tiles') tileUp(); else blendUp();
  }
}
canvas.addEventListener('pointerup', pointerEnd);
canvas.addEventListener('pointercancel', pointerEnd);
canvas.addEventListener('pointerleave', e => { if (e.pointerType !== 'touch') { S.hover = null; redraw(); } });
canvas.addEventListener('contextmenu', e => e.preventDefault());
canvas.addEventListener('wheel', e => { e.preventDefault(); zoomAt(e.offsetX, e.offsetY, e.deltaY < 0 ? 1.2 : 1 / 1.2); }, { passive: false });
document.addEventListener('gesturestart', e => e.preventDefault());
document.addEventListener('dblclick', e => e.preventDefault());

function hoverStatus(wx, wy) {
  const h = hexAt(wx, wy);
  if (S.mode === 'tiles') {
    if (!h) return;
    const set = S.colourSets[colourAt(h[0], h[1])] || '#' + colourAt(h[0], h[1]) + ' (no tile set)';
    const iss = S.issues.find(f => f.hexes.some(x => x[0] === h[0] && x[1] === h[1]));
    $('status').textContent = `hex [${h[0]},${h[1]}] ${set}` + (iss ? `   |   ${iss.code}: ${iss.message}` : '');
  } else {
    const v = blendValueAt(wx, wy);
    $('status').textContent = `blend px ${Math.floor(wx * S.K)}, ${Math.floor(wy * S.K)}` + (h ? `   hex [${h[0]},${h[1]}]` : '') +
      (v != null && v < 255 ? `   ${v} ${S.info.textures[v].name}` : '');
  }
}

document.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' && e.target.type !== 'range' && e.target.type !== 'checkbox') return;
  const mod = e.ctrlKey || e.metaKey;
  if (mod && e.key.toLowerCase() === 'z') { e.preventDefault(); e.shiftKey ? redo() : undo(); }
  else if (mod && e.key.toLowerCase() === 'y') { e.preventDefault(); redo(); }
  else if (mod && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
  else if (e.key === 'Enter' && S.tool.tiles === 'line') lineDone();
  else if (e.key === 'Escape') { S.linePts = []; redraw(); }
  else if (e.key === '[') { $('size').value = +$('size').value - 1; sizeChanged(); }
  else if (e.key === ']') { $('size').value = +$('size').value + 1; sizeChanged(); }
});

// ---------------------------------------------------------------- UI

function setMode(mode) {
  S.mode = mode;
  for (const b of document.querySelectorAll('#modes button')) b.classList.toggle('on', b.dataset.mode === mode);
  $('blendOpts').classList.toggle('hidden', mode !== 'blend');
  $('tileOpts').classList.toggle('hidden', mode !== 'tiles');
  const size = $('size');
  if (mode === 'tiles') { size.min = 0; size.max = 12; size.value = S.tileSize ?? 1; }
  else { size.min = 1; size.max = 100; size.value = S.blendSize ?? 25; }
  renderTools(); renderPalette(); renderIssues(); sizeChanged(); updateButtons(); refreshHistory(); redraw();
}
function renderTools() {
  const box = $('tools'); box.innerHTML = '';
  for (const [id, label] of TOOLS[S.mode]) {
    const b = document.createElement('button'); b.textContent = label; b.classList.toggle('on', S.tool[S.mode] === id);
    b.onclick = () => { S.tool[S.mode] = id; S.linePts = []; renderTools(); redraw(); };
    box.appendChild(b);
  }
  $('lineDone').classList.toggle('hidden', !(S.mode === 'tiles' && S.tool.tiles === 'line'));
}
function sizeChanged() {
  if (S.mode === 'tiles') { S.tileSize = +$('size').value; $('sizeLabel').textContent = `Brush ${S.tileSize} hex`; }
  else { S.blendSize = +$('size').value; $('sizeLabel').textContent = `Brush ${blendRadius()} px`; }
  redraw();
}
function renderPalette() {
  const box = $('palette'), q = $('filter').value.toLowerCase(); box.innerHTML = '';
  const items = S.mode === 'tiles'
    ? [...S.info.sets].sort((a, b) => (a.kind !== b.kind ? (a.kind === 'area' ? -1 : 1) : b.count - a.count))
        .map(s => ({ key: s.name, name: s.name, count: s.count, bg: '#' + s.rgb }))
    : S.info.textures.map(t => ({ key: t.index, name: `${t.index} ${t.name}`, count: t.count, bg: '#' + t.rgb, img: `/api/blend/thumb/${t.index}` }));
  for (const it of items) {
    if (q && !it.name.toLowerCase().includes(q)) continue;
    const row = document.createElement('div');
    row.className = 'pal' + (S.sel[S.mode] === it.key ? ' sel' : '') + (it.count ? '' : ' unused');
    const sw = document.createElement('div'); sw.className = 'sw'; sw.style.backgroundColor = it.bg;
    if (it.img) sw.style.backgroundImage = `url(${it.img})`;
    const n = document.createElement('div'); n.className = 'n'; n.textContent = it.name;
    const c = document.createElement('div'); c.className = 'c'; c.textContent = it.count ? it.count.toLocaleString() : '';
    row.append(sw, n, c);
    row.onclick = () => { S.sel[S.mode] = it.key; renderPalette(); };
    box.appendChild(row);
  }
}
function renderIssues() {
  const box = $('issues'); box.innerHTML = '';
  if (S.mode !== 'tiles') { $('summary').textContent = `${S.blendState.unsaved} unsaved texture strokes.`; return; }
  const allow = $('allowWarnings').checked;
  const blocking = S.issues.filter(f => f.severity === 'error' || (!allow && f.blocking)).length;
  $('summary').textContent = S.pendingCount ? `${S.pendingCount} hexes changed in ${S.tileStrokes} strokes; ${S.issues.length} new issues (${blocking} blocking).`
    : 'No unsaved tile edits.';
  if (S.simHoles.length) {
    const it = document.createElement('div'); it.className = 'it ' + (S.simEdited.size ? 'block' : 'warn');
    it.textContent = `BOB simulation: ${S.simHoles.length} hexes get no tile; ${S.simEdited.size} in your edits (red).`;
    const first = S.simEdited.size ? [...S.simEdited][0].split(',').map(Number) : S.simHoles[0];
    it.onclick = () => centreOn(first[0], first[1]);
    box.appendChild(it);
  }
  for (const f of S.issues) {
    const it = document.createElement('div');
    const block = f.severity === 'error' || (!allow && f.blocking);
    it.className = 'it ' + (block ? 'block' : 'warn');
    it.textContent = `${block ? 'BLOCKING' : f.severity} ${f.code} (${f.count}): ${f.message}`;
    if (f.hexes.length) it.onclick = () => centreOn(f.hexes[0][0], f.hexes[0][1]);
    box.appendChild(it);
  }
}
function updateButtons() {
  const save = $('save');
  if (S.mode === 'tiles') {
    $('undo').disabled = !S.tileStrokes; $('redo').disabled = true;
    save.textContent = S.pendingCount ? `Save (${S.pendingCount})` : 'Save';
    save.classList.toggle('dirty', !!S.pendingCount);
  } else {
    $('undo').disabled = !S.blendState.canUndo; $('redo').disabled = !S.blendState.canRedo;
    save.textContent = S.blendState.unsaved ? `Save (${S.blendState.unsaved})` : 'Save';
    save.classList.toggle('dirty', !!S.blendState.unsaved);
  }
}
async function refreshHistory() {
  try {
    const h = await api(S.mode === 'tiles' ? '/api/tiles/history' : '/api/blend/history');
    const box = $('history'); box.innerHTML = '';
    if (!h.length) box.textContent = 'none yet';
    for (const e of h.slice(0, 20)) {
      const it = document.createElement('div'); it.className = 'it';
      it.textContent = `#${e.seq}  ${new Date(e.time).toLocaleString()}  ${e.label}`;
      box.appendChild(it);
    }
  } catch { }
}

async function undo() {
  if (S.mode === 'tiles') applyTileState(await api('/api/tiles/undo', null));
  else { applyBlendState(await api('/api/blend/undo', null)); invalidate(null); }
}
async function redo() {
  if (S.mode !== 'blend') return;
  applyBlendState(await api('/api/blend/redo', null)); invalidate(null);
}
async function save() {
  if (S.busy) return;
  S.busy = true;
  try {
    if (S.mode === 'tiles') {
      if (!S.pendingCount) { toast('Nothing to save'); return; }
      let r = await api(`/api/tiles/save?allowWarnings=${$('allowWarnings').checked}`, null);
      if (!r.written && confirm(`Not saved: ${r.blocking} blocking issue(s).\n\nSave anyway?`))
        r = await api(`/api/tiles/save?allowWarnings=true&force=true`, null);
      applyTileState(r.state);
      if (r.written) toast(`Saved tile map as edit #${r.seq} (${r.hexes} hexes)`);
      else { S.issues = r.issues; renderIssues(); redraw(); }
    } else {
      if (!S.blendState.unsaved) { toast('Nothing to save'); return; }
      try {
        const r = await api('/api/blend/save', null); applyBlendState(r.state); toast(`Saved textures as edit #${r.seq}`);
      } catch (e) {
        if (e.status === 409 && confirm(e.message + '\n\nOverwrite anyway?')) {
          const r = await api('/api/blend/save?force=true', null); applyBlendState(r.state); toast(`Saved textures as edit #${r.seq}`);
        } else if (e.status !== 409) throw e;
      }
    }
    refreshHistory(); renderIssues();
  } catch (e) { toast(e.message, true); }
  finally { S.busy = false; }
}
async function simulate() {
  const btn = $('simulate'); btn.disabled = true;
  const t0 = Date.now();
  try {
    await api('/api/tiles/simulate', null);
    for (; ;) {
      await new Promise(r => setTimeout(r, 3000));
      const st = await api('/api/tiles/simulate');
      btn.textContent = `Simulating… ${Math.round((Date.now() - t0) / 1000)} s`;
      if (st.running) continue;
      if (st.error) throw new Error(st.error);
      S.simHoles = st.result.noTile; S.simEdited = new Set(st.result.inEdited.map(h => h[0] + ',' + h[1]));
      toast(`Simulation: ${S.simHoles.length} hexes get no tile, ${S.simEdited.size} in your edits`);
      break;
    }
  } catch (e) { toast(e.message, true); }
  btn.disabled = false; btn.textContent = 'Simulate BOB Tilemap (1-3 min)';
  renderIssues(); redraw();
}

for (const b of document.querySelectorAll('#modes button')) b.onclick = () => setMode(b.dataset.mode);
$('size').oninput = sizeChanged;
$('filter').oninput = renderPalette;
$('natural').onchange = buildLut;
$('allowWarnings').onchange = () => { renderIssues(); redraw(); };
$('undo').onclick = undo; $('redo').onclick = redo; $('save').onclick = save;
$('simulate').onclick = simulate; $('lineDone').onclick = lineDone;
$('menuBtn').onclick = () => { $('panel').classList.toggle('closed'); setTimeout(resize, 0); };
window.addEventListener('resize', resize);
window.addEventListener('beforeunload', e => { if (S.pendingCount || S.blendState.unsaved) { e.preventDefault(); e.returnValue = ''; } });

load().catch(e => { $('status').textContent = 'Failed to load: ' + e.message; toast(e.message, true); });
requestAnimationFrame(loop);
