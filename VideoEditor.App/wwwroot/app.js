// Interfaccia dell'editor: collega timeline, anteprima, pannelli e chat all'host WPF.

import { S, send, op, onHost, sendWithFiles, assetOf, clipEnd, findClip, fps, fmt, esc } from './state.js';
import { Timeline } from './timeline.js';
import { Player } from './player.js';

const $ = sel => document.querySelector(sel);
const $$ = sel => [...document.querySelectorAll(sel)];

let lastUiTime = -1;
let lastStateSent = 0;
let trSel = null; // { a, b } indici di parole selezionate
let claudeBubble = null;

const player = new Player($('#stage'), (t, playing) => onTimeUpdate(t, playing));
const timeline = new Timeline({
  onSeek: t => seek(t),
  onSelectionChange: () => { renderInspector(); reportState(true); },
});

// ================= Messaggi dall'host =================

onHost(msg => {
  switch (msg.type) {
    case 'welcome': showWelcome(msg.recent || []); break;
    case 'project': onProject(msg); break;
    case 'media':
      S.media[msg.assetId] = msg.info;
      renderBin();
      player.invalidate();
      timeline.render();
      break;
    case 'waveform': {
      const bin = atob(msg.data);
      const data = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) data[i] = bin.charCodeAt(i);
      S.peaks[msg.assetId] = { rate: msg.rate, data };
      timeline.scheduleWaves();
      break;
    }
    case 'words': S.words = msg.words || []; renderTranscript(); break;
    case 'seek': seek(msg.time); break;
    case 'play': player.play(); break;
    case 'pause': player.pause(); break;
    case 'select':
      S.selection = new Set(msg.ids.filter(Boolean));
      timeline.updateSelection();
      renderInspector();
      break;
    case 'toast': toast(msg.text, msg.level); break;
    case 'task': updateTask(msg); break;
    case 'previewReady':
      S.preview = { url: msg.url, duration: msg.duration, from: msg.from || 0, stale: false };
      setMode('render');
      toast('Anteprima pronta.', 'ok');
      break;
    case 'frameReady': player.showFrame(msg.url, msg.time); break;
    case 'silences': showSilences(msg.ranges, msg.trackId); break;
    case 'claude': addChat(msg.kind, msg.text); break;
    case 'claudeState':
      $('#btnStop').classList.toggle('hidden', !msg.running);
      $('#btnSend').disabled = msg.running;
      if (!msg.running) { claudeBubble = null; $$('.typing').forEach(e => e.remove()); }
      else addChat('typing', '');
      break;
    case 'appSettings': S.appSettings = msg; fillSettings(); break;
  }
});

function onProject(msg) {
  const first = !S.project || S.dir !== msg.dir;
  S.project = msg.project;
  S.dir = msg.dir;
  S.duration = msg.duration;
  S.canUndo = msg.canUndo;
  S.canRedo = msg.canRedo;
  S.media = msg.media || {};
  if (first) { S.selection.clear(); S.inPoint = S.outPoint = null; S.preview = null; S.peaks = {}; }
  for (const id of [...S.selection]) if (!findClip(id)) S.selection.delete(id);
  if (S.preview) S.preview.stale = true;
  $('#welcome').classList.add('hidden');
  $('#projectPath').textContent = msg.dir;
  $('#durationText').textContent = fmt(S.duration);
  $('#btnUndo').disabled = !S.canUndo;
  $('#btnRedo').disabled = !S.canRedo;
  document.title = `${S.project.name} — Video Editor`;
  if (msg.external) flash('Progetto aggiornato');
  if (!msg.fontAvailable) console.warn('Nessun font trovato per drawtext');
  player.invalidate();
  timeline.render();
  if (first) setTimeout(() => timeline.fit(), 0);
  renderBin();
  renderInspector();
  if (player.t > S.duration) seek(S.duration);
}

// ================= Tempo e stato =================

function seek(t) {
  player.seek(t);
  timeline.setTime(player.t);
  reportState(true);
}

