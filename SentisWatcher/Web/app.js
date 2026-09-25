// SentisWatcher web view: tracks of players and grids in 3D, a time slider, their events and inventories.
// Everything comes from the plugin's read-only API; ids are strings (they do not fit a JS number).
import * as THREE from 'three';
import { FreeLookControls } from './freelook.js';

const $ = (s) => document.querySelector(s);
const HOUR = 3600_000;
const PALETTE = ['#5fb3f9', '#f5b041', '#58d68d', '#ec7063', '#af7ac5', '#48c9b0', '#f4d03f', '#eb984e', '#85c1e9', '#f1948a'];
const KIND_COLORS = {
  damage: '#ec7063', grind: '#eb984e', destroyed: '#c0392b', death: '#ff4dff', transfer: '#58d68d', drop: '#27ae60',
  weld: '#5dade2', block_built: '#85c1e9', block_removed: '#a9cce3', paste: '#f4d03f', balance: '#f5b041',
  chat: '#bdc3c7', join: '#aab7b8', leave: '#7f8c8d', grid_added: '#48c9b0', grid_removed: '#16a085',
  grid_owner: '#af7ac5', grid_ownership: '#bb8fce', faction: '#d7bde2',
  jump: '#c39bd3', jump_passenger: '#a569bd', control: '#76d7c4', control_leave: '#45b39d', spawn: '#f7dc6f',
  drill: '#b9770e',
};

// a jump: where it went, from the event's detail ("to=x;y;z ...")
function jumpTarget(e) {
  const m = /to=(-?\d+(?:\.\d+)?);(-?\d+(?:\.\d+)?);(-?\d+(?:\.\d+)?)/.exec(e.detail || '');
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

// two positions of a track that no ship flies between: a jump (or a teleport)
const TELEPORT_M = 2000, TELEPORT_MPS = 1000;
const isTeleport = (a, b) => {
  const d = Math.hypot(b[1] - a[1], b[2] - a[2], b[3] - a[3]);
  return d > TELEPORT_M && d / Math.max(0.001, (b[0] - a[0]) / 1000) > TELEPORT_MPS;
};

// a dashed line from a jump's start to where it arrived, and a ring there
function jumpLine(e, color) {
  const to = jumpTarget(e);
  if (!to || e.x === null || e.x === undefined) return [];
  const a = world(e.x, e.y, e.z), b = world(to[0], to[1], to[2]);
  const g = new THREE.BufferGeometry().setFromPoints([a, b]);
  const length = a.distanceTo(b);
  const line = new THREE.Line(g, new THREE.LineDashedMaterial({ color, dashSize: length / 60, gapSize: length / 90, transparent: true, opacity: 0.9 }));
  line.computeLineDistances();
  const ring = sprite(color, 0.02, true);
  ring.position.copy(b);
  ring.userData = { jumpEnd: e };
  return [line, ring];
}
const kindColor = (k) => KIND_COLORS[k] || '#aaaaaa';
const KIND_NAMES = {
  damage: 'урон', grind: 'срезка', destroyed: 'уничтожение', death: 'смерть', transfer: 'перенос', drop: 'выброс',
  weld: 'сварка', block_built: 'постройка', block_removed: 'блоки убраны', paste: 'вставка', balance: 'деньги',
  chat: 'чат', join: 'вход', leave: 'выход', grid_added: 'грид появился', grid_removed: 'грид исчез',
  grid_owner: 'смена владельца', grid_ownership: 'передача грида', faction: 'фракция', jump: 'прыжок',
  jump_passenger: 'прыжок пассажиром', control: 'сел за управление', control_leave: 'вышел из управления', spawn: 'новый персонаж',
  drill: 'бурение',
};

// what the map shows: objects (markers, tracks, labels), events (their dots and jumps), or both
let layer = 'all';
const showObjects = () => layer !== 'events';
const showEventLayer = () => layer !== 'objects';

// ------------------------------------------------------------------ state

const state = {
  from: 0, to: 0, t: 0,
  playing: false, speed: 60,
  tracks: [],          // { key, kind, id, name, color, points, events, names, line, marker, box, label, eventSprites, eventDots }
  selected: null,      // key
  origin: null,        // world position subtracted from everything (keeps float32 precise)
  moment: null,        // everyone at the slider's time: { at, players, grids, gridsTotal }
};

// ------------------------------------------------------------------ helpers

function status(text, error) {
  const s = $('#status');
  s.textContent = text || '';
  s.classList.toggle('error', !!error);
}
window.addEventListener('error', (e) => status('Ошибка страницы: ' + e.message, true));
window.addEventListener('unhandledrejection', (e) => status('Ошибка: ' + (e.reason && e.reason.message || e.reason), true));

async function api(call, params = {}) {
  const url = new URL('/api/' + call, location.origin);
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null) url.searchParams.set(k, v);
  const r = await fetch(url);
  const body = await r.json();
  if (!r.ok) throw new Error(body.error || r.statusText);
  return body;
}

const pad = (n) => String(n).padStart(2, '0');
// times are shown on the server's clock: its offset comes with /api/now
let zoneOffset = 0;
function fmt(t, withDate = true) {
  const d = new Date(t + zoneOffset);
  const time = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
  return withDate ? `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${time}` : time;
}
const toInput = (t) => fmt(t).replace(' ', 'T');
const fromInput = (v) => Date.parse(v + 'Z') - zoneOffset;
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = (v, d = 2) => (v === null || v === undefined) ? '' : Number(v).toLocaleString('ru-RU', { maximumFractionDigits: d });
const debounce = (f, ms) => { let h; return (...a) => { clearTimeout(h); h = setTimeout(() => f(...a), ms); }; };

function world(x, y, z) {
  if (!state.origin) state.origin = { x, y, z };
  return new THREE.Vector3(x - state.origin.x, y - state.origin.y, z - state.origin.z);
}

// ------------------------------------------------------------------ 3D scene

const view = $('#view');
// a logarithmic depth buffer: a metre from the camera and a planet a million km off both keep their order
// (the surface, the detail patch over it and a base standing on it do not flicker into each other)
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true, logarithmicDepthBuffer: true });
renderer.setPixelRatio(window.devicePixelRatio);
view.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x03050a);
const camera = new THREE.PerspectiveCamera(55, 1, 1, 2e9);
camera.position.set(300, 250, 300);
// the game's first-person view: the left button held turns the view, Q and E roll it (see freelook.js)
const controls = new FreeLookControls(camera, renderer.domElement);
camera.lookAt(0, 0, 0);
scene.add(new THREE.AmbientLight(0xffffff, 0.3));
// a light from the camera, softer than the sun: the ground keeps its shape on the night side too (slopes
// facing away come out darker), the way a map is read rather than how the game lights it
const headlight = new THREE.DirectionalLight(0xffffff, 0.9);
scene.add(headlight, headlight.target);
// the sun: its direction comes with the world (/api/world)
const sun = new THREE.DirectionalLight(0xfff4e0, 2.2);
sun.position.set(0.4, 0.8, 0.45);
scene.add(sun, sun.target);

// ------------------------------------------------------------------ sky: stars and nebulae, drawn around the camera

const sky = new THREE.Group();
sky.renderOrder = -10;
scene.add(sky);
(function buildSky() {
  let seed = 1337;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  const R = 8e8;
  const band = new THREE.Vector3(0.3, 1, 0.2).normalize();      // the axis of the milky way
  function stars(count, size, inBand) {
    const pos = new Float32Array(count * 3), col = new Float32Array(count * 3);
    const v = new THREE.Vector3();
    for (let i = 0; i < count; i++) {
      const z = rnd() * 2 - 1, a = rnd() * Math.PI * 2, r = Math.sqrt(1 - z * z);
      v.set(r * Math.cos(a), z, r * Math.sin(a));
      if (inBand) v.addScaledVector(band, -v.dot(band) * 0.85).normalize();
      v.multiplyScalar(R);
      pos.set([v.x, v.y, v.z], i * 3);
      const b = 0.45 + rnd() * 0.55, tint = rnd();
      col.set(tint < 0.15 ? [b * 0.8, b * 0.85, b] : tint > 0.9 ? [b, b * 0.9, b * 0.7] : [b, b, b], i * 3);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('color', new THREE.BufferAttribute(col, 3));
    const p = new THREE.Points(g, new THREE.PointsMaterial({ size, sizeAttenuation: false, vertexColors: true, depthWrite: false, transparent: true }));
    p.frustumCulled = false;
    sky.add(p);
  }
  stars(5000, 1.3, false);
  stars(6000, 1.1, true);
  stars(250, 2.4, false);
  // nebulae: soft coloured clouds on a sphere behind the stars
  const c = document.createElement('canvas');
  c.width = 2048; c.height = 1024;
  const g = c.getContext('2d');
  g.fillStyle = '#03050a';
  g.fillRect(0, 0, c.width, c.height);
  const band2d = g.createLinearGradient(0, 380, 0, 640);
  band2d.addColorStop(0, 'rgba(60,70,110,0)');
  band2d.addColorStop(0.5, 'rgba(70,80,130,0.22)');
  band2d.addColorStop(1, 'rgba(60,70,110,0)');
  g.fillStyle = band2d;
  g.fillRect(0, 380, c.width, 260);
  const hues = [210, 230, 265, 290, 320, 185];
  for (let i = 0; i < 26; i++) {
    const x = rnd() * c.width, y = 200 + rnd() * 620, r = 80 + rnd() * 320;
    const hue = hues[Math.floor(rnd() * hues.length)];
    const alpha = 0.1 + rnd() * 0.12;
    // drawn a width to each side as well: a cloud cut at the edge would show as a straight line on the sky
    for (const shift of [-c.width, 0, c.width]) {
      const cx = x + shift;
      if (cx + r < 0 || cx - r > c.width) continue;
      const grad = g.createRadialGradient(cx, y, 0, cx, y, r);
      grad.addColorStop(0, `hsla(${hue},70%,55%,${alpha})`);
      grad.addColorStop(1, `hsla(${hue},70%,40%,0)`);
      g.fillStyle = grad;
      g.fillRect(cx - r, y - r, r * 2, r * 2);
    }
  }
  // as the scene's background (drawn behind everything, no geometry to clip)
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.mapping = THREE.EquirectangularReflectionMapping;
  scene.background = t;
})();

// the sun on the sky, where the server says it is: a glow and a bright disc
const sunOnSky = new THREE.Group();
(function buildSun() {
  const glow = (size, stops) => {
    const c = document.createElement('canvas');
    c.width = c.height = 256;
    const g = c.getContext('2d');
    const grad = g.createRadialGradient(128, 128, 0, 128, 128, 128);
    for (const [at, color] of stops) grad.addColorStop(at, color);
    g.fillStyle = grad;
    g.fillRect(0, 0, 256, 256);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
      map: t, sizeAttenuation: false, depthWrite: false, transparent: true, blending: THREE.AdditiveBlending,
    }));
    sprite.scale.set(size, size, 1);
    sprite.frustumCulled = false;
    return sprite;
  };
  sunOnSky.add(glow(0.55, [[0, 'rgba(255,220,160,0.55)'], [0.25, 'rgba(255,190,120,0.18)'], [1, 'rgba(255,170,90,0)']]));
  sunOnSky.add(glow(0.09, [[0, 'rgba(255,255,250,1)'], [0.45, 'rgba(255,245,220,0.95)'], [1, 'rgba(255,230,180,0)']]));
  sunOnSky.position.set(0.4, 0.8, 0.45).normalize().multiplyScalar(7e8);
  sunOnSky.renderOrder = -9;
  sky.add(sunOnSky);
})();

const labels = document.createElement('div');
view.appendChild(labels);

function resize() {
  const w = view.clientWidth, h = view.clientHeight;
  renderer.setSize(w, h);
  camera.aspect = w / Math.max(1, h);
  camera.updateProjectionMatrix();
  drawTimeline();
}
window.addEventListener('resize', resize);

function dotTexture(color, ring) {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d');
  g.beginPath();
  g.arc(32, 32, ring ? 26 : 28, 0, Math.PI * 2);
  if (ring) { g.lineWidth = 8; g.strokeStyle = color; g.stroke(); }
  else { g.fillStyle = color; g.fill(); g.lineWidth = 4; g.strokeStyle = '#000'; g.stroke(); }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

const dotMap = dotTexture('#ffffff', false);

function sprite(color, size, ring = false) {
  const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: dotTexture(color, ring), sizeAttenuation: false, depthTest: false, transparent: true }));
  s.scale.set(size, size, 1);
  s.renderOrder = 2;
  return s;
}

