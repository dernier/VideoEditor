// Stato condiviso dell'interfaccia e canale verso l'host WPF.

export const S = {
  project: null,
  dir: null,
  duration: 0,
  media: {},       // assetId -> { proxy, thumb, transcribed }
  peaks: {},       // assetId -> { rate, data: Uint8Array }
  words: [],       // parole sulla timeline { t, s, e, p, c }
  selection: new Set(),
  inPoint: null,
  outPoint: null,
  preview: null,   // { url, duration, from, stale }
  canUndo: false,
  canRedo: false,
  appSettings: null,
};

const wv = window.chrome?.webview;

export function send(type, data = {}) {
  if (wv) wv.postMessage({ type, ...data });
  else console.log('→ host', type, data);
}

export function sendWithFiles(type, data, files) {
  if (wv?.postMessageWithAdditionalObjects) wv.postMessageWithAdditionalObjects({ type, ...data }, files);
}

export function op(o) { send('op', { op: o }); }

export function onHost(handler) {
  wv?.addEventListener('message', e => handler(e.data));
}

export const assetOf = id => S.project?.assets.find(a => a.id === id);
export const clipEnd = c => c.start + c.duration;

export function findClip(id) {
  for (const track of S.project?.tracks || []) {
    const clip = track.clips.find(c => c.id === id);
    if (clip) return { track, clip };
  }
  return null;
}

export function fps() { return S.project?.settings.fps || 30; }

/** m:ss.cc oppure h:mm:ss.cc */
export function fmt(t) {
  t = Math.max(0, t || 0);
  const h = Math.floor(t / 3600), m = Math.floor(t / 60) % 60, s = Math.floor(t % 60), cs = Math.floor((t * 100) % 100);
  const p = n => String(n).padStart(2, '0');
  return h ? `${h}:${p(m)}:${p(s)}.${p(cs)}` : `${m}:${p(s)}.${p(cs)}`;
}

export function esc(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