function onTimeUpdate(t, playing) {
  if (t !== lastUiTime) {
    lastUiTime = t;
    $('#timecode').textContent = fmt(t);
    timeline.setTime(t);
    if (playing) timeline.follow();
    highlightWord(t);
  }
  $('#btnPlay').textContent = playing ? '❚❚' : '▶';
  reportState(false);
}

function reportState(force) {
  const now = performance.now();
  if (!force && now - lastStateSent < 500) return;
  lastStateSent = now;
  send('state', { time: player.t, playing: player.playing, selection: [...S.selection], in: S.inPoint, out: S.outPoint });
}

function setInOut(a, b) {
  S.inPoint = a;
  S.outPoint = b;
  if (a != null && b != null && b < a) [S.inPoint, S.outPoint] = [b, a];
  $('#inOutText').textContent = S.inPoint != null || S.outPoint != null
    ? `In ${S.inPoint != null ? fmt(S.inPoint) : '–'} · Out ${S.outPoint != null ? fmt(S.outPoint) : '–'}` : '';
  timeline.setInOut();
  timeline.drawRuler();
  reportState(true);
}

function setMode(mode) {
  $$('#modeSwitch button').forEach(b => b.classList.toggle('on', b.dataset.mode === mode));
  player.setMode(mode);
}

// ================= Comandi =================

const commands = {
  newProject: () => send('newProject'),
  openProject: () => send('openProject'),
  import: () => send('import', { add: false }),
  undo: () => send('undo'),
  redo: () => send('redo'),
  split: () => op({ op: 'split', time: player.t, clipIds: [...S.selection] }),
  delete: () => S.selection.size && op({ op: 'remove', clipIds: [...S.selection], ripple: false }),
  rippleDelete: () => S.selection.size && op({ op: 'remove', clipIds: [...S.selection], ripple: true }),
  cutInOut: () => {
    if (S.inPoint == null || S.outPoint == null) return toast('Segna prima In (I) e Out (O).');
    op({ op: 'rippleDelete', from: S.inPoint, to: S.outPoint });
    const at = S.inPoint;
    setInOut(null, null);
    seek(at);
  },
  addText: () => op({ op: 'addText', start: player.t, duration: 3, text: 'Testo' }),
  addMarker: () => op({ op: 'addMarker', time: player.t, label: '' }),
  renderPreview: () => send('render', { preview: true }),
  frame: () => { player.pause(); send('frame', { time: player.t }); },
  export: () => send('render', { preview: false }),
  openTerminal: () => send('openTerminal'),
  settings: () => { fillSettings(); $('#settingsDlg').classList.remove('hidden'); },
  settingsClose: () => $('#settingsDlg').classList.add('hidden'),
  settingsSave: () => {
    send('appSettings', { settings: {
      encoder: $('#setEncoder').value,
      whisperModel: $('#setWhisper').value,
      claudePath: $('#setClaudePath').value.trim(),
      claudeModel: $('#setClaudeModel').value.trim(),
    } });
    $('#settingsDlg').classList.add('hidden');
  },
  toStart: () => seek(0),
  toEnd: () => seek(S.duration),
  frameBack: () => { player.pause(); seek(player.t - 1 / fps()); },
  frameFwd: () => { player.pause(); seek(player.t + 1 / fps()); },
  playPause: () => player.toggle(),
  transcribeAll: () => send('transcribe', {}),
  silences: () => send('silences', {}),
  fillers: () => selectFillers(),
  trCut: () => cutTranscriptSelection(),
  trInOut: () => { if (trSel) { const r = selRange(); setInOut(r.from, r.to); } },
  trClear: () => { trSel = null; renderTrSelection(); },
  claudeSend: () => sendChat(),
  claudeStop: () => send('claudeStop'),
  claudeReset: () => { $('#chat').innerHTML = ''; send('claudeReset'); },
  addTrackVideo: () => op({ op: 'addTrack', kind: 'video' }),
  addTrackAudio: () => op({ op: 'addTrack', kind: 'audio' }),
  addTrackText: () => op({ op: 'addTrack', kind: 'text' }),
  zoomFit: () => timeline.fit(),
};