// ------------------------------------------------------------------ tracks

const trackKey = (kind, id) => kind + ':' + id;
const findTrack = (key) => state.tracks.find((t) => t.key === key);
const selectedTrack = () => findTrack(state.selected);

async function addTrack(kind, id, name) {
  const key = trackKey(kind, id);
  if (findTrack(key)) { select(key); return; }
  status('Загрузка ' + (name || id) + '…');
  const color = PALETTE[state.tracks.length % PALETTE.length];
  const track = { key, kind, id, name: name || id, color, points: [], events: [], names: {} };
  state.tracks.push(track);
  await loadTrack(track);
  if (!state.selected) select(key);
  renderTracked();
  saveHash();
  status('');
}

async function loadTrack(track) {
  const [data, ev] = await Promise.all([
    api('track', { kind: track.kind, id: track.id, from: state.from, to: state.to }),
    api('events', { id: track.id, from: state.from, to: state.to }),
  ]);
  track.name = data.name || track.name;
  track.points = data.points;
  track.total = data.total;
  track.events = ev.events;
  Object.assign(track.names, ev.names);
  build3d(track);
  renderEvents();
  drawTimeline();
}

function disposeTrack(track) {
  for (const o of [track.line, track.marker, track.box, track.eventDots, ...(track.eventSprites || [])]) {
    if (!o) continue;
    scene.remove(o);
    o.geometry?.dispose?.();
    o.material?.map?.dispose?.();
    o.material?.dispose?.();
  }
  track.label?.remove();
}

function build3d(track) {
  disposeTrack(track);
  const p = track.points;
  if (p.length > 1) {
    // segments between consecutive points, except across a jump
    const segments = [];
    for (let i = 0; i + 1 < p.length; i++) {
      if (isTeleport(p[i], p[i + 1])) continue;
      const a = world(p[i][1], p[i][2], p[i][3]), b = world(p[i + 1][1], p[i + 1][2], p[i + 1][3]);
      segments.push(a.x, a.y, a.z, b.x, b.y, b.z);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(segments), 3));
    track.line = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: track.color, transparent: true, opacity: 0.55 }));
    track.line.visible = showObjects();
    scene.add(track.line);
  }
  track.marker = sprite(track.color, track.kind === 'grid' ? 0.03 : 0.022, track.kind === 'grid');
  track.marker.userData = { track };
  scene.add(track.marker);
  if (track.kind === 'grid') {
    track.box = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial({ color: track.color, wireframe: true, transparent: true, opacity: 0.6 }));
    track.box.matrixAutoUpdate = false;
    scene.add(track.box);
  }
  track.label = document.createElement('div');
  track.label.className = 'label3d';
  track.label.textContent = track.name;
  labels.appendChild(track.label);
  // the events: one set of points per track, not an object each (a busy grid has thousands); the jumps
  // add their lines
  track.eventSprites = [];
  track.located = track.events.filter((e) => e.x !== null && e.x !== undefined).sort((a, b) => a.t - b.t);
  track.locatedTimes = track.located.map((e) => [e.t]);
  for (const e of track.located) {
    if (e.kind !== 'jump' && e.kind !== 'jump_passenger') continue;
    for (const o of jumpLine(e, kindColor(e.kind))) {
      o.userData = { ...o.userData, event: e, track };
      o.visible = showEventLayer();
      scene.add(o);
      track.eventSprites.push(o);
    }
  }
  track.eventDots = null;
  if (track.located.length) {
    const n = track.located.length;
    const pos = new Float32Array(n * 3), col = new Float32Array(n * 3), base = new Float32Array(n * 3);
    const c = new THREE.Color();
    track.located.forEach((e, i) => {
      const p = world(e.x, e.y, e.z);
      pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;
      c.set(kindColor(e.kind));
      base[i * 3] = c.r; base[i * 3 + 1] = c.g; base[i * 3 + 2] = c.b;
    });
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('color', new THREE.BufferAttribute(col, 3));
    const dots = new THREE.Points(g, new THREE.PointsMaterial({
      size: 7, sizeAttenuation: false, vertexColors: true, map: dotMap, alphaTest: 0.5, depthTest: false, transparent: true,
    }));
    dots.renderOrder = 2;
    dots.frustumCulled = false;
    dots.visible = showEventLayer();
    dots.userData = { base, painted: -2 };
    scene.add(dots);
    track.eventDots = dots;
    paintEvents(track, state.t);
  }
}

// the events up to t bright, the later ones dim: only the colours between the old and the new moment change
function paintEvents(track, t) {
  const dots = track.eventDots;
  if (!dots) return;
  const upTo = indexAt(track.locatedTimes, t);
  const was = dots.userData.painted;
  if (upTo === was) return;
  const col = dots.geometry.attributes.color.array, base = dots.userData.base;
  const [a, b] = was === -2 ? [0, track.located.length] : [Math.min(was, upTo) + 1, Math.max(was, upTo) + 1];
  for (let i = a; i < b; i++) {
    const k = i <= upTo ? 1 : 0.3;
    col[i * 3] = base[i * 3] * k; col[i * 3 + 1] = base[i * 3 + 1] * k; col[i * 3 + 2] = base[i * 3 + 2] * k;
  }
  dots.geometry.attributes.color.needsUpdate = true;
  dots.userData.painted = upTo;
}

function removeTrack(key) {
  const track = findTrack(key);
  if (!track) return;
  disposeTrack(track);
  state.tracks = state.tracks.filter((t) => t !== track);
  if (state.selected === key) state.selected = state.tracks[0]?.key || null;
  renderTracked(); renderEvents(); refreshInventory(); drawTimeline(); saveHash();
  buildAmbient(); renderMoment();
}

function select(key, focus = true) {
  state.selected = key;
  renderTracked();
  renderEvents();
  refreshInventory();
  if (focus) focusSelected();
  saveHash();
}

// the index of the last point at or before t (-1 when t is before the first)
function indexAt(points, t) {
  let lo = 0, hi = points.length - 1, found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (points[mid][0] <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
}

// Positions are written on change: between two close samples the object moved (interpolated), between two
// far ones it stood where the first one says.
function stateAt(track, t) {
  const p = track.points;
  if (!p.length) return null;
  const i = indexAt(p, t);
  if (i < 0) return { pos: world(p[0][1], p[0][2], p[0][3]), point: p[0], before: true };
  const a = p[i], b = p[i + 1];
  const moving = track.kind === 'grid' ? 15_000 : 3_000;
  let pos = world(a[1], a[2], a[3]);
  if (b && b[0] - a[0] <= moving && !isTeleport(a, b)) {
    const k = (t - a[0]) / Math.max(1, b[0] - a[0]);
    pos = pos.lerp(world(b[1], b[2], b[3]), k);
  }
  const heartbeat = track.kind === 'grid' ? 10 * 60_000 : 60_000;
  return { pos, point: a, stale: !b && t - a[0] > heartbeat * 1.5 };
}

function updateScene() {
  const t = state.t;
  const v = new THREE.Vector3();
  sky.position.copy(camera.position);
  buildPlanets();
  for (const l of planetLabels) {
    v.copy(l.pos).project(camera);
    const off = !planetGroup.visible || v.z > 1 || Math.abs(v.x) > 1.2 || Math.abs(v.y) > 1.2 ||
      camera.position.distanceTo(l.pos) < l.r * 1.02;     // standing on it: no label in the middle of the ground
    l.el.style.display = off ? 'none' : '';
    if (off) continue;
    l.el.style.left = ((v.x + 1) / 2 * view.clientWidth) + 'px';
    l.el.style.top = ((1 - v.y) / 2 * view.clientHeight) + 'px';
  }
  for (const track of state.tracks) {
    if (!track.marker) continue;      // still loading
    const s = stateAt(track, t);
    const visible = !!s && showObjects();
    track.marker.visible = visible;
    if (track.box) track.box.visible = visible;
    track.label.style.display = visible ? '' : 'none';
    if (!s) continue;
    track.marker.position.copy(s.pos);
    track.marker.material.opacity = s.before || s.stale ? 0.35 : 1;
    if (track.box) {
      const pt = s.point;
      const fwd = new THREE.Vector3(pt[4], pt[5], pt[6]).normalize();
      const up = new THREE.Vector3(pt[7], pt[8], pt[9]).normalize();
      const right = new THREE.Vector3().crossVectors(fwd, up).normalize();
      const size = Math.max(2, (pt[13] || 5) * 2);
      const m = new THREE.Matrix4().makeBasis(right, up, fwd.clone().negate());
      m.scale(new THREE.Vector3(size, size, size)).setPosition(s.pos);
      track.box.matrix.copy(m);
    }
    v.copy(s.pos).project(camera);
    const behind = v.z > 1;
    track.label.style.display = behind || !showObjects() ? 'none' : '';
    track.label.style.left = ((v.x + 1) / 2 * view.clientWidth) + 'px';
    track.label.style.top = ((1 - v.y) / 2 * view.clientHeight) + 'px';
    track.label.style.opacity = s.before || s.stale ? 0.5 : 1;
    setText(track.label, track.name + (s.stale ? ' (нет данных)' : '') + distanceNote(s.pos));
    paintEvents(track, t);
  }
  for (const l of ambientLabels) {
    v.copy(l.pos).project(camera);
    const off = v.z > 1 || Math.abs(v.x) > 1.1 || Math.abs(v.y) > 1.1 || !showObjects();
    l.el.style.display = off ? 'none' : '';
    if (off) continue;
    l.el.style.left = ((v.x + 1) / 2 * view.clientWidth) + 'px';
    l.el.style.top = ((1 - v.y) / 2 * view.clientHeight) + 'px';
    setText(l.el, l.name + distanceNote(l.pos));
  }
  if ($('#follow').checked && !flight) {
    const sel = selectedTrack();
    const s = sel && stateAt(sel, t);
    if (s) {
      const delta = s.pos.clone().sub(controls.target);
      controls.target.add(delta);
      camera.position.add(delta);
    }
  }
  $('#clock').textContent = fmt(state.t);
  updateHud();
}

// a label's text is set only when it changes: writing it every frame costs a layout each time
function setText(el, text) {
  if (el._text !== text) { el._text = text; el.textContent = text; }
}
// " · 1.2 км" from the camera, when the map shows distances
const distanceNote = (pos) => $('#showDistance').checked ? ' · ' + distanceText(camera.position.distanceTo(pos)) : '';

// ------------------------------------------------------------------ scale: distance to what is looked at, altitude, a scale bar

function distanceText(m) {
  if (m < 1000) return `${Math.round(m)} м`;
  if (m < 100_000) return `${Number((m / 1000).toFixed(m < 10_000 ? 2 : 1)).toLocaleString('ru-RU')} км`;
  return `${num(m / 1000, 0)} км`;
}

function updateHud() {
  const sel = selectedTrack();
  const s = sel && sel.marker && stateAt(sel, state.t);
  const target = s ? s.pos : controls.target;
  const distance = camera.position.distanceTo(target);
  $('#hudDistance').innerHTML = s
    ? `До <b>${esc(sel.name)}</b>: <b>${distanceText(distance)}</b>`
    : `До точки обзора: <b>${distanceText(distance)}</b>`;

  // the camera's height over the nearest planet's average surface
  let altitude = '';
  if (worldInfo && state.origin && worldInfo.planets.length) {
    let best = null;
    for (const p of worldInfo.planets) {
      const c = new THREE.Vector3(p.x - state.origin.x, p.y - state.origin.y, p.z - state.origin.z);
      const h = camera.position.distanceTo(c) - (p.radius || 0);
      if (!best || h < best.h) best = { h, p };
    }
    if (best) altitude = `Над ${best.p.generator || best.p.name}: ${best.h > 0 ? distanceText(best.h) : 'под поверхностью'}`;
  }
  $('#hudAltitude').textContent = altitude;

  // a bar of a round length that is 60..150 px long at the distance of what is looked at
  const perPixel = 2 * distance * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) / Math.max(1, view.clientHeight);
  const steps = [1, 2, 5];
  let length = 1;
  for (let exp = 0; exp < 10 && length / perPixel < 60; exp++)
    for (const k of steps) { length = k * 10 ** exp; if (length / perPixel >= 60) break; }
  $('#scaleBar').style.width = Math.min(200, Math.round(length / perPixel)) + 'px';
  $('#scaleText').textContent = distanceText(length);
}

function focusSelected() {
  const sel = selectedTrack();
  const s = sel && stateAt(sel, state.t);
  if (!s) return;
  const offset = camera.position.clone().sub(controls.target);
  if (offset.length() > 20000 || offset.length() < 5) offset.set(250, 200, 250);
  controls.target.copy(s.pos);
  camera.position.copy(s.pos).add(offset);
}

