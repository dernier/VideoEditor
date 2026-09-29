# Video Editor (WPF + WebView2 + Claude Code)

Editor video multitraccia per Windows. L'interfaccia è una pagina web dentro un WebView2 ospitato
da WPF (.NET 10); il montaggio lo fanno **ffmpeg** (render, NVENC) e **Whisper** (trascrizione locale).
**Claude Code** partecipa al montaggio dalla riga di comando con lo strumento `vedit`.

L'idea viene dai flussi di lavoro "Claude Code come montatore" (es. `claude-youtube-editor`):
Claude non clicca su una timeline, **legge la trascrizione e modifica un file di progetto**; un
programma lo esegue con ffmpeg e l'utente controlla e corregge in un editor visuale. Qui l'editor
visuale e Claude lavorano sullo stesso file, `project.vedit.json`, in tempo reale.

```
┌──────────── VideoEditor.exe (WPF) ─────────────┐        ┌──────── Claude Code ────────┐
│ WebView2: timeline, anteprima, trascrizione,   │        │ claude -p (dal pannello chat)│
│           proprietà, chat                      │        │ oppure claude interattivo    │
│ EditorHost: operazioni, cronologia, watcher,   │◄─pipe──│ vedit app status / seek      │
│             proxy, render, Whisper, chat       │        │ vedit transcript / cut / ... │
└───────────────┬────────────────────────────────┘        └──────────────┬──────────────┘
                │                 project.vedit.json                     │
                └──────────────► (+ .vedit/history per annulla) ◄────────┘
```

## Struttura

| Progetto | Contenuto |
|---|---|
| `VideoEditor.Core` | Modello (progetto, tracce, clip, trascrizioni), operazioni di montaggio (dividi, rifila, elimina e ricompatta...), cronologia annulla/ripeti su disco, ffprobe/ffmpeg, costruzione del filtergraph, Whisper.net, sessione `claude -p` in stream-json, canale named pipe. |
| `VideoEditor.Cli` | `vedit`, lo strumento a riga di comando usato da Claude (e da te). |
| `VideoEditor.App` | L'editor WPF + WebView2 (`wwwroot/`: HTML/CSS/JS senza dipendenze esterne). |
| `tests/VideoEditor.Core.Tests` | Test xUnit del core. |

## Requisiti

- Windows 10/11 x64 con [runtime WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) (già presente su Windows 11).
- .NET 10 SDK (per compilare) o Desktop Runtime 10 (per eseguire).
- **ffmpeg** nel PATH: `winget install Gyan.FFmpeg` (oppure `ffmpeg.exe`/`ffprobe.exe` accanto a `VideoEditor.exe`, o le variabili `VEDIT_FFMPEG`/`VEDIT_FFPROBE`).
- **Claude Code**: `irm https://claude.ai/install.ps1 | iex` (o `npm i -g @anthropic-ai/claude-code`), con login già fatto.
- GPU NVIDIA (opzionale ma consigliata): render e proxy con NVENC; Whisper usa CUDA se è installato il CUDA Toolkit 12.8+ (necessario per la serie RTX 50), altrimenti Vulkan, altrimenti CPU.

## Compilare e avviare

```powershell
cd src\VideoEditor
dotnet build VideoEditor.sln
dotnet run --project VideoEditor.App            # oppure: ...\bin\Debug\net10.0-windows\VideoEditor.exe [cartella progetto]
dotnet publish VideoEditor.App -c Release -o C:\Tools\VideoEditor   # editor + vedit.exe nella stessa cartella
```

Per usare `vedit` da un terminale qualsiasi aggiungi la cartella di pubblicazione al PATH.
La CI su GitHub pubblica anche l'artefatto `video-editor-windows` pronto da scaricare.

## Uso dell'editor

1. **Nuovo** → scegli una cartella vuota. Viene creato `project.vedit.json` e un `CLAUDE.md` con le istruzioni per Claude.
2. **Importa** (o trascina i file nella finestra / direttamente su una traccia). Per ogni file vengono preparati in background proxy 720p H.264 (NVENC), miniatura e forma d'onda: così anche HEVC 10 bit, ProRes o 4K scorrono fluidi nell'anteprima. Il primo video messo su una timeline vuota imposta risoluzione e fps del progetto.
3. **Timeline**: trascina le clip (anche tra tracce compatibili), rifila i bordi, aggancio a testina, clip e marcatori. Doppio clic su una traccia testi crea un testo.
4. **Anteprima**: *Live* compone nel browser i proxy (immediata, approssimata); *⟳ Anteprima* renderizza con ffmpeg un'anteprima fedele (identica all'export) e passa in modalità *Render*; 📷 renderizza il fotogramma esatto sotto la testina.
5. **Trascrizione** (🎙, Whisper locale, modello `large-v3-turbo` scaricato la prima volta): clic su una parola per saltarci, Maiusc+clic per selezionare un tratto e tagliarlo, pause (⏸) cliccabili, intercalari evidenziati, pulsanti *Pause* ed *Ehm* per tagliarli in blocco.
6. **Oscurare** volti, nomi, email o targhe: **◯ Oscura** (ellisse, tasto B) o **▭ Oscura** (rettangolo,
   Maiusc+B), poi disegna la zona trascinando sul video: nasce una maschera di 3 s alla testina sulla
   traccia *Maschere*. A video fermo la sposti sul monitor e la ridimensioni dagli angoli; nel pannello
   Proprietà scegli sfocatura, pixel o riquadro pieno ed ellisse/rettangolo. Se il soggetto si muove, vai in un
   altro istante e sposta la zona: si crea un keyframe e la maschera lo segue. La maschera oscura tutto ciò che
   sta sotto di lei (i testi sopra restano nitidi). Anche Claude può farlo: *«sfoca il volto della persona a destra»*.