document.addEventListener('click', e => {
  const b = e.target.closest('[data-cmd]');
  if (b && commands[b.dataset.cmd]) { commands[b.dataset.cmd](); b.blur(); }
  const tab = e.target.closest('[data-tab]');
  if (tab) {
    const panel = tab.closest('.panel');
    panel.querySelectorAll('[data-tab]').forEach(t => t.classList.toggle('on', t === tab));
    panel.querySelectorAll('[data-tab-body]').forEach(body => body.classList.toggle('hidden', body.dataset.tabBody !== tab.dataset.tab));
  }
  const mode = e.target.closest('[data-mode]');
  if (mode) setMode(mode.dataset.mode);
  const chip = e.target.closest('[data-prompt]');
  if (chip) { $('#chatText').value = chip.dataset.prompt; $('#chatText').focus(); }
});

$('#chkMute').addEventListener('change', e => { player.muted = e.target.checked; });

// ================= Scorciatoie =================

document.addEventListener('keydown', e => {
  if (e.key === 'F12' || (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'i')) { e.preventDefault(); send('devtools'); return; }
  if (e.target.matches('input, textarea, select')) {
    if (e.target.id === 'chatText' && e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); sendChat(); }
    return;
  }
  const k = e.key;
  const ctrl = e.ctrlKey || e.metaKey;
  const map = {
    ' ': () => player.toggle(),
    'k': () => player.pause(),
    's': () => commands.split(),
    'Delete': () => e.shiftKey ? commands.rippleDelete() : commands.delete(),
    'Backspace': () => e.shiftKey ? commands.rippleDelete() : commands.delete(),
    'ArrowLeft': () => { player.pause(); seek(player.t - (e.shiftKey ? 1 : 1 / fps())); },
    'ArrowRight': () => { player.pause(); seek(player.t + (e.shiftKey ? 1 : 1 / fps())); },
    'Home': () => seek(0),
    'End': () => seek(S.duration),
    'i': () => setInOut(player.t, S.outPoint),
    'o': () => setInOut(S.inPoint, player.t),
    'x': () => commands.cutInOut(),
    'm': () => commands.addMarker(),
    't': () => commands.addText(),
    '+': () => timeline.setZoom(timeline.zoom * 1.3, true),
    '-': () => timeline.setZoom(timeline.zoom / 1.3, true),
    'Escape': () => { S.selection.clear(); timeline.updateSelection(); renderInspector(); setInOut(null, null); },
  };
  if (ctrl && k.toLowerCase() === 'z') { e.preventDefault(); send(e.shiftKey ? 'redo' : 'undo'); return; }
  if (ctrl && k.toLowerCase() === 'y') { e.preventDefault(); send('redo'); return; }
  if (ctrl && k.toLowerCase() === 'a') {
    e.preventDefault();
    S.selection = new Set(S.project?.tracks.flatMap(t => t.clips.map(c => c.id)) || []);
    timeline.updateSelection();
    return;
  }
  if (ctrl) return;
  const fn = map[k] || map[k.toLowerCase()];
  if (fn) { e.preventDefault(); fn(); }
});

// ================= Media =================