// ------------------------------------------------------------------ planets, to scale

const planetGroup = new THREE.Group();
scene.add(planetGroup);
const planetLabels = [];
let worldInfo = null;           // { planets, sun } of the range
let planetsBuiltFor = null;     // the origin they were built around

// Each planet's colours: [low ground, middle, high ground, peaks, rock of steep faces], its air, and whether
// its peaks and poles carry snow. Simplified, but enough to read the land: valleys, slopes, cliffs, ice.
const PLANET_STYLE = [
  [/earth/i, ['#557d3b', '#6f8a47', '#8c8467', '#b7b1a6', '#77716a'], '#8fc3ff', true],
  [/moon/i, ['#8c8c8c', '#7a7a7a', '#9d9d9d', '#b4b4b4', '#6a6a6a'], null, false],
  [/mars/i, ['#a4552f', '#b76a3d', '#c98052', '#d9a07a', '#7b3a22'], '#e7a57a', true],
  [/europa/i, ['#b9cfdd', '#cfe1ee', '#e3eef6', '#f7fbfd', '#98b2c4'], '#d8ecff', false],
  [/alien/i, ['#4c7a5b', '#6b4e94', '#8466ad', '#a08bc4', '#3f3350'], '#c3a4ff', false],
  [/titan/i, ['#9a7341', '#b08650', '#c49856', '#e2c184', '#6f5430'], '#ffe2a8', false],
  [/triton/i, ['#86a3b2', '#9bb7c6', '#b4ccd8', '#dbeff8', '#667f8c'], '#cfe8ff', true],
  [/pertam/i, ['#8b6944', '#a37d52', '#b3895a', '#d3ab7a', '#6a4f33'], '#ffd9a8', false],
];
function planetStyle(p) {
  const key = (p.generator || '') + ' ' + (p.name || '');
  return PLANET_STYLE.find(([re]) => re.test(key)) || [null, ['#6f6f77', '#7d7d85', '#8d8d95', '#a4a4ac', '#5a5a62'], '#bcc6d8', false];
}

// The colour of the ground: by height (0 the lowest of the planet, 1 the highest), turning to rock where it is
// steep (up: how level it is, 1 flat) and to snow on the peaks of a snowy planet; a little noise so that it
// is not flat colour.
const _c = [new THREE.Color(), new THREE.Color()];
function groundColor(style, f, level, noise, out) {
  const [, colors, , caps] = style;
  const bands = [0, 0.3, 0.6, 0.85, 1];
  let k = 0;
  while (k < 3 && f > bands[k + 1]) k++;
  const u = Math.min(1, Math.max(0, (f - bands[k]) / (bands[k + 1] - bands[k])));
  out.set(colors[Math.min(k, 3)]).lerp(_c[0].set(colors[Math.min(k + 1, 3)]), u);
  const steep = Math.min(1, Math.max(0, (0.93 - level) * 5));
  if (steep > 0) out.lerp(_c[1].set(colors[4]), steep);
  if (caps && f > 0.8 && level > 0.8) out.lerp(_c[1].set('#f4f7fa'), Math.min(1, (f - 0.8) * 5) * (level - 0.8) * 5);
  out.multiplyScalar(0.92 + noise * 0.16);
  return out;
}
const hash01 = (i) => { const x = Math.sin(i * 12.9898) * 43758.5453; return x - Math.floor(x); };

// Fine detail over the colours: a small grey noise tile repeated many times, so the ground reads as ground
// close up (a flat colour gives no sense of distance or motion).
let detailTexture = null;
function groundDetail() {
  if (detailTexture) return detailTexture;
  const size = 256;
  const c = document.createElement('canvas');
  c.width = c.height = size;
  const g = c.getContext('2d');
  const img = g.createImageData(size, size);
  let seed = 7;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  // value noise at a few scales, tiling
  const layers = [8, 16, 32, 64].map((cells) => {
    const grid = Array.from({ length: cells * cells }, rnd);
    return (x, y) => {
      const fx = x / size * cells, fy = y / size * cells;
      const x0 = Math.floor(fx), y0 = Math.floor(fy), tx = fx - x0, ty = fy - y0;
      const at = (i, j) => grid[((j + cells) % cells) * cells + ((i + cells) % cells)];
      const sx = tx * tx * (3 - 2 * tx), sy = ty * ty * (3 - 2 * ty);
      return (at(x0, y0) * (1 - sx) + at(x0 + 1, y0) * sx) * (1 - sy) + (at(x0, y0 + 1) * (1 - sx) + at(x0 + 1, y0 + 1) * sx) * sy;
    };
  });
  for (let y = 0; y < size; y++)
    for (let x = 0; x < size; x++) {
      const v = 0.45 * layers[0](x, y) + 0.25 * layers[1](x, y) + 0.18 * layers[2](x, y) + 0.12 * layers[3](x, y);
      const grey = Math.round(255 * (0.72 + 0.28 * v));
      const o = (y * size + x) * 4;
      img.data[o] = img.data[o + 1] = img.data[o + 2] = grey;
      img.data[o + 3] = 255;
    }
  g.putImageData(img, 0, 0);
  detailTexture = new THREE.CanvasTexture(c);
  detailTexture.wrapS = detailTexture.wrapT = THREE.RepeatWrapping;
  detailTexture.colorSpace = THREE.SRGBColorSpace;
  detailTexture.anisotropy = 8;
  return detailTexture;
}

