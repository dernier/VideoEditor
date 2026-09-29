// Anteprima: modalità "live" (compone nel browser i proxy delle clip attive: veloce ma approssimata)
// e modalità "render" (riproduce l'anteprima renderizzata da ffmpeg: identica all'esportazione).

import { S, assetOf, clipEnd, findClip, maskRectAt } from './state.js';

const PRELOAD = 1.5; // secondi di anticipo per preparare la clip successiva

export class Player {
  constructor(stage, onTime) {
    this.stage = stage;
    this.layersEl = stage.querySelector('#layers');
    this.previewEl = stage.querySelector('#previewVideo');
    this.frameEl = stage.querySelector('#frameImg');
    this.badge = stage.querySelector('#stageBadge');
    this.onTime = onTime;
    this.layers = new Map();
    this.t = 0;
    this.playing = false;
    this.mode = 'live';
    this.muted = false;
    this.frame = null;
    this._t0 = 0;
    this._p0 = 0;
    requestAnimationFrame(() => this._tick());
  }

  get duration() { return S.duration || 0; }

  play() {
    if (!S.project) return;
    if (this.t >= this.duration - 0.01) this.t = 0;
    this.playing = true;
    this._t0 = this.t;
    this._p0 = performance.now();
    this.frame = null;
    if (this.mode === 'render' && S.preview) this.previewEl.play().catch(() => {});
  }

  pause() {
    this.playing = false;
    this.previewEl.pause();
    for (const l of this.layers.values()) if (l.media && !l.media.paused) l.media.pause();
  }

  toggle() { this.playing ? this.pause() : this.play(); }

  seek(t) {
    this.t = Math.max(0, Math.min(t, this.duration));
    this._t0 = this.t;
    this._p0 = performance.now();
    if (this.frame && Math.abs(this.frame.time - this.t) > 1e-3) this.frame = null;
  }

  setMode(mode) {
    this.mode = mode;
    this.previewEl.classList.toggle('hidden', mode !== 'render');
    if (mode === 'render') this._loadPreview();
    else this.previewEl.pause();
  }

  _loadPreview() {
    if (!S.preview) return;
    if (this.previewEl.dataset.url !== S.preview.url) {
      this.previewEl.src = S.preview.url;
      this.previewEl.dataset.url = S.preview.url;
    }
  }

  showFrame(url, time) {
    this.frame = { url, time };
    this.frameEl.src = url;
  }

  resize() {
    if (!S.project) return;
    const mon = this.stage.parentElement;
    const W = S.project.settings.width, H = S.project.settings.height;
    const k = Math.min(mon.clientWidth / W, mon.clientHeight / H);
    this.stage.style.width = Math.floor(W * k) + 'px';
    this.stage.style.height = Math.floor(H * k) + 'px';
    this.stage.style.background = S.project.settings.backgroundColor || '#000';
    this.k = k;
  }

  /** Il progetto è cambiato: gli elementi verranno ricreati o aggiornati al prossimo fotogramma. */
  invalidate() {
    for (const [id, l] of this.layers) {
      const found = findClip(id);
      if (!found || l.src !== sourceFor(found.clip)) this._remove(id);
    }
    this.resize();
  }

  _tick() {
    requestAnimationFrame(() => this._tick());
    if (!S.project) return;
    if (this.playing) {
      if (this.mode === 'render' && S.preview && !this.previewEl.paused) {
        this.t = S.preview.from + this.previewEl.currentTime;
      } else {
        this.t = this._t0 + (performance.now() - this._p0) / 1000;
      }
      if (this.t >= this.duration) {
        this.t = this.duration;
        this.pause();
      }
    }
    this._render();
    this.onTime(this.t, this.playing);
  }

