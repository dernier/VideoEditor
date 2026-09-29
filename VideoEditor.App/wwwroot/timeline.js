// Timeline multitraccia: righello, tracce, clip con miniature e forme d'onda, trascinamento,
// rifilatura, aggancio, trascinamento di media dal pannello e di file da Esplora risorse.

import { S, op, assetOf, clipEnd, fmt, esc, sendWithFiles } from './state.js';

const ROW_H = { video: 58, audio: 46, text: 44, mask: 44 };
const MASK_LANE = 22;
const EDGE = 7;
const MASK_LABEL = { blur: 'Sfocatura', pixelate: 'Pixel', solid: 'Riquadro' };

export class Timeline {
  constructor({ onSeek, onSelectionChange }) {
    this.onSeek = onSeek;
    this.onSelectionChange = onSelectionChange;
    this.scroll = document.getElementById('tlScroll');
    this.inner = document.getElementById('tlInner');
    this.rows = document.getElementById('rows');
    this.ruler = document.getElementById('ruler');
    this.rulerCanvas = document.getElementById('rulerCanvas');
    this.playhead = document.getElementById('playhead');
    this.inout = document.getElementById('inout');
    this.snapLine = document.getElementById('snapLine');
    this.zoomInput = document.getElementById('zoom');
    this.headW = 170;
    this.zoom = 60; // pixel al secondo
    this.t = 0;
    this.drag = null;
    this._wavePending = false;

    this.zoomInput.addEventListener('input', () => this.setZoom(sliderToZoom(+this.zoomInput.value), true));
    this.scroll.addEventListener('scroll', () => { this.drawRuler(); this.scheduleWaves(); });
    this.scroll.addEventListener('wheel', e => {
      if (e.ctrlKey) {
        e.preventDefault();
        this.setZoom(this.zoom * (e.deltaY < 0 ? 1.15 : 1 / 1.15), true, e.clientX);
      } else if (e.shiftKey) {
        e.preventDefault();
        this.scroll.scrollLeft += e.deltaY;
      }
    }, { passive: false });
    new ResizeObserver(() => { this.drawRuler(); this.scheduleWaves(); }).observe(this.scroll);

    this.ruler.addEventListener('pointerdown', e => this._scrubStart(e));
    this.rows.addEventListener('pointerdown', e => this._pointerDown(e));
    window.addEventListener('pointermove', e => this._pointerMove(e));
    window.addEventListener('pointerup', e => this._pointerUp(e));
    this.rows.addEventListener('dblclick', e => this._dblClick(e));
    this.rows.addEventListener('dragover', e => this._dragOver(e));
    this.rows.addEventListener('dragleave', e => e.target.closest?.('.lane')?.classList.remove('drop'));
    this.rows.addEventListener('drop', e => this._drop(e));
  }

  x(t) { return this.headW + t * this.zoom; }
  timeAt(clientX) {
    const r = this.inner.getBoundingClientRect();
    return Math.max(0, (clientX - r.left - this.headW) / this.zoom);
  }

  setZoom(z, keepCursor = false, clientX = null) {
    const old = this.zoom;
    z = Math.max(2, Math.min(1500, z));
    const view = this.scroll.getBoundingClientRect();
    const anchorX = clientX ?? (keepCursor ? this.x(this.t) - this.scroll.scrollLeft + view.left : view.left + this.headW);
    const anchorT = this.timeAt(anchorX);
    this.zoom = z;
    this.zoomInput.value = zoomToSlider(z);
    this.render();
    if (old !== z) this.scroll.scrollLeft = Math.max(0, this.x(anchorT) - (anchorX - view.left));
  }

  fit() {
    const w = this.scroll.clientWidth - this.headW - 40;
    this.setZoom(w / Math.max(5, S.duration));
    this.scroll.scrollLeft = 0;
  }

  // ---------- Disegno ----------