function planetTexture(colors, caps, seedFrom) {
  let seed = Math.abs(seedFrom | 0) % 2147483646 + 1;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  const c = document.createElement('canvas');
  c.width = 1024; c.height = 512;
  const g = c.getContext('2d');
  g.fillStyle = colors[0];
  g.fillRect(0, 0, c.width, c.height);
  for (let i = 0; i < 700; i++) {
    const x = rnd() * c.width, y = rnd() * c.height, r = 4 + rnd() * 70;
    g.globalAlpha = 0.12 + rnd() * 0.25;
    g.fillStyle = colors[1 + Math.floor(rnd() * 2)];
    g.beginPath();
    g.ellipse(x, y, r * (1 + rnd()), r, 0, 0, Math.PI * 2);
    g.fill();
  }
  g.globalAlpha = 1;
  if (caps) {
    for (const [y0, y1] of [[0, 55], [c.height, c.height - 55]]) {
      const grad = g.createLinearGradient(0, y0, 0, y1);
      grad.addColorStop(0, 'rgba(245,250,255,0.95)');
      grad.addColorStop(1, 'rgba(245,250,255,0)');
      g.fillStyle = grad;
      g.fillRect(0, Math.min(y0, y1), c.width, 55);
    }
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

async function loadWorld() {
  worldInfo = await api('world', { t: state.to });
  planetsBuiltFor = null;
  const s = worldInfo.sun;
  if (s) {
    sun.position.set(s[0], s[1], s[2]);
    sunOnSky.position.set(s[0], s[1], s[2]).normalize().multiplyScalar(7e8);
  }
}

function buildPlanets() {
  if (!worldInfo || !state.origin || planetsBuiltFor === state.origin) return;
  planetsBuiltFor = state.origin;
  dropPatch();
  for (const m of [...planetGroup.children]) { m.geometry.dispose(); m.material.map?.dispose(); m.material.dispose(); }
  planetGroup.clear();
  for (const l of planetLabels) l.el.remove();
  planetLabels.length = 0;
  for (const p of worldInfo.planets) {
    const [, colors, air, caps] = planetStyle(p);
    const r = p.radius || 30000;
    const pos = world(p.x, p.y, p.z);
    const body = new THREE.Mesh(new THREE.SphereGeometry(r, 128, 64),
      new THREE.MeshStandardMaterial({ map: planetTexture(colors, caps, Number(String(p.id).slice(-6))), roughness: 1, metalness: 0 }));
    body.position.copy(pos);
    planetGroup.add(body);
    applyRelief(body, p);
    // where the air ends (the game's own boundary: average radius + atmosphere altitude)
    if (p.atmosphere > r) {
      const shell = new THREE.Mesh(new THREE.SphereGeometry(p.atmosphere, 96, 48),
        new THREE.MeshBasicMaterial({ color: air || '#bcc6d8', transparent: true, opacity: 0.12, depthWrite: false }));
      shell.position.copy(pos);
      planetGroup.add(shell);
    }
    const el = document.createElement('div');
    el.className = 'label3d planet';
    el.textContent = `${p.generator || p.name} · R ${num(r / 1000, 0)} км`;
    labels.appendChild(el);
    planetLabels.push({ el, pos, r });
  }
  planetGroup.visible = $('#showPlanets').checked;
}
// The planet with its real surface: the server reads the planet's heights for the vertices of a sphere
// (Relief.cs: the same directions, in the same order as three.js lays them out) and the sphere's vertices
// are moved to them - a base on a hillside then sits on the ground instead of over a smooth ball of the
// average radius. Kept per planet; a planet the running game does not have stays a sphere.
const reliefs = {};
async function applyRelief(body, p) {
  let relief = reliefs[p.id];
  if (relief === undefined) {
    relief = reliefs[p.id] = api('relief', { id: p.id, name: p.name }).catch(() => null);
  }
  relief = await relief;
  if (!relief || !relief.relief || body.parent !== planetGroup) return;
  const raw = atob(relief.relief);
  const heights = new Int16Array(raw.length / 2);
  for (let i = 0; i < heights.length; i++) heights[i] = (raw.charCodeAt(2 * i) | (raw.charCodeAt(2 * i + 1) << 8)) << 16 >> 16;
  const geometry = new THREE.SphereGeometry(1, relief.w, relief.h);
  const at = geometry.attributes.position;
  if (at.count !== heights.length) { geometry.dispose(); return; }
  const v = new THREE.Vector3();
  let low = Infinity, high = -Infinity;
  for (let i = 0; i < heights.length; i++) { low = Math.min(low, heights[i]); high = Math.max(high, heights[i]); }
  for (let i = 0; i < at.count; i++) {
    v.fromBufferAttribute(at, i).normalize().multiplyScalar(relief.base + heights[i]);
    at.setXYZ(i, v.x, v.y, v.z);
  }
  geometry.computeVertexNormals();
  // coloured by height and steepness, with the fine detail tile over it (about a kilometre a tile)
  const style = planetStyle(p);
  const colors = new Float32Array(at.count * 3);
  const normal = geometry.attributes.normal;
  const n = new THREE.Vector3(), c = new THREE.Color();
  for (let i = 0; i < at.count; i++) {
    v.fromBufferAttribute(at, i).normalize();
    n.fromBufferAttribute(normal, i);
    groundColor(style, (heights[i] - low) / Math.max(1, high - low), n.dot(v), hash01(i), c);
    colors[3 * i] = c.r; colors[3 * i + 1] = c.g; colors[3 * i + 2] = c.b;
  }
  geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  const detail = groundDetail().clone();
  detail.needsUpdate = true;
  detail.repeat.set(relief.w, relief.h / 2);
  body.material.map?.dispose();
  body.material.dispose();
  body.material = new THREE.MeshStandardMaterial({ vertexColors: true, map: detail, roughness: 1, metalness: 0 });
  body.geometry.dispose();
  body.geometry = geometry;
  body.userData.relief = { planet: p, base: relief.base, low, high, style };
}

// ------------------------------------------------------------------ the ground close up

// Near a planet the sphere's vertices are hundreds of metres apart: the ground around where the camera looks
// is asked of the server in detail (Relief.Patch, 129 x 129 over a square a few times the camera's height) and
// drawn over it, coloured the same way. Asked again when the view moves off it or the height changes much.
const patchGroup = new THREE.Group();
scene.add(patchGroup);
let patch = null;            // { planetId, x, y, z (world, the centre asked), size, mesh }
let patchRequest = 0, patchBusy = false, patchCheckedAt = 0;
const PATCH_ALTITUDE = 20000, PATCH_N = 129;

async function updatePatch(now) {
  if (patchBusy || now - patchCheckedAt < 500 || !state.origin) return;
  patchCheckedAt = now;
  const body = planetGroup.children.find((m) => m.userData.relief &&
    camera.position.distanceTo(m.position) - m.userData.relief.base < PATCH_ALTITUDE);
  if (!body || !planetGroup.visible) { dropPatch(); return; }
  const r = body.userData.relief;
  const altitude = Math.max(50, camera.position.distanceTo(body.position) - r.base);
  // what is looked at, down on the planet: the orbit target, or straight below the camera when that is far
  const look = controls.target.distanceTo(camera.position) < altitude * 6 ? controls.target : camera.position;
  const wx = look.x + state.origin.x, wy = look.y + state.origin.y, wz = look.z + state.origin.z;
  const size = Math.min(40000, Math.max(1500, altitude * 4));
  if (patch && patch.planetId === r.planet.id && size / patch.size < 2 && patch.size / size < 2 &&
      Math.hypot(wx - patch.x, wy - patch.y, wz - patch.z) < patch.size / 4) return;
  patchBusy = true;
  const request = ++patchRequest;
  try {
    const data = await api('terrain', { id: r.planet.id, name: r.planet.name, x: wx, y: wy, z: wz, size: Math.round(size), n: PATCH_N });
    if (request !== patchRequest || !data || !data.heights) return;
    buildPatch(body, r, data, { x: wx, y: wy, z: wz, size });
  } catch (e) {
    // no detail then; the sphere stays
  } finally {
    patchBusy = false;
  }
}

function buildPatch(body, r, data, asked) {
  const raw = atob(data.heights);
  const bytes = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  const heights = new Float32Array(bytes.buffer);
  const n = data.n, size = data.size;
  const [cx, cy, cz] = data.center, [ux, uy, uz] = data.up, [ex, ey, ez] = data.east, [nx, ny, nz] = data.north;
  // positions relative to the patch's own middle, in doubles until then: float32 stays exact near the camera
  const mid = heights[((n - 1) / 2) * n + (n - 1) / 2] + data.base;
  const ox = cx + ux * mid, oy = cy + uy * mid, oz = cz + uz * mid;
  const positions = new Float32Array(n * n * 3), uvs = new Float32Array(n * n * 2), colors = new Float32Array(n * n * 3);
  for (let j = 0, k = 0; j < n; j++)
    for (let i = 0; i < n; i++, k++) {
      const a = (i / (n - 1) - 0.5) * size, b = (j / (n - 1) - 0.5) * size;
      let dx = ux * data.base + ex * a + nx * b, dy = uy * data.base + ey * a + ny * b, dz = uz * data.base + ez * a + nz * b;
      const len = Math.hypot(dx, dy, dz);
      const radius = data.base + heights[k];
      dx = dx / len * radius; dy = dy / len * radius; dz = dz / len * radius;
      positions[3 * k] = cx + dx - ox; positions[3 * k + 1] = cy + dy - oy; positions[3 * k + 2] = cz + dz - oz;
      // a detail tile about 25 m across
      uvs[2 * k] = a / 25; uvs[2 * k + 1] = b / 25;
    }
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  geometry.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
  const index = [];
  for (let j = 0; j < n - 1; j++)
    for (let i = 0; i < n - 1; i++) {
      // counter-clockwise seen from above (east, north, up is right-handed): the faces look out of the planet
      const k = j * n + i;
      index.push(k, k + 1, k + n, k + 1, k + n + 1, k + n);
    }
  geometry.setIndex(index);
  geometry.computeVertexNormals();
  const normal = geometry.attributes.normal, v = new THREE.Vector3(), up = new THREE.Vector3(), c = new THREE.Color();
  for (let k = 0; k < n * n; k++) {
    up.set(positions[3 * k] + ox - cx, positions[3 * k + 1] + oy - cy, positions[3 * k + 2] + oz - cz).normalize();
    v.fromBufferAttribute(normal, k);
    // the same colours as the sphere's; the noise on a finer grain
    groundColor(r.style, (heights[k] - r.low) / Math.max(1, r.high - r.low), v.dot(up), hash01(k * 7.13), c);
    colors[3 * k] = c.r; colors[3 * k + 1] = c.g; colors[3 * k + 2] = c.b;
  }
  geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  // the edges fade into the sphere below instead of ending in a step
  const material = new THREE.MeshStandardMaterial({ vertexColors: true, map: groundDetail(), roughness: 1, metalness: 0,
    polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4 });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.set(ox - state.origin.x, oy - state.origin.y, oz - state.origin.z);
  dropPatch();
  patchGroup.add(mesh);
  // the coarse sphere sinks a little under the detail: between its far-apart vertices it can stand above
  // the real ground of a valley and hide it
  body.scale.setScalar((r.base - Math.min(150, asked.size / 40)) / r.base);
  patch = { planetId: r.planet.id, x: asked.x, y: asked.y, z: asked.z, size: asked.size, mesh, body };
}

function dropPatch() {
  if (!patch) return;
  patchGroup.remove(patch.mesh);
  patch.mesh.geometry.dispose();
  patch.mesh.material.dispose();
  patch.body.scale.setScalar(1);
  patch = null;
}
$('#showPlanets').addEventListener('change', () => {
  planetGroup.visible = patchGroup.visible = $('#showPlanets').checked;
});

// ------------------------------------------------------------------ the moment: everyone at the slider's time

const ambient = new THREE.Group();
scene.add(ambient);
const ambientLabels = [];       // { el, pos } for the players and the active grids
const ambientTextures = {};
let momentRequest = 0;
let lastMomentAt = 0;
let focusedOnce = false;

const ambientMaterials = {};
function ambientSprite(color, size) {
  if (!ambientTextures[color]) ambientTextures[color] = dotTexture(color, false);
  // one material for each colour, shared by every dot of it
  const material = ambientMaterials[color] || (ambientMaterials[color] =
    new THREE.SpriteMaterial({ map: ambientTextures[color], sizeAttenuation: false, depthTest: false, transparent: true, opacity: 0.85 }));
  const s = new THREE.Sprite(material);
  s.scale.set(size, size, 1);
  s.renderOrder = 1;
  s.userData.shared = true;
  return s;
}

const loadMoment = debounce(async () => {
  const request = ++momentRequest;
  const t = Math.round(state.t);
  // where everyone was: near the moment; their events: over the whole range chosen on the timeline
  const data = await api('moment', { t, window: 300_000, from: Math.round(state.from), to: Math.round(state.to) });
  if (request !== momentRequest) return;
  lastMomentAt = t;
  state.moment = data;
  buildAmbient();
  renderMoment();
}, 250);

function buildAmbient() {
  for (const s of ambient.children) {
    if (s.userData.shared) continue;
    s.geometry?.dispose?.();
    s.material.dispose();
  }
  ambient.clear();
  for (const l of ambientLabels) l.el.remove();
  ambientLabels.length = 0;
  const m = state.moment;
  if (!m || !$('#showAmbient').checked) return;
  const first = m.players[0] || m.grids[0];
  if (first && !state.origin) world(first.x, first.y, first.z);
  const add = (o, kind) => {
    if (findTrack(trackKey(kind, o.id))) return;       // drawn as a track already
    const hot = o.events > 0;
    const color = kind === 'player' ? (hot ? '#f5b041' : '#ffffff') : hot ? '#eb984e' : o.static ? '#6c7686' : '#9fb3c8';
    const s = ambientSprite(color, kind === 'player' ? 0.014 : hot ? 0.012 : 0.008);
    const pos = world(o.x, o.y, o.z);
    s.position.copy(pos);
    s.userData = { ambient: o, kind, shared: true };
    s.visible = showObjects();
    ambient.add(s);
    if ((kind === 'player' || hot) && ambientLabels.length < 60) {
      const el = document.createElement('div');
      el.className = 'label3d';
      el.style.opacity = 0.75;
      el.textContent = o.name || o.id;
      labels.appendChild(el);
      ambientLabels.push({ el, pos, name: o.name || o.id });
    }
  };
  m.players.forEach((o) => add(o, 'player'));
  m.grids.forEach((o) => add(o, 'grid'));
  for (const j of m.jumps || []) {
    if (findTrack(trackKey('grid', j.entity))) continue;      // a tracked grid draws its own
    for (const o of jumpLine(j, '#c39bd3')) {
      o.userData = o.isSprite ? { ambientJump: j, layer: 'events' } : { layer: 'events' };
      o.visible = showEventLayer();
      ambient.add(o);
    }
  }
  if (!focusedOnce && !state.tracks.length && first) {
    // nothing chosen yet: look at the most active one
    focusedOnce = true;
    focusOn(world(first.x, first.y, first.z));
  }
}

function focusOn(pos) {
  const offset = camera.position.clone().sub(controls.target);
  if (offset.length() > 20000 || offset.length() < 5) offset.set(300, 250, 300);
  controls.target.copy(pos);
  camera.position.copy(pos).add(offset);
  $('#follow').checked = false;
}

function renderMoment() {
  const m = state.moment;
  if (!m) return;
  const q = $('#momentFilter').value.trim().toLowerCase();
  const match = (o) => !q || (o.name || '').toLowerCase().includes(q) || String(o.id).includes(q);
  const players = m.players.filter(match), grids = m.grids.filter(match);
  const row = (o, kind) => {
    const tracked = findTrack(trackKey(kind, o.id));
    const where = kind === 'grid' ? `${o.blocks} бл.${o.static ? ', статика' : ''}` : (o.grid ? 'на гриде' : 'пешком');
    return `<div class="item" data-kind="${kind}" data-id="${esc(o.id)}">
      ${trackButton(kind, o.id, o.name)}
      <span class="count ${o.events ? 'hot' : ''}">${o.events ? plural(o.events, 'событие', 'события', 'событий') : 'без событий'}</span>
      <b>${esc(o.name || o.id)}</b><br><span class="when">${esc(where)} · позиция ${fmt(o.t, false)}</span></div>`;
  };
  $('#moment').innerHTML = `<p class="hint">${fmt(m.at)}: игроков ${m.players.length}, гридов ${m.gridsTotal};
      события — за интервал шкалы ${fmt(m.eventsFrom)} – ${fmt(m.eventsTo)}${q ? `; по запросу: ${players.length + grids.length}` : ''}.</p>` +
    (players.length ? `<div class="head">Игроки</div>` + players.map((o) => row(o, 'player')).join('') : '') +
    (grids.length ? `<div class="head">Гриды</div>` + grids.slice(0, 300).map((o) => row(o, 'grid')).join('') +
      (grids.length > 300 ? `<div class="more">и ещё ${grids.length - 300} — уточните поиск</div>` : '') : '') +
    (!players.length && !grids.length ? `<p class="hint">${q ? 'Никого не нашлось.' : 'В этот момент записей нет.'}</p>` : '');
  document.querySelectorAll('#moment .item').forEach((el) => {
    const list = el.dataset.kind === 'player' ? m.players : m.grids;
    const o = list.find((x) => x.id === el.dataset.id);
    el.addEventListener('click', () => focusOn(world(o.x, o.y, o.z)));
    el.addEventListener('dblclick', () => flyTo(world(o.x, o.y, o.z)));
  });
  bindTrackButtons($('#moment'));
}

// "+ следить" adds to the view; once tracked, the same button stops it
function trackButton(kind, id, name) {
  const tracked = findTrack(trackKey(kind, id));
  return tracked
    ? `<button class="untrack" data-track="${kind}" data-id="${esc(id)}" data-name="${esc(name || '')}" title="Убрать из просмотра">− перестать следить</button>`
    : `<button data-track="${kind}" data-id="${esc(id)}" data-name="${esc(name || '')}" title="Добавить к просмотру: трек, события, инвентарь">+ следить</button>`;
}
function bindTrackButtons(container) {
  container.querySelectorAll('button[data-track]').forEach((b) => b.addEventListener('click', async (e) => {
    e.stopPropagation();
    const key = trackKey(b.dataset.track, b.dataset.id);
    if (findTrack(key)) removeTrack(key);
    else await addTrack(b.dataset.track, b.dataset.id, b.dataset.name);
    buildAmbient();
    renderMoment();
    if (lastNear) renderNear();
  }));
}
$('#momentFilter').addEventListener('input', debounce(() => renderMoment(), 150));

$('#showAmbient').addEventListener('change', buildAmbient);
$('#addActive').addEventListener('click', async () => {
  const m = state.moment;
  if (!m) return;
  const active = [...m.players.map((o) => ['player', o]), ...m.grids.map((o) => ['grid', o])]
    .filter(([, o]) => o.events > 0).sort((a, b) => b[1].events - a[1].events).slice(0, 10);
  for (const [kind, o] of active) await addTrack(kind, o.id, o.name);
  buildAmbient();
  renderMoment();
});

// ------------------------------------------------------------------ picking and tips

// what is under the mouse: the nearest thing within a few pixels on the screen (markers first), found by
// projecting their positions - cheaper than casting a ray at thousands of dots
const pickV = new THREE.Vector3();
function pick(ev) {
  const r = renderer.domElement.getBoundingClientRect();
  const mx = ev.clientX - r.left, my = ev.clientY - r.top;
  let best = null, bestD = 10 * 10;
  const near = (x, y, z, weight) => {
    pickV.set(x, y, z).project(camera);
    if (pickV.z > 1) return Infinity;
    const dx = (pickV.x + 1) / 2 * r.width - mx, dy = (1 - pickV.y) / 2 * r.height - my;
    return (dx * dx + dy * dy) * weight;
  };
  const consider = (o, weight = 1) => {
    const d = near(o.position.x, o.position.y, o.position.z, weight);
    if (d < bestD) { bestD = d; best = o; }
  };
  for (const t of state.tracks) {
    if (t.marker?.visible) consider(t.marker, 0.4);
    for (const s of t.eventSprites || []) if (s.visible && s.isSprite) consider(s);
    const dots = t.eventDots;
    if (dots?.visible) {
      const p = dots.geometry.attributes.position.array;
      for (let i = 0; i < t.located.length; i++) {
        const d = near(p[i * 3], p[i * 3 + 1], p[i * 3 + 2], 1);
        if (d < bestD) {
          bestD = d;
          best = { position: new THREE.Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), userData: { event: t.located[i], track: t } };
        }
      }
    }
  }
  for (const o of ambient.children) if (o.isSprite) consider(o, 0.7);
  return best;
}
// the tip follows the mouse, worked out once a frame at most
let hoverEvent = null;
renderer.domElement.addEventListener('mousemove', (ev) => { hoverEvent = ev; });
renderer.domElement.addEventListener('mouseleave', () => { hoverEvent = null; $('#tip').hidden = true; });
function hover(ev) {
  const o = pick(ev);
  const tip = $('#tip');
  if (!o) { tip.hidden = true; return; }
  tip.hidden = false;
  tip.style.left = ev.clientX + 14 + 'px';
  tip.style.top = ev.clientY + 14 + 'px';
  if (o.userData.ambientJump || o.userData.jumpEnd) {
    const j = o.userData.ambientJump || o.userData.jumpEnd;
    tip.innerHTML = `<b>прыжок</b> ${fmt(j.t, false)}${j.amount ? ' на ' + distanceText(j.amount) : ''}<br>${esc((j.detail || '').replace(/^to=\S+\s*/, ''))}`;
    return;
  }
  if (o.userData.ambient) {
    const a = o.userData.ambient;
    tip.innerHTML = `<b>${esc(a.name || a.id)}</b><br>${o.userData.kind === 'grid' ? `грид, ${a.blocks} бл.${a.static ? ', статика' : ''}` : 'игрок'}` +
      `<br>${a.events ? plural(a.events, 'событие', 'события', 'событий') : 'без событий'} · клик — добавить`;
    return;
  }
  tip.innerHTML = o.userData.event ? eventHtml(o.userData.event, o.userData.track) : `<b>${esc(o.userData.track.name)}</b><br>${esc(o.userData.track.kind)} ${esc(o.userData.track.id)}` +
    '<br><span class="utc">двойной клик — подлететь</span>';
}
// a double click flies up to what is under the mouse
renderer.domElement.addEventListener('dblclick', (ev) => {
  if (controls.consumeDrag()) return;               // (the end of turning the view, not a click)
  const o = pick(ev);
  if (!o) return;
  const marker = o.userData.track && !o.userData.event;
  if (marker) select(o.userData.track.key, false);
  flyTo(o.position.clone(), marker);
});
renderer.domElement.addEventListener('click', (ev) => {
  if (controls.consumeDrag()) return;               // (the end of turning the view, not a click)
  const o = pick(ev);
  if (!o) return;
  if (o.userData.ambientJump) {
    const j = o.userData.ambientJump;
    setTime(j.t);
    addTrack('grid', j.entity).then(() => { buildAmbient(); renderMoment(); });
    return;
  }
  if (o.userData.jumpEnd && !o.userData.track) return;
  if (o.userData.ambient) {
    const a = o.userData.ambient;
    addTrack(o.userData.kind, a.id, a.name).then(() => { buildAmbient(); renderMoment(); });
    return;
  }
  if (o.userData.event) setTime(o.userData.event.t);
  select(o.userData.track.key, false);
});

// ------------------------------------------------------------------ side panel

function showTab(name) {
  const button = document.querySelector(`.tabs button[data-tab="${name}"]`);
  if (!button) return;
  document.querySelectorAll('.tabs button').forEach((x) => x.classList.toggle('active', x === button));
  document.querySelectorAll('.tab').forEach((x) => x.classList.toggle('active', x.id === 'tab-' + name));
  if (name === 'inventory') refreshInventory();
  saveHash();
}
const activeTab = () => document.querySelector('.tabs button.active')?.dataset.tab;
document.querySelectorAll('.tabs button').forEach((b) => b.addEventListener('click', () => showTab(b.dataset.tab)));

function renderTracked() {
  $('#tracked').innerHTML = state.tracks.map((t) => `
    <div class="track ${t.key === state.selected ? 'selected' : ''}" data-key="${esc(t.key)}">
      <span class="swatch" style="background:${t.color}"></span>
      <span class="name">${esc(t.name)}<br><span class="meta">${t.kind === 'grid' ? 'грид' : 'игрок'} ${esc(t.id)} ·
        ${t.total ?? 0} точек, ${t.events.length} событий</span></span>
      <button class="x" title="Убрать">✕</button>
    </div>`).join('');
  document.querySelectorAll('#tracked .track').forEach((el) => {
    el.addEventListener('click', () => select(el.dataset.key));
    el.addEventListener('dblclick', () => {
      const track = findTrack(el.dataset.key);
      const s = track && stateAt(track, state.t);
      if (s) flyTo(s.pos, true);
    });
    el.querySelector('.x').addEventListener('click', (e) => { e.stopPropagation(); removeTrack(el.dataset.key); });
  });
}

// who an id is: the name when any list knows it, the id only when none does
function who(id, track) {
  if (!id || id === '0') return '';
  return track?.names?.[id] || state.tracks.find((t) => t.id === id)?.name || momentName(id) ||
    state.tracks.map((t) => t.names?.[id]).find(Boolean) || id;
}
function momentName(id) {
  const m = state.moment;
  return m && (m.players.find((o) => o.id === id)?.name || m.grids.find((o) => o.id === id)?.name);
}

function eventHtml(e, track) {
  const parts = [`<span class="when">${fmt(e.t, false)}</span><span class="kind" style="background:${kindColor(e.kind)}">${esc(e.kind)}</span>`];
  if (e.actor && e.actor !== '0') parts.push('кто: ' + esc(who(e.actor, track)));
  if (e.entity && e.entity !== '0') parts.push('над: ' + esc(who(e.entity, track)));
  if (e.kind === 'jump' || e.kind === 'jump_passenger') {
    if (e.amount) parts.push('на ' + distanceText(e.amount));
    const to = jumpTarget(e);
    if (to) parts.push(`<div class="detail">куда: ${num(to[0], 0)} : ${num(to[1], 0)} : ${num(to[2], 0)}</div>`);
  } else if (e.kind === 'drill') {
    if (e.amount) parts.push(num(e.amount, 1) + ' м³ грунта');
  } else if (e.amount !== null && e.amount !== undefined) parts.push('×' + num(e.amount));
  if (e.count) parts.push(e.count + ' раз');
  let detail = e.kind === 'jump' || e.kind === 'jump_passenger' ? (e.detail || '').replace(/^to=\S+\s*/, '') : e.detail;
  // the drill's detail is the ore most of it was; "clearing" for the drill's secondary action, which keeps nothing
  if (e.kind === 'drill' && detail) detail = /^clearing /.test(detail) ? 'расчистка без сбора, в основном ' + detail.slice(9) : 'руда: ' + detail;
  if (detail) parts.push(`<div class="detail">${esc(detail)}</div>`);
  return parts.join(' ');
}

// The events of every tracked object (or only the selected one), in time order. Only a window of them
// around the moment is in the page: thousands of rows made every move of the slider slow.
const EVENT_WINDOW = 300;
let eventList = [];             // [{ e, track, t }]
let eventWindow = null;         // { start, end } of eventList in the page
let eventNow = -1;

function renderEvents() {
  const only = $('#eventsSelected').checked;
  const sel = selectedTrack();
  const filter = $('#eventFilter').value.trim().toLowerCase();
  eventList = [];
  for (const track of only ? (sel ? [sel] : []) : state.tracks)
    for (const e of track.events)
      if (!filter || (e.kind + ' ' + (e.detail || '') + ' ' + track.name).toLowerCase().includes(filter)) eventList.push({ e, track, t: e.t });
  eventList.sort((a, b) => a.t - b.t);
  eventWindow = null;
  showEvents(true);
  renderLegend();
}
$('#eventFilter').addEventListener('input', debounce(renderEvents, 150));
$('#eventsSelected').addEventListener('change', renderEvents);

function eventIndexAt(t) {
  let lo = 0, hi = eventList.length - 1, found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (eventList[mid].t <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
}

// the window around the moment: rebuilt only when the moment nears its edge
function showEvents(force = false) {
  const list = $('#events');
  const now = eventIndexAt(state.t);
  const w = eventWindow;
  const inside = w && (now >= w.start + 30 || w.start === 0) && (now < w.end - 30 || w.end === eventList.length);
  if (!force && inside) { markEventsNow(now); return; }
  if (!eventList.length) {
    list.innerHTML = `<p class="hint">${state.tracks.length ? 'Нет событий у отслеживаемых в этом интервале.' : 'Добавьте игрока или грид к просмотру.'}</p>`;
    eventWindow = { start: 0, end: 0 };
    return;
  }
  const start = Math.max(0, Math.min(now - EVENT_WINDOW / 2, eventList.length - EVENT_WINDOW));
  const end = Math.min(eventList.length, start + EVENT_WINDOW);
  const many = state.tracks.length > 1 && !$('#eventsSelected').checked;
  let html = start > 0 ? `<div class="more">раньше ещё ${start} — сдвиньте время назад</div>` : '';
  for (let i = start; i < end; i++) {
    const { e, track } = eventList[i];
    // whose event: a stripe of the object's colour at the left (the dot colours are the kinds, on the map)
    const whose = many ? `<b>${esc(track.name)}</b> ` : '';
    const stripe = many ? ` style="border-left-color:${track.color}"` : '';
    html += `<div class="item${many ? ' owned' : ''} ${e.t > state.t ? 'later' : ''}" data-i="${i}"${stripe}>${whose}${eventHtml(e, track)}</div>`;
  }
  if (end < eventList.length) html += `<div class="more">позже ещё ${eventList.length - end}</div>`;
  list.innerHTML = html;
  eventWindow = { start, end };
  eventNow = -2;
  markEventsNow(now);
}

// marks the rows between the old and the new moment, and scrolls to the moment
function markEventsNow(now) {
  if (now === eventNow || !eventWindow) return;
  const rows = $('#events').querySelectorAll('.item');
  const { start } = eventWindow;
  rows.forEach((el, k) => {
    const i = start + k;
    el.classList.toggle('later', i > now);        // as on the map: what is still to come is dim
    el.classList.toggle('now', i === now);
  });
  eventNow = now;
  const current = rows[now - start];
  if (current && activeTab() === 'events') current.scrollIntoView({ block: 'nearest' });
}

// one listener for every row: a click sets the time, a double click flies up to where it happened
$('#events').addEventListener('click', (ev) => {
  const row = ev.target.closest('.item');
  if (row) setTime(eventList[Number(row.dataset.i)].t);
});
$('#events').addEventListener('dblclick', (ev) => {
  const row = ev.target.closest('.item');
  const e = row && eventList[Number(row.dataset.i)]?.e;
  if (e && e.x !== null && e.x !== undefined) flyTo(world(e.x, e.y, e.z));
});

// ------------------------------------------------------------------ inventory

let inventoryRequest = 0;
const refreshInventory = debounce(async () => {
  if (!$('#tab-inventory').classList.contains('active')) return;
  const sel = selectedTrack();
  if (!sel) { $('#inventory').innerHTML = '<p class="hint">Выберите игрока или грид.</p>'; return; }
  const request = ++inventoryRequest;
  const data = await api('inventory', { kind: sel.kind, id: sel.id, t: Math.round(state.t) });
  if (request !== inventoryRequest) return;
  renderInventory(sel, data);
}, 250);

function renderInventory(sel, data) {
  const filter = $('#itemFilter').value.trim().toLowerCase();
  const key = (i) => i.type + '/' + i.subtype;
  const cards = data.inventories.map((inv) => {
    const prev = new Map((inv.prev?.items || []).map((i) => [key(i), i.amount]));
    const now = new Map(inv.items.map((i) => [key(i), i.amount]));
    const rows = inv.items.map((i) => {
      const d = inv.prev ? i.amount - (prev.get(key(i)) || 0) : 0;
      return { name: key(i), amount: i.amount, diff: d };
    });
    if (inv.prev) for (const [k, a] of prev) if (!now.has(k)) rows.push({ name: k, amount: 0, diff: -a, gone: true });
    const shown = rows.filter((r) => !filter || r.name.toLowerCase().includes(filter));
    if (filter && !shown.length) return '';
    // as a person reads it: the block's name and which of its inventories, counted from 1; a character's
    // inventory as the player's suit (the body it has now) or a body it left behind
    const who = inv.body === 'current' ? `Скафандр «${inv.name || '?'}»`
      : inv.body === 'old' ? `Брошенное тело «${inv.name || '?'}»`
        : inv.name || 'блок ' + inv.entity;
    const title = `${who}[${(Number(inv.inv) || 0) + 1}]`;
    return `<div class="inv">
      <h4><span>${esc(title)}</span>
        <span class="meta" title="Когда записано это содержимое">${fmt(inv.at, false)}${inv.prev ? ' (было ' + fmt(inv.prev.at, false) + ')' : ''}</span></h4>
      ${shown.length ? `<table>${shown.map((r) => `<tr class="${r.gone ? 'gone' : ''}"><td>${esc(r.name)}</td>
        <td class="amount">${num(r.amount)}</td>
        <td class="diff ${r.diff > 0 ? 'plus' : r.diff < 0 ? 'minus' : ''}">${r.diff ? (r.diff > 0 ? '+' : '') + num(r.diff) : ''}</td></tr>`).join('')}</table>`
        : '<div class="empty">пусто</div>'}
      <div class="volume">объём ${num(inv.volume * 1000, 0)} / ${num(inv.max * 1000, 0)} л · ${esc(inv.entity)}</div>
    </div>`;
  }).join('');
  $('#inventory').innerHTML = `<p class="hint">${esc(sel.name)} на ${fmt(data.at)}: ${data.inventories.length} инвентарей.
    Справа — изменение по сравнению с прошлой записью этого инвентаря.</p>` + (cards || '<p class="hint">Ничего не найдено.</p>');
}
$('#itemFilter').addEventListener('input', refreshInventory);

// ------------------------------------------------------------------ near and alerts

$('#nearGo').addEventListener('click', async () => {
  const sel = selectedTrack();
  const s = sel && stateAt(sel, state.t);
  if (!s) { $('#near').innerHTML = '<p class="hint">Выберите объект с позицией в этот момент.</p>'; return; }
  const p = s.point;
  const r = Number($('#nearRadius').value) || 1000;
  const w = (Number($('#nearWindow').value) || 5) * 60_000;
  status('Поиск рядом…');
  const data = await api('near', { x: p[1], y: p[2], z: p[3], r, from: Math.round(state.t - w), to: Math.round(state.t + w) });
  status('');
  lastNear = { data, r, w, name: sel.name, at: state.t };
  renderNear();
});

let lastNear = null;
function renderNear() {
  const { data, r, w, name, at } = lastNear;
  const row = (kind, o) => `<div class="item">${trackButton(kind, o.id, data.names[o.id])}
    ${kind === 'grid' ? 'грид' : 'игрок'} <b>${esc(data.names[o.id] || o.id)}</b><br>
    <span class="when">${fmt(o.first, false)} – ${fmt(o.last, false)}</span></div>`;
  $('#near').innerHTML = `<p class="hint">В ${num(r, 0)} м от «${esc(name)}», ${fmt(at - w, false)}–${fmt(at + w, false)}.</p>` +
    data.players.map((o) => row('player', o)).join('') + data.grids.map((o) => row('grid', o)).join('');
  bindTrackButtons($('#near'));
}

$('#alertsGo').addEventListener('click', async () => {
  const alerts = await api('alerts', { from: state.from, to: state.to });
  $('#alerts').innerHTML = alerts.length ? alerts.map((a) => `<div class="item" data-t="${a.t}">
      <span class="when">${fmt(a.t)}</span><span class="kind" style="background:#ec7063">${esc(a.kind)}</span>
      <div class="detail">${esc(a.detail)}</div></div>`).join('') : '<p class="hint">Алертов нет.</p>';
  document.querySelectorAll('#alerts .item').forEach((el) => el.addEventListener('click', () => setTime(Number(el.dataset.t))));
});

// ------------------------------------------------------------------ search: a drop-down of everyone active in the range

const results = $('#results');
let objects = new Map();        // filter -> { players, grids, playersTotal, gridsTotal } of the current range
let options = [];               // what the drop-down shows, in order: { kind, id, name, note }
let activeOption = -1;

async function loadObjects(q) {
  if (!objects.has(q)) objects.set(q, await api('objects', { from: state.from, to: state.to, q }));
  return objects.get(q);
}

const plural = (n, one, few, many) => {
  const m10 = n % 10, m100 = n % 100;
  return n + ' ' + (m10 === 1 && m100 !== 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20) ? few : many);
};
const activityNote = (o) => [o.events ? plural(o.events, 'событие', 'события', 'событий') : '', o.positions ? plural(o.positions, 'точка', 'точки', 'точек') : '']
  .filter(Boolean).join(' · ');

async function openDropdown() {
  const q = $('#search').value.trim();
  const all = await loadObjects(q);
  if ($('#search').value.trim() !== q) return;      // typed on meanwhile
  const players = all.players.map((o) => ({ kind: 'player', id: o.id, name: o.name, note: activityNote(o) }));
  const grids = all.grids.map((o) => ({ kind: 'grid', id: o.id, name: o.name, note: activityNote(o) }));
  options = [...players, ...grids];
  const section = (title, list, offset, total) => list.length
    ? `<div class="head">${title} (${total ?? list.length})</div>` + list.map((o, i) => optionHtml(o, offset + i)).join('') +
      (total > list.length ? `<div class="none">и ещё ${total - list.length} — уточните поиск</div>` : '') : '';
  let html = section('Игроки в интервале', players, 0, all.playersTotal) + section('Гриды в интервале', grids, players.length, all.gridsTotal);
  if (q.length >= 2) {
    // those not active in the range, from the names of the week before
    const found = await api('search', { q, from: state.from - 7 * 24 * HOUR, to: state.to });
    const known = new Set(options.map((o) => o.kind + ':' + o.id));
    const others = found.filter((f) => !known.has(f.kind + ':' + f.id))
      .map((f) => ({ kind: f.kind, id: f.id, name: f.name, note: (f.kind === 'grid' ? 'грид' : 'игрок') + ', нет активности в интервале' }));
    const offset = options.length;
    options.push(...others);
    html += section('Вне интервала', others, offset);
  }
  results.innerHTML = html || '<div class="none">Никого: в этом интервале нет ни позиций, ни событий.</div>';
  results.querySelectorAll('.opt').forEach((el) => {
    const i = Number(el.dataset.i);
    el.addEventListener('mousedown', (e) => { e.preventDefault(); pickOption(i); });
    el.addEventListener('mousemove', () => highlight(i));
  });
  activeOption = -1;
  results.hidden = false;
}

function optionHtml(o, i) {
  const tracked = findTrack(trackKey(o.kind, o.id)) ? ' tracked' : '';
  return `<div class="opt${tracked}" data-i="${i}"><span class="n">${esc(o.name || o.id)}</span><span class="c">${esc(o.note)}</span></div>`;
}

function highlight(i) {
  activeOption = i;
  let active = null;
  results.querySelectorAll('.opt').forEach((el) => {
    const on = Number(el.dataset.i) === i;
    el.classList.toggle('active', on);
    if (on) active = el;
  });
  active?.scrollIntoView({ block: 'nearest' });
}

function pickOption(i) {
  const o = options[i];
  if (!o) return;
  results.hidden = true;
  search.value = '';
  search.blur();
  addTrack(o.kind, o.id, o.name);
}

const search = $('#search');
search.addEventListener('focus', () => openDropdown());
search.addEventListener('click', () => { if (results.hidden) openDropdown(); });
search.addEventListener('input', debounce(openDropdown, 200));
search.addEventListener('keydown', (e) => {
  if (e.key === 'ArrowDown') { e.preventDefault(); if (results.hidden) openDropdown(); else highlight(Math.min(options.length - 1, activeOption + 1)); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); highlight(Math.max(0, activeOption - 1)); }
  else if (e.key === 'Enter') { e.preventDefault(); pickOption(activeOption >= 0 ? activeOption : 0); }
  else if (e.key === 'Escape') { results.hidden = true; search.blur(); }
});
$('#searchOpen').addEventListener('click', () => {
  if (!results.hidden) { results.hidden = true; return; }
  if (document.activeElement === search) openDropdown(); else search.focus();
});
document.addEventListener('mousedown', (e) => { if (!e.target.closest('.search')) results.hidden = true; });

