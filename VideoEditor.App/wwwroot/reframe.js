// Reinquadratura (come in CapCut): si mostra tutto il video sorgente con sopra la cornice del formato
// di uscita; ciò che resta fuori dalla cornice viene tagliato. La cornice si sposta e si ridimensiona
// (proporzioni bloccate); più grande del video significa video rimpicciolito con bande nere.
// Stessa matematica di VideoEditor.Core.Editing.Reframe.

import { S, op, assetOf, clipEnd, findClip, esc } from './state.js';

const $ = sel => document.querySelector(sel);

export function toTransform(fx, fy, fw, W, H, vw, vh) {
  const k = W / Math.max(1e-6, fw * vw);
  const dw = vw * k;
  return {
    scale: dw / Math.min(W, H * vw / vh),
    x: 0.5 + (0.5 - fx) * vw * k / W,
    y: 0.5 + (0.5 - fy) * vh * k / H,
  };
}

export function fromTransform(scale, x, y, W, H, vw, vh) {
  const dw = scale * Math.min(W, H * vw / vh);
  const k = dw / vw;
  return { fx: 0.5 - (x - 0.5) * W / (vw * k), fy: 0.5 - (y - 0.5) * H / (vh * k), fw: W / k / vw };
}

export class Reframer {
  constructor(player, toast) {
    this.player = player;
    this.toast = toast;
    this.root = $('#reframe');
    this.area = this.root.querySelector('.rf-area');
    this.video = $('#rfVideo');
    this.img = $('#rfImg');
    this.frameEl = $('#rfFrame');
    this.clipId = null;
    this.frame = null;   // { fx, fy, fw } durante il trascinamento
    this.view = null;    // { ds, ox, oy } mappa pixel del video → pixel dello schermo
    this.drag = null;

    this.root.querySelector('.rf-bar').addEventListener('click', e => {
      const b = e.target.closest('[data-rf]');
      if (!b) return;
      if (b.dataset.rf === 'done') this.close();
      else op({ op: 'reframe', clipId: this.clipId, mode: b.dataset.rf, all: $('#rfAll').checked });
    });
    this.frameEl.addEventListener('pointerdown', e => this._down(e));
    this.frameEl.addEventListener('pointermove', e => this._move(e));
    this.frameEl.addEventListener('pointerup', () => this._up());
    new ResizeObserver(() => this.isOpen && this.refresh()).observe(this.area);
  }

  get isOpen() { return !this.root.classList.contains('hidden'); }

  /** Clip da reinquadrare: quella selezionata, altrimenti il video più in alto sotto la testina. */
  pickClip() {
    for (const id of S.selection) {
      const f = findClip(id);
      if (f && f.track.kind === 'video' && assetOf(f.clip.assetId)?.width) return f.clip.id;
    }
    const t = this.player.t;
    for (const track of S.project?.tracks || []) {
      if (track.kind !== 'video' || track.hidden) continue;
      const c = track.clips.find(c => t >= c.start && t < clipEnd(c) && assetOf(c.assetId)?.width);
      if (c) return c.id;
    }
    return null;
  }

  open(clipId = this.pickClip()) {
    if (!clipId) {
      this.toast('Metti la linea rossa su un video (o selezionane uno) per reinquadrarlo.');
      return;
    }
    this.player.pause();
    this.clipId = clipId;
    this.root.classList.remove('hidden');
    this.refresh();
  }

  close() {
    this.root.classList.add('hidden');
    this.video.pause();
    this.clipId = null;
  }

  /** Ridisegna dopo un aggiornamento del progetto (o del riquadro). */
  refresh() {
    if (!this.isOpen) return;
    const f = findClip(this.clipId);
    const asset = f && assetOf(f.clip.assetId);
    if (!f || !asset?.width) { this.close(); return; }
    const { clip } = f;
    const W = S.project.settings.width, H = S.project.settings.height;
    $('#rfInfo').textContent = `${asset.name} · uscita ${W}×${H}`;

    // Sorgente: proxy del video al punto giusto (o l'immagine).
    const src = S.media[asset.id]?.proxy;
    const isImage = asset.kind === 'image';
    this.video.classList.toggle('hidden', isImage);
    this.img.classList.toggle('hidden', !isImage);
    const el = isImage ? this.img : this.video;
    if (src && el.dataset.src !== src) { el.src = src; el.dataset.src = src; }
    const thumb = S.media[asset.id]?.thumb;
    if (!isImage && thumb && this.video.poster !== thumb) this.video.poster = thumb;
    if (!isImage && this.video.readyState > 0) {
      const t = Math.min(Math.max(this.player.t, clip.start), clipEnd(clip) - 0.01);
      const want = clip.in + (t - clip.start);
      if (Math.abs(this.video.currentTime - want) > 0.04) this.video.currentTime = want;
    } else if (!isImage) {
      this.video.onloadedmetadata = () => this.refresh();
    }

    if (!this.drag) this.frame = fromTransform(clip.scale, clip.x, clip.y, W, H, asset.width, asset.height);
    this._layout(asset.width, asset.height, W / H, !this.drag);
  }