  render() {
    const p = S.project;
    this.rows.innerHTML = '';
    if (!p) return;
    const width = this.x(Math.max(S.duration + 30, 60)) + 200;
    this.inner.style.width = width + 'px';

    for (const track of p.tracks) {
      const row = document.createElement('div');
      row.className = `trow kind-${track.kind}` + (track.hidden || track.muted ? ' dim' : '');
      // Le maschere possono sovrapporsi (più volti insieme): si dispongono su sotto-righe.
      const lanes = track.kind === 'mask' ? assignLanes(track.clips) : null;
      const laneCount = lanes ? Math.max(1, ...lanes.values()) : 1;
      row.style.height = (lanes && laneCount > 1 ? Math.max(ROW_H.mask, laneCount * MASK_LANE + 6) : ROW_H[track.kind]) + 'px';
      row.dataset.track = track.id;
      row.innerHTML = `
        <div class="thead">
          <div class="tname" title="Doppio clic per rinominare">${esc(track.name)} <small>${track.id}</small></div>
          <div class="tbtns">
            ${track.kind !== 'audio' ? `<button data-t="hidden" class="${track.hidden ? 'on' : ''}" title="Nascondi">👁</button>` : ''}
            ${track.kind !== 'text' && track.kind !== 'mask' ? `<button data-t="muted" class="${track.muted ? 'on' : ''}" title="Muto">M</button>` : ''}
            <button data-t="locked" class="${track.locked ? 'on' : ''}" title="Blocca (esclusa dai tagli)">🔒</button>
            ${track.kind === 'audio' ? `<button data-t="duck" class="${track.duck ? 'on' : ''}" title="Ducking: si abbassa quando si parla">D</button>` : ''}
            <button data-t="up" title="Sposta su">▲</button>
            <button data-t="remove" title="Elimina traccia">✕</button>
          </div>
        </div>
        <div class="lane"></div>`;
      const lane = row.querySelector('.lane');
      for (const clip of track.clips) {
        const el = this._clipEl(clip, track);
        if (lanes && laneCount > 1) {
          el.style.top = 3 + (lanes.get(clip.id) - 1) * MASK_LANE + 'px';
          el.style.bottom = 'auto';
          el.style.height = MASK_LANE - 2 + 'px';
        }
        lane.appendChild(el);
      }
      row.querySelector('.tbtns').addEventListener('click', e => this._trackButton(e, track));
      row.querySelector('.tname').addEventListener('dblclick', () => {
        const name = prompt('Nome della traccia', track.name);
        if (name) op({ op: 'setTrack', trackId: track.id, patch: { name } });
      });
      this.rows.appendChild(row);
    }
    this._markers();
    this.setTime(this.t);
    this.setInOut();
    this.drawRuler();
    this.scheduleWaves();
  }

  _clipEl(clip, track) {
    const asset = clip.assetId ? assetOf(clip.assetId) : null;
    const kind = clip.mask ? 'mask' : clip.text != null ? 'text' : asset?.kind === 'image' ? 'image' : track.kind === 'audio' ? 'audio' : 'video';
    const el = document.createElement('div');
    el.className = `clip ${kind}` + (S.selection.has(clip.id) ? ' sel' : '');
    el.dataset.clip = clip.id;
    el.style.left = clip.start * this.zoom + 'px';
    el.style.width = Math.max(2, clip.duration * this.zoom) + 'px';
    const label = clip.mask ? `${MASK_LABEL[clip.mask.effect] || 'Maschera'}${clip.mask.keyframes?.length ? ` · ${clip.mask.keyframes.length} keyframe` : ''}`
      : clip.text != null ? clip.text.replace(/\n/g, ' ') : asset ? asset.name : '?';
    el.title = `${clip.id} · ${label}\n${fmt(clip.start)} → ${fmt(clipEnd(clip))} (${clip.duration.toFixed(2)} s)` + (clip.note ? `\n${clip.note}` : '');
    const thumb = asset && S.media[asset.id]?.thumb;
    if (thumb && (kind === 'video' || kind === 'image')) el.style.backgroundImage = `url("${thumb}")`;
    el.innerHTML = `<div class="lbl">${esc(label)}</div><div class="edge l"></div><div class="edge r"></div>`;
    if (clip.fadeIn > 0) el.insertAdjacentHTML('beforeend', `<div class="fade in" style="width:${clip.fadeIn * this.zoom}px"></div>`);
    if (clip.fadeOut > 0) el.insertAdjacentHTML('beforeend', `<div class="fade out" style="width:${clip.fadeOut * this.zoom}px"></div>`);
    for (const k of clip.mask?.keyframes || []) {
      const x = (k.t - clip.in) * this.zoom;
      if (x >= -1 && x <= clip.duration * this.zoom + 1) el.insertAdjacentHTML('beforeend', `<div class="kf" style="left:${x}px" title="keyframe ${fmt(clip.start + k.t - clip.in)}"></div>`);
    }
    if (asset?.hasAudio) {
      const cv = document.createElement('canvas');
      cv.className = 'wave';
      el.appendChild(cv);
    }
    return el;
  }