// ------------------------------------------------------------------ time

function setTime(t, fromPlayback = false, fromLive = false) {
  // a moment picked by hand, away from "now", ends following the live edge
  if (liveTimer && !fromLive && t < state.to - 3000) setLive(false);
  state.t = Math.min(state.to, Math.max(state.from, t));
  showEvents();
  if (!fromPlayback) saveHash();
  refreshInventory();
  if (!fromPlayback || Math.abs(state.t - lastMomentAt) > 2000 * state.speed) loadMoment();
  drawTimeline();
}

// the camera goes where most happened in the range, and the moment to when
async function focusHotspot() {
  const hot = await api('hotspot', { from: state.from, to: state.to });
  if (!hot.found) return;
  const pos = world(hot.x, hot.y, hot.z);
  const offset = camera.position.clone().sub(controls.target);
  offset.setLength(Math.max(1500, hot.cell * 1.5));
  controls.target.copy(pos);
  camera.position.copy(pos).add(offset);
  $('#follow').checked = false;
  setTime(hot.t);
  status(`Больше всего событий (${hot.count}) — здесь, около ${fmt(hot.t, false)}`);
}

const rangeHistory = [];
async function setRange(from, to, t, remember = true, hot = false) {
  setLive(false);
  if (remember && (Math.round(from) !== state.from || Math.round(to) !== state.to)) {
    rangeHistory.push([state.from, state.to, state.t]);
    if (rangeHistory.length > 30) rangeHistory.shift();
  }
  $('#rangeBack').hidden = !rangeHistory.length;
  selection = null;
  $('#rangeBar').hidden = true;
  state.from = Math.round(from);
  state.to = Math.round(to);
  objects = new Map();
  loadActivity();
  loadWorld();
  state.t = Math.min(state.to, Math.max(state.from, t ?? state.to));
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  status('Загрузка…');
  for (const track of state.tracks) await loadTrack(track);
  renderTracked();
  status('');
  setTime(state.t);
  if (hot) await focusHotspot();
}