  _layout(vw, vh, outAspect, recomputeView) {
    const { fx, fy, fw } = this.frame;
    const fwPx = fw * vw, fhPx = fwPx / outAspect;
    const fl = fx * vw - fwPx / 2, ft = fy * vh - fhPx / 2;
    if (recomputeView || !this.view) {
      // Vista che contiene sia il video sia la cornice, con un po' di margine.
      const ux0 = Math.min(0, fl), uy0 = Math.min(0, ft), ux1 = Math.max(vw, fl + fwPx), uy1 = Math.max(vh, ft + fhPx);
      const aw = this.area.clientWidth, ah = this.area.clientHeight;
      const ds = Math.min(aw * 0.9 / (ux1 - ux0), ah * 0.9 / (uy1 - uy0));
      this.view = { ds, ox: (aw - (ux1 - ux0) * ds) / 2 - ux0 * ds, oy: (ah - (uy1 - uy0) * ds) / 2 - uy0 * ds };
    }
    const { ds, ox, oy } = this.view;
    for (const el of [this.video, this.img]) {
      el.style.left = ox + 'px';
      el.style.top = oy + 'px';
      el.style.width = vw * ds + 'px';
      el.style.height = vh * ds + 'px';
    }
    const st = this.frameEl.style;
    st.left = ox + fl * ds + 'px';
    st.top = oy + ft * ds + 'px';
    st.width = fwPx * ds + 'px';
    st.height = fhPx * ds + 'px';
    const crop = fw < 0.999 || fwPx / outAspect < vh * 0.999;
    this.frameEl.querySelector('.rf-label').textContent =
      `${S.project.settings.width}×${S.project.settings.height}` + (fw > 1.001 || fhPx > vh * 1.001 ? ' · bande nere' : crop ? ' · ritaglio' : '');
  }

  _asset() {
    const f = findClip(this.clipId);
    return f && assetOf(f.clip.assetId);
  }

  _down(e) {
    const asset = this._asset();
    if (!asset) return;
    e.preventDefault();
    this.frameEl.setPointerCapture(e.pointerId);
    const handle = e.target.classList.contains('h') ? [...e.target.classList].find(c => c.length === 2) : null;
    this.drag = { handle, x0: e.clientX, y0: e.clientY, f0: { ...this.frame }, vw: asset.width, vh: asset.height };
  }

  _move(e) {
    const d = this.drag;
    if (!d) return;
    const { ds } = this.view;
    const W = S.project.settings.width, H = S.project.settings.height, outAspect = W / H;
    const dxv = (e.clientX - d.x0) / ds, dyv = (e.clientY - d.y0) / ds; // spostamento in pixel del video
    let { fx, fy, fw } = d.f0;
    if (!d.handle) {
      fx += dxv / d.vw;
      fy += dyv / d.vh;
    } else {
      // Ridimensiona tenendo fermo l'angolo opposto, a proporzioni bloccate.
      const wPx = fw * d.vw, hPx = wPx / outAspect;
      const sx = d.handle.includes('w') ? -1 : 1, sy = d.handle.includes('n') ? -1 : 1;
      const ax = fx * d.vw - sx * wPx / 2, ay = fy * d.vh - sy * hPx / 2; // angolo fisso
      const px = fx * d.vw + sx * wPx / 2 + dxv, py = fy * d.vh + sy * hPx / 2 + dyv; // angolo trascinato
      let nw = Math.max(Math.abs(px - ax), Math.abs(py - ay) * outAspect);
      nw = Math.max(nw, d.vw * 0.03);
      const nh = nw / outAspect;
      fx = (ax + sx * nw / 2) / d.vw;
      fy = (ay + sy * nh / 2) / d.vh;
      fw = nw / d.vw;
    }
    this.frame = { fx, fy, fw };
    this._layout(d.vw, d.vh, outAspect, false);
    this._preview();
  }

  _up() {
    if (!this.drag) return;
    this.drag = null;
    const q = v => Math.round(v * 100000) / 100000;
    op({ op: 'reframe', clipId: this.clipId, fx: q(this.frame.fx), fy: q(this.frame.fy), fw: q(this.frame.fw), all: $('#rfAll').checked });
  }

  /** Mentre si trascina aggiorna subito anche l'anteprima dietro (senza aspettare il salvataggio). */
  _preview() {
    const f = findClip(this.clipId);
    const asset = this._asset();
    if (!f || !asset) return;
    const W = S.project.settings.width, H = S.project.settings.height;
    const t = toTransform(this.frame.fx, this.frame.fy, this.frame.fw, W, H, asset.width, asset.height);
    f.clip.scale = t.scale;
    f.clip.x = t.x;
    f.clip.y = t.y;
  }
}

/** Popola il menu dei formati e mostra quello attuale. */
export function fillAspectMenu(select, aspects) {
  if (!S.project || !aspects) return;
  const W = S.project.settings.width, H = S.project.settings.height, r = W / H;
  const current = aspects.find(a => Math.abs(a.ratio - r) < 0.01);
  select.innerHTML =
    `<option value="">Formato: ${esc(current ? current.id.replace('5.8in', '5,8"') : r.toFixed(2) + ':1')} (${W}×${H})</option>` +
    aspects.map(a => `<option value="${esc(a.id)}">${esc(a.label)}</option>`).join('') +
    '<option value="original">Originale (come il video)</option>';
}