  _markers() {
    this.ruler.querySelectorAll('.marker').forEach(m => m.remove());
    for (const m of S.project?.markers || []) {
      const el = document.createElement('div');
      el.className = 'marker';
      el.style.left = this.x(m.time) + 'px';
      el.title = `${m.label || m.id} — ${fmt(m.time)} (clic destro per eliminare)`;
      el.innerHTML = `<span>${esc(m.label || '')}</span>`;
      el.addEventListener('pointerdown', e => { e.stopPropagation(); this.onSeek(m.time); });
      el.addEventListener('contextmenu', e => { e.preventDefault(); op({ op: 'removeMarker', id: m.id }); });
      this.ruler.appendChild(el);
    }
  }

  setTime(t) {
    this.t = t;
    this.playhead.style.left = this.x(t) + 'px';
  }

  /** Mantiene la testina visibile durante la riproduzione. */
  follow() {
    const x = this.x(this.t) - this.scroll.scrollLeft;
    const w = this.scroll.clientWidth;
    if (x > w - 40 || x < this.headW) this.scroll.scrollLeft = this.x(this.t) - this.headW - 40;
  }

  setInOut() {
    const a = S.inPoint, b = S.outPoint;
    const show = a != null && b != null && b > a;
    this.inout.classList.toggle('hidden', !show);
    if (show) {
      this.inout.style.left = this.x(a) + 'px';
      this.inout.style.width = (b - a) * this.zoom + 'px';
    }
  }

  updateSelection() {
    this.rows.querySelectorAll('.clip').forEach(el => el.classList.toggle('sel', S.selection.has(el.dataset.clip)));
  }