$('#reload').addEventListener('click', () => setRange(fromInput($('#from').value), fromInput($('#to').value), state.t, true, true));
$('#allRange').addEventListener('click', async () => {
  const [days, clock] = await Promise.all([api('days'), api('now')]);
  if (!days.length) return;
  const oldest = Date.parse(days[days.length - 1] + 'T00:00:00Z');
  setRange(Math.max(oldest, clock.now - 7 * 24 * HOUR), clock.now, state.t, true, true);
});
$('#rangeBack').addEventListener('click', () => {
  const back = rangeHistory.pop();
  if (back) setRange(back[0], back[1], back[2], false);
});
$('#lastHour').addEventListener('click', async () => { const { now } = await api('now'); setRange(now - HOUR, now, now, true, true); });
$('#lastDay').addEventListener('click', async () => { const { now } = await api('now'); setRange(now - 24 * HOUR, now, now, true, true); });
$('#speed').addEventListener('change', () => { state.speed = Number($('#speed').value); });
$('#play').addEventListener('click', () => {
  setLive(false);
  if (!state.playing && state.t >= state.to) state.t = state.from;
  state.playing = !state.playing;
  $('#play').textContent = state.playing ? '⏸' : '▶';
  if (!state.playing) setTime(state.t);
});
// ------------------------------------------------------------------ live: the newest records every few seconds