  _render() {
    const t = this.t;
    const showFrame = !!this.frame && !this.playing && Math.abs(this.frame.time - t) < 1e-3;
    this.frameEl.classList.toggle('hidden', !showFrame);

    if (this.mode === 'render') {
      this.previewEl.classList.remove('hidden');
      for (const id of [...this.layers.keys()]) this._remove(id);
      const p = S.preview;
      this.badge.classList.toggle('hidden', !!p && !p.stale);
      this.badge.textContent = p ? 'Anteprima non aggiornata: premi ⟳ Anteprima' : 'Nessuna anteprima: premi ⟳ Anteprima';
      if (!p) return;
      this._loadPreview();
      const local = t - p.from;
      this.previewEl.muted = this.muted;
      if (!this.playing) {
        if (!this.previewEl.paused) this.previewEl.pause();
        if (Math.abs(this.previewEl.currentTime - local) > 0.02 && !this.previewEl.seeking) this.previewEl.currentTime = Math.max(0, local);
      } else if (this.previewEl.paused && local >= 0 && local < p.duration) {
        this.previewEl.currentTime = local;
        this.previewEl.play().catch(() => {});
      }
      return;
    }

    this.badge.classList.add('hidden');
    const active = new Set();
    const tracks = S.project.tracks;
    let z = 1;
    // Dal basso verso l'alto: l'ultima traccia visiva della lista è lo sfondo.
    for (let ti = tracks.length - 1; ti >= 0; ti--) {
      const track = tracks[ti];
      const visual = track.kind !== 'audio';
      if (visual && track.hidden && track.muted) continue;
      for (const clip of track.clips) {
        const end = clipEnd(clip);
        if (t < clip.start - PRELOAD || t >= end) continue;
        const onNow = t >= clip.start;
        if (track.kind === 'audio' && track.muted) continue;
        active.add(clip.id);
        const layer = this._ensure(clip, track);
        this._update(layer, clip, track, t, onNow, z++);
      }
    }
    for (const id of [...this.layers.keys()]) if (!active.has(id)) this._remove(id);
  }

  _ensure(clip, track) {
    let l = this.layers.get(clip.id);
    if (l) return l;
    const asset = clip.assetId ? assetOf(clip.assetId) : null;
    const src = sourceFor(clip);
    let el, media = null;
    if (clip.mask) {
      el = document.createElement('div');
      el.className = 'maskLayer';
    } else if (clip.text != null) {
      el = document.createElement('div');
      el.className = 'txt';
    } else if (!src) {
      el = document.createElement('div');
      el.className = 'placeholder';
      el.textContent = asset ? `${asset.name}: proxy in preparazione…` : 'asset mancante';
    } else if (asset.kind === 'image') {
      el = document.createElement('img');
      el.src = src;
    } else if (asset.kind === 'audio' || track.kind === 'audio') {
      el = media = document.createElement('audio');
      el.preload = 'auto';
      el.src = src;
    } else {
      el = media = document.createElement('video');
      el.preload = 'auto';
      el.playsInline = true;
      el.src = src;
    }
    l = { el, media, src, kind: track.kind };
    if (media) {
      // Se il proxy non si carica lo si dice sul monitor, invece di restare neri.
      media.addEventListener('error', () => {
        const err = media.error;
        const msg = `Impossibile riprodurre ${asset?.name || ''}\n(codice ${err?.code ?? '?'}: ${err?.message || 'errore sconosciuto'})\n${src}`;
        console.error(msg);
        if (this.layers.get(clip.id) !== l) return;
        const ph = document.createElement('div');
        ph.className = 'placeholder';
        ph.style.whiteSpace = 'pre-wrap';
        ph.style.textAlign = 'center';
        ph.textContent = msg;
        media.replaceWith(ph);
        l.el = ph;
        l.media = null;
      });
    }
    this.layers.set(clip.id, l);
    this.layersEl.appendChild(el);
    return l;
  }

  _remove(id) {
    const l = this.layers.get(id);
    if (!l) return;
    if (l.media) { l.media.pause(); l.media.removeAttribute('src'); l.media.load(); }
    l.el.remove();
    this.layers.delete(id);
  }