  drawRuler() {
    const cv = this.rulerCanvas;
    const w = Math.max(10, this.scroll.clientWidth - this.headW);
    const dpr = window.devicePixelRatio || 1;
    cv.style.left = this.scroll.scrollLeft + this.headW + 'px';
    cv.style.width = w + 'px';
    cv.width = w * dpr;
    cv.height = 26 * dpr;
    const g = cv.getContext('2d');
    g.scale(dpr, dpr);
    g.fillStyle = '#1a1d23';
    g.fillRect(0, 0, w, 26);
    const t0 = this.scroll.scrollLeft / this.zoom;
    const steps = [1 / 30, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
    const major = steps.find(s => s * this.zoom >= 70) || 600;
    const minor = major / (major >= 60 ? 6 : major >= 1 ? 5 : 2);
    g.strokeStyle = '#3a4150';
    g.fillStyle = '#8b93a1';
    g.font = '10px Consolas, monospace';
    g.beginPath();
    const perMajor = Math.round(major / minor);
    for (let i = Math.floor(t0 / minor); i * minor < t0 + w / this.zoom; i++) {
      const t = i * minor;
      const x = Math.round((t - t0) * this.zoom) + 0.5;
      const isMajor = i % perMajor === 0;
      g.moveTo(x, isMajor ? 12 : 19);
      g.lineTo(x, 26);
      if (isMajor) g.fillText(fmt(t).replace(/\.00$/, ''), x + 3, 10);
    }
    g.stroke();
    if (S.inPoint != null) { g.fillStyle = '#4f8cff'; g.fillRect((S.inPoint - t0) * this.zoom - 1, 0, 2, 26); }
    if (S.outPoint != null) { g.fillStyle = '#4f8cff'; g.fillRect((S.outPoint - t0) * this.zoom - 1, 0, 2, 26); }
  }

  scheduleWaves() {
    if (this._wavePending) return;
    this._wavePending = true;
    requestAnimationFrame(() => { this._wavePending = false; this._drawWaves(); });
  }

  /** Disegna le forme d'onda solo per la parte visibile di ogni clip (le clip lunghe possono essere enormi). */
  _drawWaves() {
    if (!S.project) return;
    const viewL = this.scroll.scrollLeft;
    const viewR = viewL + this.scroll.clientWidth - this.headW;
    const dpr = window.devicePixelRatio || 1;
    for (const el of this.rows.querySelectorAll('.clip')) {
      const cv = el.querySelector('canvas.wave');
      if (!cv) continue;
      const found = findClipLocal(el.dataset.clip);
      if (!found) continue;
      const { clip } = found;
      const peaks = S.peaks[clip.assetId];
      const cl = clip.start * this.zoom, cr = cl + clip.duration * this.zoom;
      const l = Math.max(cl, viewL), r = Math.min(cr, viewR);
      if (!peaks || r <= l) { cv.style.display = 'none'; continue; }
      const w = Math.ceil(r - l);
      cv.style.display = '';
      cv.style.left = l - cl + 'px';
      cv.style.width = w + 'px';
      const h = cv.clientHeight || 20;
      cv.width = w * dpr;
      cv.height = h * dpr;
      const g = cv.getContext('2d');
      g.scale(dpr, dpr);
      g.fillStyle = el.classList.contains('audio') ? '#7fe0b0' : '#9fc0ff';
      const src0 = clip.in + (l - cl) / this.zoom;
      const perPx = 1 / this.zoom;
      const gain = Math.min(1, (clip.volume ?? 1));
      for (let x = 0; x < w; x++) {
        const a = Math.floor((src0 + x * perPx) * peaks.rate);
        const b = Math.max(a + 1, Math.floor((src0 + (x + 1) * perPx) * peaks.rate));
        let m = 0;
        for (let i = a; i < b && i < peaks.data.length; i++) if (peaks.data[i] > m) m = peaks.data[i];
        const ph = (m / 255) * h * gain;
        g.fillRect(x, h - ph, 1, ph);
      }
    }
  }

  // ---------- Interazione ----------

  _trackButton(e, track) {
    const b = e.target.closest('button');
    if (!b) return;
    const what = b.dataset.t;
    if (what === 'remove') {
      if (track.clips.length && !confirm(`Eliminare la traccia "${track.name}" e le sue ${track.clips.length} clip?`)) return;
      op({ op: 'removeTrack', trackId: track.id });
    } else if (what === 'up') {
      const i = S.project.tracks.indexOf(track);
      if (i > 0) op({ op: 'moveTrack', trackId: track.id, index: i - 1 });
    } else {
      op({ op: 'setTrack', trackId: track.id, patch: { [what]: !track[what] } });
    }
  }

  _scrubStart(e) {
    if (e.button !== 0 || e.target.closest('.marker')) return;
    this.drag = { mode: 'scrub' };
    this.onSeek(this.timeAt(e.clientX));
  }

  _pointerDown(e) {
    if (e.button !== 0 || e.target.closest('.thead')) return;
    const clipEl = e.target.closest('.clip');
    if (!clipEl) {
      // Clic su spazio vuoto: deseleziona e sposta la testina.
      if (!e.shiftKey) { S.selection.clear(); this.updateSelection(); this.onSelectionChange(); }
      this._scrubStart(e);
      return;
    }
    const id = clipEl.dataset.clip;
    const found = findClipLocal(id);
    if (!found) return;
    if (e.ctrlKey || e.shiftKey) {
      S.selection.has(id) ? S.selection.delete(id) : S.selection.add(id);
    } else if (!S.selection.has(id)) {
      S.selection.clear();
      S.selection.add(id);
    }
    this.updateSelection();
    this.onSelectionChange();
    if (found.track.locked) return;

    const r = clipEl.getBoundingClientRect();
    const mode = e.target.classList.contains('l') ? 'trimStart' : e.target.classList.contains('r') ? 'trimEnd'
      : e.clientX - r.left < EDGE ? 'trimStart' : r.right - e.clientX < EDGE ? 'trimEnd' : 'move';
    const group = mode === 'move'
      ? [...S.selection].map(findClipLocal).filter(f => f && !f.track.locked).map(f => ({ ...f, el: this.rows.querySelector(`[data-clip="${f.clip.id}"]`) }))
      : [{ ...found, el: clipEl }];
    this.drag = { mode, x0: e.clientX, y0: e.clientY, main: found, group, moved: false, dt: 0, targetTrack: found.track };
    e.preventDefault();
  }

  _pointerMove(e) {
    const d = this.drag;
    if (!d) return;
    if (d.mode === 'scrub') { this.onSeek(this.timeAt(e.clientX)); return; }
    const dx = e.clientX - d.x0;
    if (!d.moved && Math.abs(dx) < 3 && Math.abs(e.clientY - d.y0) < 3) return;
    d.moved = true;
    let dt = dx / this.zoom;
    const c = d.main.clip;
    const exclude = new Set(d.group.map(g => g.clip.id));
    if (d.mode === 'move') {
      const minStart = Math.min(...d.group.map(g => g.clip.start));
      dt = Math.max(dt, -minStart);
      const snapped = this._snap([c.start + dt, clipEnd(c) + dt], exclude);
      if (snapped != null) dt += snapped;
      // Traccia di destinazione (solo spostando una clip singola).
      const rowEl = document.elementFromPoint(e.clientX, e.clientY)?.closest('.trow');
      const target = rowEl && S.project.tracks.find(t => t.id === rowEl.dataset.track);
      d.targetTrack = d.group.length === 1 && target && compatible(target, c) ? target : d.main.track;
      for (const g of d.group) g.el.style.left = (g.clip.start + dt) * this.zoom + 'px';
      if (d.targetTrack !== d.main.track) this.rows.querySelector(`[data-track="${d.targetTrack.id}"] .lane`).appendChild(d.group[0].el);
      else if (d.group[0].el.parentElement !== this.rows.querySelector(`[data-track="${d.main.track.id}"] .lane`))
        this.rows.querySelector(`[data-track="${d.main.track.id}"] .lane`).appendChild(d.group[0].el);
    } else if (d.mode === 'trimStart') {
      const asset = c.assetId ? assetOf(c.assetId) : null;
      const still = c.text != null || asset?.kind === 'image';
      dt = Math.min(dt, c.duration - 0.05);
      if (!still) dt = Math.max(dt, -c.in);
      dt = Math.max(dt, -c.start);
      const snapped = this._snap([c.start + dt], exclude);
      if (snapped != null) dt += snapped;
      d.group[0].el.style.left = (c.start + dt) * this.zoom + 'px';
      d.group[0].el.style.width = (c.duration - dt) * this.zoom + 'px';
    } else if (d.mode === 'trimEnd') {
      const asset = c.assetId ? assetOf(c.assetId) : null;
      const still = c.text != null || asset?.kind === 'image';
      dt = Math.max(dt, 0.05 - c.duration);
      if (!still && asset) dt = Math.min(dt, asset.duration - c.in - c.duration);
      const snapped = this._snap([clipEnd(c) + dt], exclude);
      if (snapped != null) dt += snapped;
      d.group[0].el.style.width = (c.duration + dt) * this.zoom + 'px';
    }
    d.dt = dt;
  }

  _pointerUp() {
    const d = this.drag;
    this.drag = null;
    this.snapLine.classList.add('hidden');
    if (!d || d.mode === 'scrub' || !d.moved) return;
    const c = d.main.clip;
    const r3 = v => Math.round(v * 1000) / 1000;
    if (d.mode === 'move') {
      if (d.group.length === 1) {
        op({ op: 'moveClip', clipId: c.id, start: r3(c.start + d.dt), trackId: d.targetTrack.id });
      } else {
        op({ op: 'moveClips', moves: d.group.map(g => ({ clipId: g.clip.id, start: r3(g.clip.start + d.dt) })) });
      }
    } else if (d.mode === 'trimStart') {
      op({ op: 'trimClip', clipId: c.id, start: r3(c.start + d.dt) });
    } else {
      op({ op: 'trimClip', clipId: c.id, end: r3(clipEnd(c) + d.dt) });
    }
  }

  /** Aggancio a testina, bordi delle clip, marcatori e In/Out. Restituisce la correzione da applicare. */
  _snap(times, exclude) {
    if (!document.getElementById('chkSnap').checked) return null;
    const targets = [0, this.t];
    for (const tr of S.project.tracks) for (const c of tr.clips) if (!exclude.has(c.id)) targets.push(c.start, clipEnd(c));
    for (const m of S.project.markers) targets.push(m.time);
    if (S.inPoint != null) targets.push(S.inPoint);
    if (S.outPoint != null) targets.push(S.outPoint);
    const tol = 8 / this.zoom;
    let best = null;
    for (const t of times) for (const g of targets) {
      const diff = g - t;
      if (Math.abs(diff) < tol && (best == null || Math.abs(diff) < Math.abs(best.diff))) best = { diff, at: g };
    }
    this.snapLine.classList.toggle('hidden', !best);
    if (best) this.snapLine.style.left = this.x(best.at) + 'px';
    return best?.diff ?? null;
  }

  _dblClick(e) {
    const clipEl = e.target.closest('.clip');
    if (clipEl) {
      const f = findClipLocal(clipEl.dataset.clip);
      if (f?.clip.text != null) {
        const text = prompt('Testo', f.clip.text);
        if (text != null) op({ op: 'setClip', clipId: f.clip.id, patch: { text } });
      }
      return;
    }
    const lane = e.target.closest('.lane');
    const row = e.target.closest('.trow');
    const kind = row && S.project.tracks.find(t => t.id === row.dataset.track)?.kind;
    const start = Math.round(this.timeAt(e.clientX) * 100) / 100;
    if (lane && kind === 'text') op({ op: 'addText', trackId: row.dataset.track, start, text: 'Testo' });
    if (lane && kind === 'mask') op({ op: 'addMask', trackId: row.dataset.track, start, duration: 3 });
  }

  _dragOver(e) {
    const lane = e.target.closest('.lane');
    if (!lane) return;
    e.preventDefault();
    this.rows.querySelectorAll('.lane.drop').forEach(l => l !== lane && l.classList.remove('drop'));
    lane.classList.add('drop');
  }

  _drop(e) {
    const lane = e.target.closest('.lane');
    if (!lane) return;
    e.preventDefault();
    e.stopPropagation();
    lane.classList.remove('drop');
    document.body.classList.remove('dragging-files');
    const trackId = lane.parentElement.dataset.track;
    const at = Math.round(this.timeAt(e.clientX) * 1000) / 1000;
    const assetId = e.dataTransfer.getData('application/x-vedit-asset');
    if (assetId) {
      op({ op: 'addClip', assetId, trackId, start: at });
    } else if (e.dataTransfer.files.length) {
      sendWithFiles('importFiles', { add: true, trackId, at }, [...e.dataTransfer.files]);
    }
  }
}

function findClipLocal(id) {
  for (const track of S.project?.tracks || []) {
    const clip = track.clips.find(c => c.id === id);
    if (clip) return { track, clip };
  }
  return null;
}

/** Assegna a ogni clip la prima sotto-riga libera (1, 2, ...) in cui non si sovrappone ad altre. */
function assignLanes(clips) {
  const ends = [];
  const result = new Map();
  for (const c of [...clips].sort((a, b) => a.start - b.start)) {
    let i = ends.findIndex(e => e <= c.start + 1e-6);
    if (i < 0) { ends.push(0); i = ends.length - 1; }
    ends[i] = clipEnd(c);
    result.set(c.id, i + 1);
  }
  return result;
}

function compatible(track, clip) {
  if (clip.mask) return track.kind === 'mask';
  if (clip.text != null) return track.kind === 'text';
  if (track.kind === 'mask') return false;
  const a = assetOf(clip.assetId);
  if (track.kind === 'video') return a?.kind !== 'audio';
  if (track.kind === 'audio') return !!a?.hasAudio;
  return false;
}

// Lo slider dello zoom è logaritmico: 0 → 2 px/s, 100 → 1500 px/s.
function sliderToZoom(v) { return 2 * Math.pow(750, v / 100); }
function zoomToSlider(z) { return Math.round(100 * Math.log(z / 2) / Math.log(750)); }