function renderBin() {
  const bin = $('#bin');
  bin.innerHTML = '';
  for (const a of S.project?.assets || []) {
    const m = S.media[a.id] || {};
    const el = document.createElement('div');
    el.className = 'asset';
    el.draggable = true;
    el.title = `${a.id} · ${a.path}\n${a.kind}${a.width ? ` ${a.width}x${a.height}` : ''}${a.fps ? ` @ ${a.fps}` : ''}${a.codec ? ` · ${a.codec}` : ''}`;
    const icon = a.kind === 'audio' ? '♪' : a.kind === 'image' ? '🖼' : '🎬';
    el.innerHTML = `
      <div class="thumb" style="${m.thumb ? `background-image:url('${m.thumb}')` : ''}">
        ${m.thumb ? '' : icon}
        ${a.kind !== 'image' ? `<span class="badge">${fmt(a.duration)}</span>` : ''}
        ${m.transcribed ? '<span class="badge tr" title="Trascritto">T</span>' : ''}
      </div>
      <div class="prog" data-prog="${a.id}"></div>
      <div class="name">${esc(a.name)}</div>
      <div class="acts">
        <button data-a="add" title="Aggiungi alla fine della traccia">+ Timeline</button>
        ${a.hasAudio ? `<button data-a="tr" title="Trascrivi">🎙</button>` : ''}
        <button data-a="rm" title="Rimuovi dal progetto">✕</button>
      </div>`;
    el.addEventListener('dragstart', e => {
      e.dataTransfer.setData('application/x-vedit-asset', a.id);
      e.dataTransfer.effectAllowed = 'copy';
    });
    el.addEventListener('dblclick', () => op({ op: 'addClip', assetId: a.id, start: player.t }));
    el.querySelector('.acts').addEventListener('click', e => {
      const act = e.target.closest('button')?.dataset.a;
      if (act === 'add') op({ op: 'addClip', assetId: a.id });
      if (act === 'tr') send('transcribe', { assets: [a.id], force: m.transcribed === true && confirm('Trascrivere di nuovo?') });
      if (act === 'rm') op({ op: 'removeAsset', assetId: a.id });
    });
    bin.appendChild(el);
  }
}

// File trascinati da Esplora risorse sulla finestra (fuori dalla timeline): solo importazione.
let dragDepth = 0;
window.addEventListener('dragenter', e => {
  if (e.dataTransfer.types.includes('Files')) { dragDepth++; document.body.classList.add('dragging-files'); }
});
window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging-files'); } });
window.addEventListener('dragover', e => e.preventDefault());
window.addEventListener('drop', e => {
  e.preventDefault();
  dragDepth = 0;
  document.body.classList.remove('dragging-files');
  if (e.dataTransfer.files.length) sendWithFiles('importFiles', { add: false }, [...e.dataTransfer.files]);
});

// ================= Trascrizione =================