// Every 1, 5 or 10 seconds: the range moves on to the server's "now" (as long as it was), the tracked
// players and grids get their new points and events (only what came since the last time, not the whole
// track again), and the moment stands on "now" - everyone in the moment, their inventories and the events
// list follow. Positions are written once a second and the day file is written every second, so what shows
// is a second or two behind the game.
let liveTimer = null, liveBusy = false, liveActivityAt = 0;
function setLive(on) {
  if (!!liveTimer === on) return;
  $('#live').checked = on;
  clearInterval(liveTimer);
  liveTimer = null;
  document.body.classList.toggle('is-live', on);
  if (on) {
    if (state.playing) { state.playing = false; $('#play').textContent = '▶'; }
    liveTimer = setInterval(liveTick, Number($('#liveEvery').value));
    liveTick();
  }
  saveHash();
}
$('#live').addEventListener('change', () => setLive($('#live').checked));
$('#liveEvery').addEventListener('change', () => { if (liveTimer) { setLive(false); setLive(true); } else saveHash(); });

async function liveTick() {
  if (liveBusy) return;
  liveBusy = true;
  try {
    const { now } = await api('now');
    if (!liveTimer) return;
    const since = state.to;
    const span = Math.max(60_000, state.to - state.from);
    state.to = now;
    state.from = now - span;
    $('#from').value = toInput(state.from);
    $('#to').value = toInput(state.to);
    await Promise.all(state.tracks.map((track) => extendTrack(track, since)));
    if (!liveTimer) return;
    renderEvents();
    // the activity bars of the whole range: not more often than every 5 s
    if (now - liveActivityAt >= 5000) { liveActivityAt = now; loadActivity(); }
    setTime(now, false, true);
  } catch (e) {
    status('Обновление в реальном времени: ' + e.message, true);
  } finally {
    liveBusy = false;
  }
}

// a track's points and events since the given time added on, the ones gone out of the range dropped
async function extendTrack(track, since) {
  const from = since - 2000;           // a little back: a point written late is not missed
  const [data, ev] = await Promise.all([
    api('track', { kind: track.kind, id: track.id, from, to: state.to }),
    api('events', { id: track.id, from, to: state.to }),
  ]);
  const lastPoint = track.points.length ? track.points[track.points.length - 1][0] : -Infinity;
  const points = track.points.filter((p) => p[0] >= state.from);
  for (const p of data.points) if (p[0] > lastPoint) points.push(p);
  track.points = points;
  const seen = new Set(track.events.map((e) => e.t + '|' + e.kind + '|' + e.entity));
  track.events = track.events.filter((e) => e.t >= state.from).concat(ev.events.filter((e) => !seen.has(e.t + '|' + e.kind + '|' + e.entity)));
  Object.assign(track.names, ev.names);
  if (data.name) track.name = data.name;
  build3d(track);
}

function setLayer(value) {
  layer = value;
  document.querySelectorAll('#layers button').forEach((b) => b.classList.toggle('active', b.dataset.layer === value));
  for (const t of state.tracks) {
    for (const s of t.eventSprites) s.visible = showEventLayer();
    if (t.eventDots) t.eventDots.visible = showEventLayer();
    if (t.line) t.line.visible = showObjects();
  }
  for (const o of ambient.children) o.visible = o.userData.layer === 'events' ? showEventLayer() : showObjects();
  renderLegend();
  saveHash();
}

// what the colours of the event dots on the map mean: the kinds the tracked objects have, most frequent first
function renderLegend() {
  const counts = {};
  for (const t of state.tracks) for (const e of t.located || []) counts[e.kind] = (counts[e.kind] || 0) + 1;
  const kinds = Object.keys(counts).sort((a, b) => counts[b] - counts[a]);
  const legend = $('#legend');
  legend.hidden = !kinds.length || !showEventLayer();
  legend.innerHTML = '<div class="title">События на карте</div>' + kinds.map((k) =>
    `<div><span class="dot" style="background:${kindColor(k)}"></span>${esc(KIND_NAMES[k] || k)} <span class="n">${counts[k]}</span></div>`).join('');
}
document.querySelectorAll('#layers button').forEach((b) => b.addEventListener('click', () => setLayer(b.dataset.layer)));

// ------------------------------------------------------------------ timeline: time axis, activity bars, tracked objects

// what the activity bars stack, bottom up; movement is drawn apart, as a faint area behind
const CATEGORIES = [
  { name: 'бой', color: '#ec7063', kinds: ['damage', 'grind', 'destroyed', 'death'] },
  { name: 'предметы', color: '#58d68d', kinds: ['transfer', 'drop', 'balance'] },
  { name: 'стройка', color: '#5dade2', kinds: ['block_built', 'block_removed', 'weld', 'paste', 'grid_added', 'grid_removed'] },
  { name: 'прыжки', color: '#c39bd3', kinds: ['jump', 'jump_passenger'] },
  { name: 'добыча', color: '#b9770e', kinds: ['drill'] },
  { name: 'прочее', color: '#bdc3c7', kinds: null },
];
function categoryOf(kind) {
  const i = CATEGORIES.findIndex((c) => c.kinds && c.kinds.includes(kind));
  return i >= 0 ? i : CATEGORIES.length - 1;
}

const timeline = $('#timeline');
const AXIS = 13, BARS = 34, ROWS_TOP = AXIS + BARS + 4;

let activity = null;            // { from, width, buckets: [{ b, counts }] } of the current range
let activityRequest = 0;
const loadActivity = debounce(async () => {
  const request = ++activityRequest;
  const buckets = Math.max(20, Math.floor(timeline.clientWidth / 3));
  const data = await api('activity', { from: state.from, to: state.to, buckets });
  if (request !== activityRequest) return;
  for (const b of data.buckets) {
    b.categories = CATEGORIES.map(() => 0);
    b.events = 0;
    for (const [kind, n] of Object.entries(b.counts)) {
      if (kind.startsWith('@')) continue;
      b.categories[categoryOf(kind)] += n;
      b.events += n;
    }
    b.movement = (b.counts['@players'] || 0) + (b.counts['@grids'] || 0);
  }
  activity = data;
  activityVersion++;
  drawTimeline();
}, 150);

// The bars, the axis and the rows of the tracked objects change with the data, not with the moment: drawn
// into a canvas of their own when the data changes, and copied under the moment's line otherwise.
const timelineBase = document.createElement('canvas');
let timelineBaseKey = '';
let activityVersion = 0;
function drawTimeline() {
  const w = timeline.clientWidth, h = timeline.clientHeight;
  if (!w) return;
  const ratio = window.devicePixelRatio || 1;
  if (timeline.width !== Math.round(w * ratio) || timeline.height !== Math.round(h * ratio)) {
    timeline.width = Math.round(w * ratio);
    timeline.height = Math.round(h * ratio);
  }
  const key = [w, h, ratio, state.from, state.to, activityVersion, ...state.tracks.map((t) => t.key + t.color + t.events.length + '/' + t.points.length)].join('|');
  if (key !== timelineBaseKey) {
    timelineBase.width = timeline.width;
    timelineBase.height = timeline.height;
    drawTimelineBase(timelineBase.getContext('2d'), w, h, ratio);
    timelineBaseKey = key;
  }
  const g = timeline.getContext('2d');
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.clearRect(0, 0, timeline.width, timeline.height);
  g.drawImage(timelineBase, 0, 0);
  g.setTransform(ratio, 0, 0, ratio, 0, 0);
  const span = Math.max(1, state.to - state.from);
  const x = (t) => (t - state.from) / span * w;
  drawTimelineNow(g, x, h);
}

function drawTimelineBase(g, w, h, ratio) {
  g.setTransform(ratio, 0, 0, ratio, 0, 0);
  g.clearRect(0, 0, w, h);
  g.fillStyle = '#12151a';
  g.fillRect(0, 0, w, h);
  const span = Math.max(1, state.to - state.from);
  const x = (t) => (t - state.from) / span * w;

  // activity: bars by category (square root scale, so a quiet minute still shows next to a battle)
  if (activity && activity.buckets.length) {
    const bw = Math.max(1, activity.width / span * w);
    let maxEvents = 1, maxMove = 1;
    for (const b of activity.buckets) { maxEvents = Math.max(maxEvents, b.events); maxMove = Math.max(maxMove, b.movement); }
    const base = AXIS + BARS;
    for (const b of activity.buckets) {
      const left = x(activity.from + b.b * activity.width);
      if (b.movement) {
        g.fillStyle = 'rgba(255,255,255,0.09)';
        const mh = Math.sqrt(b.movement / maxMove) * BARS;
        g.fillRect(left, base - mh, Math.max(1, bw - 0.5), mh);
      }
      if (!b.events) continue;
      const total = Math.max(2, Math.sqrt(b.events / maxEvents) * BARS);
      let y = base;
      b.categories.forEach((n, i) => {
        if (!n) return;
        const part = total * n / b.events;
        g.fillStyle = CATEGORIES[i].color;
        g.fillRect(left, y - part, Math.max(1, bw - 0.5), part);
        y -= part;
      });
    }
  }

  // legend, top right; the time labels stop short of it
  g.font = '10px Consolas, monospace';
  let lx = w - 8;
  g.textAlign = 'right';
  for (const c of [...CATEGORIES].reverse()) {
    g.fillStyle = '#8b95a5';
    g.fillText(c.name, lx, 10);
    lx -= g.measureText(c.name).width + 4;
    g.fillStyle = c.color;
    g.fillRect(lx - 7, 3, 7, 7);
    lx -= 16;
  }
  g.textAlign = 'left';
  // the time axis
  const steps = [60_000, 300_000, 900_000, 1800_000, HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, 24 * HOUR];
  const step = steps.find((s) => span / s <= w / 70) || 24 * HOUR;
  g.fillStyle = '#8b95a5';
  for (let t = Math.ceil(state.from / step) * step; t <= state.to; t += step) {
    g.fillRect(x(t), 0, 1, 4);
    const label = step >= 24 * HOUR ? fmt(t).slice(5, 10) : fmt(t, false).slice(0, 5);
    if (x(t) + 3 + g.measureText(label).width < lx) g.fillText(label, x(t) + 3, 10);
  }

  // the tracked objects: when they have positions, and their events
  state.tracks.forEach((track, row) => {
    const y = ROWS_TOP + (row % 4) * 5;
    if (y + 3 > h) return;
    g.fillStyle = track.color + '66';
    if (track.points.length) g.fillRect(x(track.points[0][0]), y, Math.max(2, x(track.points[track.points.length - 1][0]) - x(track.points[0][0])), 3);
    for (const e of track.events) {
      g.fillStyle = kindColor(e.kind);
      g.fillRect(x(e.t) - 1, y - 1, 2, 5);
    }
  });

}

function drawTimelineNow(g, x, h) {
  // the selected range
  if (typeof selection !== 'undefined' && selection) {
    g.fillStyle = 'rgba(95,179,249,0.18)';
    g.fillRect(x(selection[0]), 0, x(selection[1]) - x(selection[0]), h);
    g.fillStyle = '#5fb3f9';
    g.fillRect(x(selection[0]), 0, 1, h);
    g.fillRect(x(selection[1]) - 1, 0, 1, h);
  }

  // now: a line with a handle at the top, which can be dragged
  g.fillStyle = '#5fb3f9';
  g.fillRect(x(state.t) - 1, 0, 2, h);
  g.beginPath();
  g.moveTo(x(state.t) - 5, 0);
  g.lineTo(x(state.t) + 5, 0);
  g.lineTo(x(state.t), 7);
  g.fill();
}

let dragging = false;
const timeFromMouse = (ev) => {
  const r = timeline.getBoundingClientRect();
  return state.from + (ev.clientX - r.left) / r.width * (state.to - state.from);
};
// a click sets the moment; a drag selects a range (shown with a button to open it); a drag that starts on the
// playhead moves the moment
let downX = 0, downT = 0, mode = null;
let selection = null;           // [from, to] while a range is selected
const playheadX = () => {
  const r = timeline.getBoundingClientRect();
  return r.left + (state.t - state.from) / Math.max(1, state.to - state.from) * r.width;
};
timeline.addEventListener('mousedown', (ev) => {
  if (ev.button !== 0) return;      // the middle button pans (below)
  dragging = true;
  downX = ev.clientX;
  downT = timeFromMouse(ev);
  mode = Math.abs(ev.clientX - playheadX()) <= 6 ? 'scrub' : 'click';
});