  _update(l, clip, track, t, onNow, z) {
    const el = l.el;
    const k = this.k || 1;
    const W = S.project.settings.width, H = S.project.settings.height;
    const fade = fadeFactor(clip, t);
    const visible = onNow && track.kind !== 'audio' && !track.hidden;
    el.style.display = visible ? '' : 'none';
    el.style.zIndex = z;

    if (visible) {
      if (clip.mask) {
        // Anteprima live: backdrop-filter sfoca ciò che sta sotto nella pila (come nel render).
        const m = clip.mask;
        const r = S.maskDrag?.id === clip.id ? S.maskDrag.rect : maskRectAt(clip, t);
        const bw = r.w * W * k, bh = r.h * H * k;
        el.style.width = bw + 'px';
        el.style.height = bh + 'px';
        el.style.left = r.x * W * k - bw / 2 + 'px';
        el.style.top = r.y * H * k - bh / 2 + 'px';
        el.style.borderRadius = m.shape === 'ellipse' ? '50%' : '0';
        const strength = (m.strength || 30) * k;
        el.classList.toggle('pixelate', m.effect === 'pixelate');
        el.style.background = m.effect === 'solid' ? (m.color || '#000') : '';
        el.style.backdropFilter = m.effect === 'solid' ? '' : `blur(${Math.max(2, m.effect === 'pixelate' ? strength / 2 : strength / 2)}px)`;
        el.style.setProperty('--px', Math.max(3, strength) + 'px');
      } else if (clip.text != null) {
        const st = clip.style || {};
        el.style.left = clip.x * W * k + 'px';
        el.style.top = clip.y * H * k + 'px';
        el.style.fontSize = (st.fontSize || 64) * k + 'px';
        el.style.color = st.color || '#fff';
        el.style.opacity = (clip.opacity ?? 1) * fade;
        el.style.webkitTextStroke = st.borderWidth > 0 && st.borderColor ? `${st.borderWidth * 2 * k}px ${st.borderColor}` : '';
        const key = clip.text + '|' + (st.boxColor || '') + '|' + (st.boxPadding ?? 16) + '|' + k;
        if (el.dataset.key !== key) {
          el.dataset.key = key;
          el.innerHTML = '';
          clip.text.split('\n').forEach((line, i) => {
            if (i) el.appendChild(document.createElement('br'));
            const span = document.createElement('span');
            span.textContent = line;
            if (st.boxColor) {
              span.style.background = st.boxColor;
              span.style.padding = `0 ${(st.boxPadding ?? 16) * k}px`;
            }
            el.appendChild(span);
          });
        }
      } else {
        const asset = assetOf(clip.assetId) || {};
        const ar = asset.width && asset.height ? asset.width / asset.height : W / H;
        let bw = W * clip.scale * k, bh = H * clip.scale * k;
        if (ar > bw / bh) bh = bw / ar; else bw = bh * ar;
        el.style.width = bw + 'px';
        el.style.height = bh + 'px';
        el.style.left = clip.x * W * k - bw / 2 + 'px';
        el.style.top = clip.y * H * k - bh / 2 + 'px';
        el.style.opacity = (clip.opacity ?? 1) * fade;
      }
    }

    const m = l.media;
    if (!m) return;
    m.muted = this.muted || track.muted;
    m.volume = Math.max(0, Math.min(1, (clip.volume ?? 1) * (track.volume ?? 1) * fade));
    if (m.readyState === 0) return;
    const want = onNow ? clip.in + (t - clip.start) : clip.in;
    if (onNow && this.playing) {
      if (m.paused) m.play().catch(() => {});
      if (Math.abs(m.currentTime - want) > 0.25 && !m.seeking) m.currentTime = want;
    } else {
      if (!m.paused) m.pause();
      if (Math.abs(m.currentTime - want) > 0.02 && !m.seeking) m.currentTime = want;
    }
  }
}

function sourceFor(clip) {
  if (!clip.assetId) return null;
  return S.media[clip.assetId]?.proxy || null;
}

function fadeFactor(clip, t) {
  let f = 1;
  if (clip.fadeIn > 0) f = Math.min(f, (t - clip.start) / clip.fadeIn);
  if (clip.fadeOut > 0) f = Math.min(f, (clipEnd(clip) - t) / clip.fadeOut);
  return Math.max(0, Math.min(1, f));
}