const FILLERS = new Set(['ehm', 'eh', 'ehh', 'ehhm', 'uhm', 'um', 'umm', 'uh', 'mh', 'mmh', 'mm', 'mmm', 'ah', 'ahm', 'hmm', 'boh']);
const bare = w => w.toLowerCase().replace(/[^\p{L}\p{N}']/gu, '');

function renderTranscript() {
  const box = $('#transcript');
  const words = S.words;
  if (!words.length) {
    box.innerHTML = '<p class="muted">Nessuna trascrizione sulla timeline. Premi «🎙 Trascrivi» nel pannello Media.</p>';
    return;
  }
  const q = bare($('#trSearch').value || '');
  const html = [];
  let para = [];
  const flush = () => { if (para.length) html.push(`<p>${para.join('')}</p>`); para = []; };
  for (let i = 0; i < words.length; i++) {
    const w = words[i];
    const prev = words[i - 1];
    const gap = prev ? w.s - prev.e : 0;
    if (!prev || gap > 1.5) {
      flush();
      para.push(`<span class="ts" data-seek="${w.s}">${fmt(w.s)}</span>`);
    }
    if (prev && gap >= 0.5) para.push(`<span class="pause" data-pa="${prev.e}" data-pb="${w.s}" title="Clic: segna come In/Out · doppio clic: taglia">⏸ ${gap.toFixed(1)}s</span> `);
    const cls = ['w'];
    if (FILLERS.has(bare(w.t))) cls.push('filler');
    if (w.p < 0.4) cls.push('low');
    if (q && bare(w.t).includes(q)) cls.push('hit');
    if (trSel && i >= Math.min(trSel.a, trSel.b) && i <= Math.max(trSel.a, trSel.b)) cls.push('sel');
    para.push(`<span class="${cls.join(' ')}" data-i="${i}">${esc(w.t)}</span> `);
    if (/[.?!]$/.test(w.t)) { flush(); }
  }
  flush();
  box.innerHTML = html.join('');
  lastWordIdx = -1;
  renderTrSelection();
}

$('#trSearch').addEventListener('input', () => renderTranscript());

$('#transcript').addEventListener('click', e => {
  const w = e.target.closest('.w');
  if (w) {
    const i = +w.dataset.i;
    if (e.shiftKey && trSel) trSel.b = i;
    else if (e.shiftKey) trSel = { a: i, b: i };
    else { trSel = null; seek(S.words[i].s); }
    if (e.shiftKey) renderTranscript();
    else renderTrSelection();
    return;
  }
  const ts = e.target.closest('[data-seek]');
  if (ts) seek(+ts.dataset.seek);
  const pause = e.target.closest('.pause');
  if (pause) { setInOut(+pause.dataset.pa, +pause.dataset.pb); seek(+pause.dataset.pa); }
});

$('#transcript').addEventListener('dblclick', e => {
  const pause = e.target.closest('.pause');
  if (pause) {
    // Taglia la pausa lasciando 0,15 s per lato.
    const a = +pause.dataset.pa + 0.15, b = +pause.dataset.pb - 0.15;
    if (b > a) op({ op: 'rippleDelete', from: a, to: b });
  }
});

function selRange() {
  const a = Math.min(trSel.a, trSel.b), b = Math.max(trSel.a, trSel.b);
  const from = S.words[a].s;
  const next = S.words[b + 1];
  // Fino all'inizio della parola successiva, se è vicina: niente respiro spezzato.
  const to = next && next.s - S.words[b].e < 0.6 ? next.s : S.words[b].e;
  return { from, to, count: b - a + 1 };
}

function renderTrSelection() {
  const box = $('#trSelection');
  box.classList.toggle('hidden', !trSel);
  if (!trSel) { $$('#transcript .w.sel').forEach(w => w.classList.remove('sel')); return; }
  const r = selRange();
  $('#trSelInfo').textContent = `${r.count} parole · ${fmt(r.from)} → ${fmt(r.to)} (${(r.to - r.from).toFixed(2)} s)`;
}

function cutTranscriptSelection() {
  if (!trSel) return;
  const r = selRange();
  op({ op: 'rippleDelete', from: r.from, to: r.to });
  trSel = null;
  seek(r.from);
}

let lastWordIdx = -1;
function highlightWord(t) {
  const words = S.words;
  if (!words.length) return;
  let lo = 0, hi = words.length - 1, idx = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (words[mid].s <= t) { idx = mid; lo = mid + 1; } else hi = mid - 1;
  }
  if (idx >= 0 && t > words[idx].e + 0.3) idx = -1;
  if (idx === lastWordIdx) return;
  document.querySelector('#transcript .w.cur')?.classList.remove('cur');
  lastWordIdx = idx;
  if (idx < 0) return;
  const el = document.querySelector(`#transcript .w[data-i="${idx}"]`);
  if (el) {
    el.classList.add('cur');
    if (player.playing && !$('[data-tab-body="transcript"]').classList.contains('hidden')) el.scrollIntoView({ block: 'nearest' });
  }
}

function selectFillers() {
  const ranges = [];
  S.words.forEach((w, i) => {
    if (!FILLERS.has(bare(w.t))) return;
    const next = S.words[i + 1];
    ranges.push([w.s, next && next.s - w.e < 0.3 ? next.s : w.e]);
  });
  showCutList(ranges, `${ranges.length} intercalari`);
}

function showSilences(ranges, trackId) {
  showCutList(ranges, `${ranges.length} pause su ${trackId}`);
  $('[data-tab="transcript"]').click();
}

function showCutList(ranges, title) {
  const box = $('#cutList');
  if (!ranges.length) { box.classList.add('hidden'); toast('Niente da tagliare.'); return; }
  const total = ranges.reduce((s, r) => s + r[1] - r[0], 0);
  box.innerHTML = `<div class="row"><b>${esc(title)}</b><span class="muted small">− ${total.toFixed(1)} s</span><div class="spacer"></div>
    <button data-x="cut" class="primary">Taglia tutte</button><button data-x="close">×</button></div>` +
    ranges.map(r => `<div class="item" data-s="${r[0]}"><span>${fmt(r[0])}</span><span class="muted">${(r[1] - r[0]).toFixed(2)} s</span></div>`).join('');
  box.classList.remove('hidden');
  box.onclick = e => {
    const x = e.target.closest('[data-x]')?.dataset.x;
    if (x === 'cut') {
      op({ op: 'cutRanges', ranges });
      box.classList.add('hidden');
    } else if (x === 'close') {
      box.classList.add('hidden');
    } else {
      const item = e.target.closest('.item');
      if (item) seek(+item.dataset.s);
    }
  };
}

// ================= Proprietà =================

function renderInspector() {
  const box = $('#inspector');
  if (!S.project) { box.innerHTML = ''; return; }
  const ids = [...S.selection];
  if (ids.length !== 1) {
    const st = S.project.settings;
    box.innerHTML = `
      <h3>Progetto</h3>
      ${field('Nome', `<input data-p="name" value="${esc(S.project.name)}">`)}
      ${field('Larghezza', `<input type="number" data-p="width" value="${st.width}" step="2">`)}
      ${field('Altezza', `<input type="number" data-p="height" value="${st.height}" step="2">`)}
      ${field('Fotogrammi/s', `<input type="number" data-p="fps" value="${st.fps}" step="0.001">`)}
      ${field('Sfondo', colorInput('backgroundColor', st.backgroundColor, 'p'))}
      ${field('Lingua', `<input data-p="language" value="${esc(st.language)}" title="Codice per Whisper (it, en, auto…)">`)}
      ${field('Font testi', `<input data-p="fontFile" value="${esc(st.fontFile || '')}" placeholder="C:\\Windows\\Fonts\\arialbd.ttf">`)}
      <p class="note">${ids.length > 1 ? `${ids.length} clip selezionate.` : 'Seleziona una clip per modificarne le proprietà.'}</p>`;
    box.querySelectorAll('[data-p]').forEach(inp => inp.addEventListener('change', () => {
      const k = inp.dataset.p;
      let v = inp.value;
      if (inp.type === 'number') v = +v;
      if (k === 'fontFile' && !v) v = null;
      op({ op: 'settings', patch: { [k]: v } });
    }));
    return;
  }
  const f = findClip(ids[0]);
  if (!f) { box.innerHTML = ''; return; }
  const c = f.clip;
  const asset = c.assetId ? assetOf(c.assetId) : null;
  const isText = c.text != null;
  const visual = f.track.kind !== 'audio';
  const st = c.style || {};
  box.innerHTML = `
    <h3>${isText ? 'Testo' : esc(asset?.name || '')} <span class="muted small">${c.id} · ${f.track.id}</span></h3>
    ${field('Inizio', num('start', c.start))}
    ${field('Durata', num('duration', c.duration))}
    ${!isText && asset?.kind !== 'image' ? field('Ingresso sorgente', num('in', c.in)) : ''}
    ${isText ? field('Testo', `<textarea data-k="text">${esc(c.text)}</textarea>`) : ''}
    ${isText ? field('Dimensione', `<input type="number" data-s="fontSize" value="${st.fontSize ?? 64}">`) : ''}
    ${isText ? field('Colore', colorInput('color', st.color || '#FFFFFF', 's')) : ''}
    ${isText ? field('Riquadro', colorInput('boxColor', st.boxColor || '', 's')) : ''}
    ${isText ? field('Contorno', `<input type="number" data-s="borderWidth" value="${st.borderWidth ?? 2}" step="0.5">`) : ''}
    ${visual ? field('Posizione X', range('x', c.x, 0, 1, 0.01)) : ''}
    ${visual ? field('Posizione Y', range('y', c.y, 0, 1, 0.01)) : ''}
    ${visual && !isText ? field('Scala', range('scale', c.scale, 0.05, 1.5, 0.01)) : ''}
    ${visual ? field('Opacità', range('opacity', c.opacity, 0, 1, 0.01)) : ''}
    ${asset?.hasAudio ? field('Volume', range('volume', c.volume, 0, 2, 0.01)) : ''}
    ${field('Dissolvenza in', num('fadeIn', c.fadeIn))}
    ${field('Dissolvenza out', num('fadeOut', c.fadeOut))}
    ${field('Nota', `<input data-k="note" value="${esc(c.note || '')}">`)}
    <div class="row"><button data-x="seek">Vai all'inizio</button><button data-x="inout">Segna In/Out</button></div>`;
  box.querySelectorAll('[data-k]').forEach(inp => inp.addEventListener('change', () => {
    let v = inp.value;
    if (inp.type === 'number' || inp.type === 'range') v = +v;
    if (inp.dataset.k === 'note' && !v) v = null;
    op({ op: 'setClip', clipId: c.id, patch: { [inp.dataset.k]: v } });
  }));
  box.querySelectorAll('[data-s]').forEach(inp => inp.addEventListener('change', () => {
    let v = inp.value;
    if (inp.type === 'number') v = +v;
    if (v === '') v = null;
    op({ op: 'setClip', clipId: c.id, patch: { style: { [inp.dataset.s]: v } } });
  }));
  box.querySelectorAll('input[type=color]').forEach(inp => inp.addEventListener('input', () => {
    inp.previousElementSibling.value = inp.value.toUpperCase();
  }));
  box.querySelectorAll('input[type=color]').forEach(inp => inp.addEventListener('change', () => {
    inp.previousElementSibling.dispatchEvent(new Event('change'));
  }));
  box.querySelector('[data-x="seek"]').onclick = () => seek(c.start);
  box.querySelector('[data-x="inout"]').onclick = () => setInOut(c.start, clipEnd(c));
}

const field = (label, input) => `<div class="field"><label>${label}</label>${input}</div>`;
const num = (k, v) => `<input type="number" data-k="${k}" value="${+(+v).toFixed(3)}" step="0.01" min="0">`;
const range = (k, v, min, max, step) => `<input type="range" data-k="${k}" value="${v}" min="${min}" max="${max}" step="${step}" title="${v}">`;
function colorInput(key, value, kind) {
  const hex = /^#[0-9a-f]{6}/i.test(value || '') ? value.slice(0, 7) : '#000000';
  return `<div class="color"><input type="text" data-${kind}="${key}" value="${esc(value || '')}" placeholder="#RRGGBB o #RRGGBBAA"><input type="color" value="${hex}"></div>`;
}

// ================= Claude =================

function sendChat() {
  const box = $('#chatText');
  const text = box.value.trim();
  if (!text) return;
  addChat('user', text);
  box.value = '';
  send('claude', { text });
}

function addChat(kind, text) {
  const chat = $('#chat');
  $$('.typing').forEach(e => e.remove());
  if (kind === 'text' && claudeBubble) {
    claudeBubble.innerHTML += '\n\n' + md(text);
  } else if (kind !== 'typing') {
    const el = document.createElement('div');
    el.className = 'msg ' + kind;
    el.innerHTML = kind === 'text' ? md(text) : esc(text);
    chat.appendChild(el);
    claudeBubble = kind === 'text' ? el : null;
  }
  if (!$('#btnStop').classList.contains('hidden') || kind === 'typing') {
    const t = document.createElement('div');
    t.className = 'msg system typing';
    t.textContent = 'Claude sta lavorando';
    chat.appendChild(t);
  }
  chat.scrollTop = chat.scrollHeight;
}

/** Markdown minimo: codice, grassetto, elenchi. */
function md(text) {
  let h = esc(text);
  h = h.replace(/```[\w-]*\n?([\s\S]*?)```/g, (_, c) => `<code style="display:block;white-space:pre-wrap">${c}</code>`);
  h = h.replace(/`([^`\n]+)`/g, '<code>$1</code>');
  h = h.replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>');
  h = h.replace(/^#{1,4} (.*)$/gm, '<b>$1</b>');
  return h;
}

// ================= Attività, notifiche, benvenuto, impostazioni =================

const tasks = new Map();
function updateTask(t) {
  const box = $('#tasks');
  let el = tasks.get(t.id);
  if (t.done) {
    el?.remove();
    tasks.delete(t.id);
    const bar = document.querySelector(`[data-prog="${t.id.replace('media-', '')}"]`);
    if (bar) bar.style.width = '0';
    return;
  }
  if (!el) {
    el = document.createElement('div');
    el.className = 'task';
    box.appendChild(el);
    tasks.set(t.id, el);
  }
  const indet = t.progress == null;
  el.innerHTML = `<span>${esc(t.label)}</span><div class="bar ${indet ? 'indet' : ''}"><div style="width:${indet ? 30 : Math.round(t.progress * 100)}%"></div></div>
    ${indet ? '' : `<span class="muted">${Math.round(t.progress * 100)}%</span>`}
    ${t.cancellable ? '<button title="Annulla">✕</button>' : ''}`;
  el.querySelector('button')?.addEventListener('click', () => send('cancelTask'));
  if (t.id.startsWith('media-')) {
    const bar = document.querySelector(`[data-prog="${t.id.slice(6)}"]`);
    if (bar) bar.style.width = Math.round((t.progress || 0) * 100) + '%';
  }
}

function toast(text, level = 'info') {
  const el = document.createElement('div');
  el.className = 'toast ' + level;
  el.textContent = text;
  $('#toasts').appendChild(el);
  setTimeout(() => el.remove(), level === 'error' ? 9000 : 4000);
}

let flashTimer;
function flash(text) {
  const b = $('#stageBadge');
  if (player.mode === 'render') return;
  b.textContent = text;
  b.classList.remove('hidden');
  clearTimeout(flashTimer);
  flashTimer = setTimeout(() => b.classList.add('hidden'), 1500);
}

function showWelcome(recent) {
  $('#welcome').classList.remove('hidden');
  $('#recent').innerHTML = recent.length ? '<span class="muted small">Recenti</span>' +
    recent.map(r => `<button data-recent="${esc(r)}" title="${esc(r)}">${esc(r)}</button>`).join('') : '';
  $$('#recent [data-recent]').forEach(b => b.addEventListener('click', () => send('openProject', { path: b.dataset.recent })));
}

function fillSettings() {
  const a = S.appSettings;
  if (!a) return;
  const s = a.settings;
  $('#setEncoder').innerHTML = a.encoders.map(e => `<option ${e === s.encoder ? 'selected' : ''}>${e}</option>`).join('');
  $('#setWhisper').innerHTML = a.whisperModels.map(m => `<option ${m === s.whisperModel ? 'selected' : ''}>${m}</option>`).join('');
  $('#setClaudePath').value = s.claudePath || '';
  $('#setClaudeModel').value = s.claudeModel || '';
  $('#setChecks').innerHTML =
    `ffmpeg: ${a.ffmpegFound ? '✅ trovato' : '❌ non trovato (winget install Gyan.FFmpeg)'}<br>` +
    `Claude Code: ${a.claudeFound ? '✅ trovato' : '❌ non trovato (irm https://claude.ai/install.ps1 | iex)'}`;
  if (!a.ffmpegFound) toast('ffmpeg non trovato: installalo (winget install Gyan.FFmpeg) e riavvia l\'editor.', 'error');
}

// ================= Layout =================

const splitter = $('#splitter');
splitter.addEventListener('pointerdown', e => {
  splitter.setPointerCapture(e.pointerId);
  const y0 = e.clientY;
  const h0 = $('#timeline').getBoundingClientRect().height;
  const move = ev => {
    const h = Math.max(140, Math.min(window.innerHeight - 260, h0 - (ev.clientY - y0)));
    document.body.style.setProperty('--tl-h', h + 'px');
    player.resize();
  };
  const up = () => { splitter.removeEventListener('pointermove', move); splitter.removeEventListener('pointerup', up); };
  splitter.addEventListener('pointermove', move);
  splitter.addEventListener('pointerup', up);
});
new ResizeObserver(() => player.resize()).observe($('#monitor'));

send('ready');