// the pressed wheel (middle button) moves the whole range left and right; the data follows on release
let pan = null;                 // { x, from, to } where the pan started
timeline.addEventListener('mousedown', (ev) => {
  if (ev.button !== 1) return;
  ev.preventDefault();              // no auto-scroll
  pan = { x: ev.clientX, from: state.from, to: state.to };
  timeline.style.cursor = 'grabbing';
  setLive(false);
});
timeline.addEventListener('auxclick', (ev) => { if (ev.button === 1) ev.preventDefault(); });
window.addEventListener('mousemove', (ev) => {
  if (!pan) return;
  const shift = -(ev.clientX - pan.x) / timeline.getBoundingClientRect().width * (pan.to - pan.from);
  state.from = Math.round(pan.from + shift);
  state.to = Math.round(pan.to + shift);
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  selection = null;
  $('#rangeBar').hidden = true;
  drawTimeline();
});
window.addEventListener('mouseup', (ev) => {
  if (!pan || ev.button !== 1) return;
  const [from, to, start] = [state.from, state.to, pan];
  pan = null;
  timeline.style.cursor = '';
  if (from === start.from) return;
  [state.from, state.to] = [start.from, start.to];     // so that the back button returns to where the pan started
  setRange(from, to, Math.min(to, Math.max(from, state.t)));
});
window.addEventListener('mousemove', (ev) => {
  if (!dragging) return;
  if (mode === 'click' && Math.abs(ev.clientX - downX) > 4) mode = 'select';
  if (mode === 'scrub') setTime(timeFromMouse(ev), true);
  if (mode === 'select') {
    const t = Math.min(state.to, Math.max(state.from, timeFromMouse(ev)));
    selection = [Math.min(downT, t), Math.max(downT, t)];
    $('#rangeBar').hidden = true;
    drawTimeline();
  }
});
window.addEventListener('mouseup', (ev) => {
  if (!dragging) return;
  dragging = false;
  if (mode === 'click') {
    selection = null;
    $('#rangeBar').hidden = true;
    setTime(downT);
  } else if (mode === 'scrub') {
    setTime(state.t);
  } else if (mode === 'select' && selection) {
    showRangeBar();
  }
  mode = null;
});

// the wheel zooms the time line around the mouse at once; the data follows when the wheel stops
let wheelBase = null;
const applyWheel = debounce(() => {
  const [from, to, t] = [state.from, state.to, state.t];
  [state.from, state.to] = wheelBase;          // so that the back button returns to where the wheel started
  wheelBase = null;
  setRange(from, to, t);
}, 400);
timeline.addEventListener('wheel', (ev) => {
  ev.preventDefault();
  setLive(false);
  if (!wheelBase) wheelBase = [state.from, state.to];
  const k = ev.deltaY > 0 ? 1.3 : 1 / 1.3;
  const at = timeFromMouse(ev);
  const from = at - (at - state.from) * k, to = at + (state.to - at) * k;
  if (to - from < 60_000 || to - from > 7 * 24 * HOUR) return;
  state.from = Math.round(from);
  state.to = Math.round(to);
  state.t = Math.min(state.to, Math.max(state.from, state.t));
  $('#from').value = toInput(state.from);
  $('#to').value = toInput(state.to);
  selection = null;
  $('#rangeBar').hidden = true;
  drawTimeline();
  applyWheel();
}, { passive: false });

function showRangeBar() {
  const [a, b] = selection;
  const r = timeline.getBoundingClientRect();
  const wrap = timeline.parentElement.getBoundingClientRect();
  const mid = r.left + ((a + b) / 2 - state.from) / Math.max(1, state.to - state.from) * r.width - wrap.left;
  const bar = $('#rangeBar');
  bar.style.left = Math.max(120, Math.min(wrap.width - 120, mid)) + 'px';
  const sameDay = fmt(a).slice(0, 10) === fmt(b).slice(0, 10);
  $('#rangeZoom').textContent = `Показать ${fmt(a, !sameDay)} – ${fmt(b, false)} ›`;
  bar.hidden = false;
}
$('#rangeZoom').addEventListener('click', () => {
  if (!selection) return;
  const [a, b] = selection;
  setRange(a, b, Math.min(b, Math.max(a, state.t)), true, true);
});
$('#rangeCancel').addEventListener('click', () => { selection = null; $('#rangeBar').hidden = true; drawTimeline(); });
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && selection) { selection = null; $('#rangeBar').hidden = true; drawTimeline(); }
});

// what happened in the slice under the mouse
timeline.addEventListener('mousemove', (ev) => {
  const tip = $('#tip');
  if (!activity) { tip.hidden = true; return; }
  const index = Math.floor((timeFromMouse(ev) - activity.from) / activity.width);
  const b = activity.buckets.find((x) => x.b === index);
  const begin = activity.from + index * activity.width;
  const lines = [`<b>${fmt(begin)} – ${fmt(begin + activity.width, false)}</b>`];
  if (!b) lines.push('<span class="utc">ничего не происходило</span>');
  else {
    b.categories.forEach((n, i) => { if (n) lines.push(`<span style="color:${CATEGORIES[i].color}">■</span> ${CATEGORIES[i].name}: ${n}`); });
    const kinds = Object.entries(b.counts).filter(([k]) => !k.startsWith('@')).sort((a, c) => c[1] - a[1]).slice(0, 6);
    if (kinds.length) lines.push('<span class="utc">' + kinds.map(([k, n]) => `${esc(k)} ${n}`).join(', ') + '</span>');
    if (b.movement) lines.push(`<span class="utc">позиций записано: игроков ${b.counts['@players'] || 0}, гридов ${b.counts['@grids'] || 0}</span>`);
  }
  tip.innerHTML = lines.join('<br>');
  tip.hidden = false;
  const r = tip.getBoundingClientRect();
  tip.style.left = Math.min(ev.clientX + 12, window.innerWidth - r.width - 8) + 'px';
  tip.style.top = Math.max(4, ev.clientY - r.height - 12) + 'px';
});
timeline.addEventListener('mouseleave', () => { $('#tip').hidden = true; });
window.addEventListener('resize', () => loadActivity());

// ------------------------------------------------------------------ link to the current view

function saveHash() {
  const h = new URLSearchParams();
  h.set('from', state.from);
  h.set('to', state.to);
  h.set('t', Math.round(state.t));
  if (state.tracks.length) h.set('tracks', state.tracks.map((t) => t.key).join(','));
  if (state.selected) h.set('sel', state.selected);
  if (activeTab() && activeTab() !== 'moment') h.set('tab', activeTab());
  if (layer !== 'all') h.set('layer', layer);
  if (liveTimer) h.set('live', $('#liveEvery').value);
  history.replaceState(null, '', '#' + h.toString());
  // the inventories page opens on the same range and the selected player or grid
  const ledger = new URLSearchParams();
  ledger.set('from', state.from);
  ledger.set('to', state.to);
  const [kind, id] = (state.selected || '').split(':');
  if (id && (kind === 'player' || kind === 'grid')) { ledger.set('kind', kind); ledger.set('id', id); }
  $('#toLedger').href = 'ledger.html#' + ledger.toString();
}

async function start() {
  resize();
  const h = new URLSearchParams(location.hash.slice(1));
  const clock = await api('now');
  const now = clock.now;
  zoneOffset = (clock.offsetMinutes || 0) * 60_000;
  $('#zone').textContent = clock.zone || 'UTC';
  const to = Number(h.get('to')) || now;
  const from = Number(h.get('from')) || to - HOUR;
  state.from = from; state.to = to; state.t = Number(h.get('t')) || to;
  $('#from').value = toInput(from);
  $('#to').value = toInput(to);
  loadActivity();
  loadWorld();
  for (const key of (h.get('tracks') || '').split(',').filter(Boolean)) {
    const [kind, id] = key.split(':');
    await addTrack(kind, id);
  }
  if (h.get('sel') && findTrack(h.get('sel'))) select(h.get('sel'));
  if (h.get('tab')) showTab(h.get('tab'));
  if (h.get('layer')) setLayer(h.get('layer'));
  setTime(state.t);
  if (h.get('live')) {
    const every = h.get('live');
    if ([...$('#liveEvery').options].some((o) => o.value === every)) $('#liveEvery').value = every;
    setLive(true);
  }
  const days = await api('days');
  if (!state.tracks.length) status(days.length ? `Записи есть за ${days.length} дн. (последний ${days[0]}). Найдите игрока или грид.` : 'Записей пока нет.');
}

// ------------------------------------------------------------------ flying up to things, moving by keys

// a smooth flight of the camera to 100 m from a point, looking at it from where it looked before
let flight = null;
function flyTo(pos, keepFollow = false, distance = 100) {
  if (!keepFollow) $('#follow').checked = false;     // else the camera goes back to the selected one
  const dir = camera.position.clone().sub(controls.target);
  if (dir.lengthSq() < 1e-6) dir.set(1, 0.8, 1);
  dir.setLength(distance);
  flight = {
    t0: performance.now(), ms: 700,
    fromTarget: controls.target.clone(), fromCamera: camera.position.clone(),
    toTarget: pos.clone(), toCamera: pos.clone().add(dir),
  };
}
function fly(now) {
  if (!flight) return;
  const k = Math.min(1, (now - flight.t0) / flight.ms);
  const e = k < 0.5 ? 4 * k * k * k : 1 - (-2 * k + 2) ** 3 / 2;
  controls.target.lerpVectors(flight.fromTarget, flight.toTarget, e);
  camera.position.lerpVectors(flight.fromCamera, flight.toCamera, e);
  if (k >= 1) flight = null;
}

// W, A, S, D move the camera along the view, Space up, C down (on the screen); Shift is faster. The speed
// grows with the distance to what is looked at, so a ship and a planet both take a few seconds.
const MOVE_KEYS = ['KeyW', 'KeyA', 'KeyS', 'KeyD', 'Space', 'KeyC'];
const keysDown = new Set();
const typing = (el) => !!el?.matches && (el.matches('textarea, select, input:not([type=checkbox]):not([type=radio]):not([type=button])'));
window.addEventListener('keydown', (e) => {
  if (typing(e.target) || e.ctrlKey || e.altKey || e.metaKey) return;
  if (!MOVE_KEYS.includes(e.code)) return;
  e.preventDefault();                               // no page scroll, no button pressed by Space
  if (document.activeElement && document.activeElement !== document.body && !typing(document.activeElement)) document.activeElement.blur();
  keysDown.add(e.code);
});
window.addEventListener('keyup', (e) => keysDown.delete(e.code));
window.addEventListener('blur', () => keysDown.clear());
const moveV = new THREE.Vector3(), axisV = new THREE.Vector3();
function moveByKeys(dt) {
  if (!keysDown.size) return;
  moveV.set(0, 0, 0);
  const m = camera.matrixWorld;
  const add = (column, sign) => moveV.add(axisV.setFromMatrixColumn(m, column).multiplyScalar(sign));
  if (keysDown.has('KeyW')) add(2, -1);
  if (keysDown.has('KeyS')) add(2, 1);
  if (keysDown.has('KeyA')) add(0, -1);
  if (keysDown.has('KeyD')) add(0, 1);
  if (keysDown.has('Space')) add(1, 1);
  if (keysDown.has('KeyC')) add(1, -1);
  if (!moveV.lengthSq()) return;
  const fast = keysDown.has('Shift');
  const speed = Math.max(20, camera.position.distanceTo(controls.target)) * (fast ? 3 : 1);
  moveV.normalize().multiplyScalar(speed * Math.min(dt, 100) / 1000);
  camera.position.add(moveV);
  controls.target.add(moveV);
  $('#follow').checked = false;
  flight = null;
}
window.addEventListener('keydown', (e) => { if (e.key === 'Shift') keysDown.add('Shift'); });
window.addEventListener('keyup', (e) => { if (e.key === 'Shift') keysDown.delete('Shift'); });

// ------------------------------------------------------------------ loop

let last = performance.now();
let frameError = null;
function frame(now) {
  requestAnimationFrame(frame);     // first: a failing frame must not stop the next ones
  const dt = now - last;
  last = now;
  try {
    if (state.playing) {
      setTime(state.t + dt * state.speed, true);
      if (state.t >= state.to) { state.playing = false; $('#play').textContent = '▶'; setTime(state.t); }
    }
    fly(now);
    moveByKeys(dt);
    controls.update(dt);
    // the headlight a little above and behind the camera, shining where it looks
    headlight.position.copy(camera.position).add(camera.up.clone().multiplyScalar(camera.position.distanceTo(controls.target) * 0.3));
    headlight.target.position.copy(controls.target);
    updateScene();
    updatePatch(now);
    if (hoverEvent) { hover(hoverEvent); hoverEvent = null; }
    renderer.render(scene, camera);
  } catch (e) {
    if (String(e) !== frameError) { frameError = String(e); status('Ошибка отрисовки: ' + e.message, true); }
  }
}

start().catch((e) => status('Ошибка: ' + e.message, true));
requestAnimationFrame(frame);

// for debugging from the browser console
window.__watcher = { scene, sky, camera, controls, state, planetGroup };