7. **Sottotitoli**: nella scheda *Trascrizione* **💬 Crea sottotitoli** genera la traccia *Sottotitoli*
   (righe bilanciate, max 42 caratteri — modificabile —, intercalari esclusi). Correggi il testo nel pannello
   Proprietà; cambia stile a uno e premi **Applica stile a tutta la traccia**; **Esporta SRT** per YouTube.
   Sono testi veri: finiscono anche "impressi" nel video esportato (nascondi la traccia per non averli).
8. **Esporta**: MP4 con NVENC (H.264, HEVC o AV1 dalle impostazioni ⚙), audio AAC; musica con ducking automatico sotto il parlato.

Tutte le operazioni si annullano con Ctrl+Z, anche quelle fatte da Claude o da riga di comando.

### Pulsanti

| Dove | Pulsante | Cosa fa |
|---|---|---|
| Barra in alto | Nuovo / Apri / Importa | nuovo progetto in una cartella, apre un progetto, importa file (anche trascinandoli) |
| | Live / Render | monitor in tempo reale (proxy, approssimato) o anteprima renderizzata (identica all'export) |
| | ⟳ Anteprima | renderizza con ffmpeg l'anteprima fedele e passa in modalità Render |
| | 📷 | fotogramma esatto alla testina, renderizzato da ffmpeg |
| | Terminale | prompt nella cartella del progetto con `vedit` nel PATH (per `claude` interattivo) |
| | Esporta | video finale MP4 (NVENC) |
| | ⚙ | encoder, modello Whisper, percorso e modello di Claude, verifica di ffmpeg/Claude |
| Barra della timeline | ↶ / ↷ | annulla / ripeti (anche le modifiche fatte da Claude) |
| | ✂ Dividi | taglia in due alla linea rossa le clip selezionate, o tutte quelle attraversate dalla linea |
| | ⇤ Elimina a sinistra | toglie la parte della clip prima della linea rossa e chiude il buco (il video riprende dalla linea) |
| | Elimina a destra ⇥ | toglie la parte della clip dopo la linea rossa e chiude il buco |
| | Elimina | toglie le clip selezionate (clic sulla clip: bordo giallo) lasciando il vuoto |
| | Elimina e chiudi | toglie le clip selezionate e chiude il buco su tutte le tracce non bloccate |
| | [ In · Out ] | segnano inizio e fine di un tratto alla linea rossa (evidenziato in azzurro) |
| | Taglia In/Out | taglia il tratto tra In e Out su tutte le tracce e chiude il buco |
| | T Testo | testo di 3 s alla testina |
| | ◯ Oscura / ▭ Oscura | disegna sul video una maschera ellittica / rettangolare |
| | ⚑ | marcatore alla testina (clic destro sul marcatore per toglierlo) |
| | + Traccia… | nuova traccia video, audio, testi o maschere |
| | aggancio, Zoom, ↔ | aggancio magnetico ai bordi; zoom della timeline; adatta alla finestra |
| Intestazione traccia | 👁 M 🔒 D ▲ ✕ | nascondi, muto, blocca (esclusa dai tagli), ducking, sposta su, elimina traccia |
| Pannello Media | + Importa, 🎙 Trascrivi | importa; trascrive con Whisper i video e gli audio del parlato |
| | + Timeline, 🎙, ✕ (su ogni file) | accoda il file alla traccia, trascrive solo quel file, lo rimuove dal progetto |
| Scheda Trascrizione | Pause, Ehm | trova pause lunghe / intercalari e li taglia tutti in blocco |
| | 💬 Crea sottotitoli, Esporta SRT | traccia Sottotitoli dalla trascrizione; file .srt |
| | Taglia, In/Out (su una selezione) | taglia le parole selezionate (Maiusc+clic) o le segna come In/Out |
| Monitor / trasporto | ⏮ ◂ ▶ ▸ ⏭, muto | inizio, fotogramma indietro, play/pausa, fotogramma avanti, fine; silenzia l'anteprima |
| Pannello Claude | chip, Invia, Interrompi, Nuova | richieste pronte, invia (Ctrl+Invio), ferma Claude, nuova conversazione |
| Pannello Proprietà | campi della clip | tempi, testo e stile, posizione, scala, opacità, volume, dissolvenze; maschere: effetto, forma, keyframe |
| | Applica stile a tutta la traccia | copia stile e posizione del testo selezionato su tutti i testi della traccia |

### Scorciatoie

| Tasto | Azione | Tasto | Azione |
|---|---|---|---|
| Spazio | play/pausa | S | dividi alla testina |
| ← / → | fotogramma prec./succ. (Maiusc: 1 s) | Canc | elimina clip (Maiusc: elimina e ricompatta) |
| I / O | segna In / Out | X | taglia l'intervallo In/Out |
| Q / W | elimina a sinistra / a destra della linea rossa | | |
| T | testo alla testina | M | marcatore |
| B / Maiusc+B | disegna maschera ellittica / rettangolare | F12 | strumenti di sviluppo (diagnosi) |
| Ctrl+Z / Ctrl+Y | annulla / ripeti | Ctrl+rotellina, + / − | zoom timeline |
| Home / Fine | inizio / fine | Esc | deseleziona |

## Claude

Due modi, sullo stesso progetto:

- **Pannello chat** dell'editor: ogni messaggio avvia `claude -p --output-format stream-json` nella cartella
  del progetto (la conversazione continua con `--resume`). Al messaggio viene aggiunto il contesto: posizione
  della testina, clip selezionate, intervallo In/Out. Claude è autorizzato solo a `vedit`, `ffprobe` e alla
  lettura/scrittura di file nella cartella. Le risposte e i comandi eseguiti compaiono nella chat, la timeline
  si aggiorna da sola.
- **Terminale** (pulsante in alto): prompt dei comandi nella cartella del progetto con `vedit` nel PATH; lì
  puoi lanciare `claude` in modo interattivo. Con `vedit app status` Claude sa cosa stai guardando, con
  `vedit app seek 42.5` ti porta in un punto.

Esempi di richieste: *«pulisci il parlato: togli pause oltre 0,6 s, ehm e ripetizioni, prima mostrami il piano»*,
*«aggiungi i sottotitoli»*, *«metti il logo in alto a destra per tutto il video»*, *«la musica deve partire a 0:05 con dissolvenza»*.

### `vedit` in breve

```text
vedit init | info | settings | validate | undo | redo | history
vedit import <file...> [--add] | probe | proxy
vedit add | text | move | trim | set | split | remove | cut | gap | track | marker
vedit mask add --at T --dur D --rect x,y,w,h [--shape rect] [--effect blur|pixelate|solid] | mask key <clip> <T> x,y,w,h
vedit subtitles create [--max-chars 42] | subtitles export file.srt | subtitles style <clip>
vedit transcribe | transcript [--words|--srt|--json] | find "frase" | fillers | silences
vedit render [--preview] [--encoder hevc_nvenc] | frame <T> [--grid]
vedit app status | seek <T> | play | pause | select <clip> | reload | say "msg"
```

`vedit help` mostra l'elenco completo; il `CLAUDE.md` creato in ogni progetto spiega a Claude flusso e regole.

## Formato del progetto

```jsonc
{
  "name": "Intervista",
  "settings": { "width": 3840, "height": 2160, "fps": 59.94, "language": "it" },
  "assets": [{ "id": "m1", "path": "media/clip.mp4", "kind": "video", "duration": 612.3, "hasAudio": true }],
  "tracks": [                       // dall'alto: le tracce visive più in alto sono in primo piano
    { "id": "T1", "kind": "text", "clips": [{ "id": "c9", "text": "Titolo", "start": 0, "duration": 3, "y": 0.85, "style": { "fontSize": 96 } }] },
    { "id": "V1", "kind": "video", "clips": [{ "id": "c1", "assetId": "m1", "start": 0, "in": 12.4, "duration": 30.2 }] },
    { "id": "A1", "kind": "audio", "duck": true, "volume": 0.5, "clips": [] }
  ],
  "markers": []
}
```

Cartelle: `media/` (file copiati con `--copy`), `transcripts/` (parole con tempi, per asset), `output/`
(anteprime ed export), `.vedit/` (proxy, cache, cronologia: rigenerabile).

## Note tecniche

- Il render costruisce un unico filtergraph: sfondo, sovrapposizione delle tracce dal basso verso l'alto
  (scala, posizione, opacità, dissolvenze), `drawtext` per i testi, mix audio con `sidechaincompress` per il ducking.
  Le clip contigue dello stesso file (tipico dopo i tagli delle pause) usano **un solo decoder** con `select`
  su segmenti allineati ai fotogrammi, e l'audio corrispondente viene tagliato sugli stessi confini: centinaia
  di tagli non aprono centinaia di decoder 4K e l'audio resta in sincrono.
- Se NVENC non è disponibile il render ripiega automaticamente su libx264/libx265.
- Overlay animati (motion graphics, es. fatti con Remotion) si importano come video con canale alfa
  (ProRes 4444 o WebM VP9) su una traccia video superiore.
- L'anteprima *Live* non applica il ducking e riproduce l'audio dal proxy: per il giudizio finale usa *⟳ Anteprima*.
