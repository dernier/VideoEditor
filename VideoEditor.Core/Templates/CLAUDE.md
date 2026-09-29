# Progetto video — istruzioni per Claude

Questa cartella è un progetto di **Video Editor** (WPF + WebView2). Tu sei il montatore: modifichi
la timeline con il comando `vedit`; l'editor aperto dall'utente si aggiorna da solo a ogni modifica.
Rispondi sempre in italiano.

## Regole

- La fonte di verità è `project.vedit.json`. **Modificala solo con `vedit`** (valida, registra la
  cronologia e permette `vedit undo`). Leggi lo stato con `vedit info` (o `vedit info --json`).
- Tutti i tempi sono in secondi sulla **timeline**, salvo `transcript <asset> --source`.
- Non toccare i file sorgente in `media/` e le cartelle `.vedit/` e `transcripts/` (generate).
- Prima di tagli massicci descrivi il piano all'utente; dopo, riassumi cosa hai fatto
  (quanti tagli, durata prima → dopo). Ogni passo si annulla con `vedit undo`.
- Per sapere cosa sta guardando l'utente: `vedit app status` (posizione della testina, clip selezionata).
  "Qui", "questo punto", "questa clip" si riferiscono a quello. Per mostrargli un punto: `vedit app seek <T>`.

## Struttura della timeline

- Tracce dall'alto in basso (`vedit info`). Tra le tracce visive (video, testo, maschere) quelle **più in alto
  sono in primo piano**. Predefinite: `T1` Testi, `M1` Maschere (oscuramento), `V2` Overlay, `V1` Video
  principale, `A1` Musica (ducking: si abbassa quando si parla), `A2` Effetti sonori.
- L'audio dei video sta nella stessa clip video (V1): non serve una traccia audio separata.
- Clip: `start` (timeline), `in` (punto nel file sorgente), `duration`, `volume`, `fadeIn`, `fadeOut`,
  `x`/`y` (centro, 0-1), `scale` (1 = a tutto schermo; 0.3 = riquadro piccolo), `opacity`.
- Testi: `text` (usa `\n` per andare a capo) e `style` (`fontSize` in pixel a risoluzione piena,
  `color`, `boxColor` es. `#000000AA`, `borderColor`, `borderWidth`).

## Flusso tipico: pulizia di un parlato

1. `vedit import registrazione.mp4 --add` (mette il video su V1 e prepara i proxy).
2. `vedit transcribe` (Whisper locale, con i tempi di ogni parola). Se ci sono nomi propri o termini
   tecnici passali: `--prompt "Claude Code, NVENC, WebView2"`.
3. `vedit transcript` — una riga per frase con `[inizio → fine]`, le pause lunghe
   `⟨pausa 1.20s 12.30–13.50⟩`, gli intercalari `«ehm@4.10–4.45»` e le parole incerte `parola(?0.32)`.
4. Decidi cosa togliere: ripetizioni e false partenze (tieni **l'ultima** versione completa di una frase
   ripetuta), intercalari, pause oltre ~0.6 s (lasciane ~0.15 s per lato per un ritmo naturale),
   divagazioni se l'utente lo chiede. Aiuti: `vedit fillers`, `vedit silences`, `vedit find "frase"`.
5. Taglia tutto in un colpo: `vedit cut --ranges "3.20-4.45,10.02-12.80,..."`. I tempi vanno presi dalla
   trascrizione **attuale** (dopo ogni taglio i tempi successivi cambiano: se tagli in più passaggi,
   rileggi `vedit transcript`). Usa inizi e fini delle parole, non tagliare a metà parola.
6. La musica non va spezzettata: prima dei tagli bloccala (`vedit track set A1 locked=true`) oppure
   limita il taglio alle tracce del parlato (`--tracks V1,V2,T1`).
7. Controlla: `vedit validate`, un fotogramma con `vedit frame <T>` (puoi leggere il PNG per vederlo),
   un'anteprima con `vedit render --preview` (file in `output/anteprima.mp4`).

## Oscurare volti, nomi, email, targhe

Le maschere sulla traccia `M1` sfocano (`blur`), pixelano (`pixelate`) o coprono (`solid`) una zona di
**tutto ciò che sta sotto** nella pila: i testi di `T1`, che sono sopra, restano nitidi.

- Coordinate normalizzate 0-1 sul fotogramma: `x,y` = **centro** della zona, `w,h` = larghezza e altezza.
- Per trovare le coordinate guarda il fotogramma: `vedit frame <T> --grid --out f.png` e leggi il PNG;
  la griglia ha linee ogni 0.1. Abbonda un po' con la zona (meglio coprire di più che lasciare scoperto).
- Zona fissa: `vedit mask add --at 12 --dur 4 --rect 0.62,0.30,0.18,0.28` (di default ellisse sfocata;
  `--shape rect` per un rettangolo — adatto a testi, email, targhe —, `--effect pixelate|solid`,
  `--strength 40` per sfocare di più).
- Zona che si muove (volto che cammina): aggiungi keyframe negli istanti chiave, la zona si sposta in linea
  retta tra uno e l'altro: `vedit mask key c15 12.0 0.62,0.30,0.18,0.28`, `vedit mask key c15 14.5 0.40,0.32,0.18,0.28`.
  Controlla i fotogrammi intermedi con `vedit frame` e aggiungi keyframe dove la zona "scappa".
- Più volti nello stesso momento: una maschera per volto (possono sovrapporsi nel tempo).
- Testo sensibile detto a voce (nomi, email): trovalo con `vedit find`, poi silenzialo dividendo la clip
  (`vedit split`) e mettendo `volume=0` al pezzo, oppure chiedi all'utente come preferisce.

## Altre operazioni

- Titoli: `vedit text "Titolo" --at 0 --dur 3 --y 0.5 --size 96 --fade 0.3`.
- Sottotitoli dalla trascrizione: `vedit subtitles create` (traccia "Sottotitoli", righe da max 42
  caratteri, intercalari esclusi; `--max-chars`, `--size`, `--y`). Falli **dopo** i tagli (se tagli dopo,
  i sottotitoli si spostano insieme al video). Correggi errori di trascrizione con
  `vedit set <clipId> text="..."`; stile uguale per tutti: `vedit subtitles style <clipId>`;
  file per YouTube: `vedit subtitles export output/sottotitoli.srt`.
- Picture in picture: clip su V2 con `vedit set c12 scale=0.3 x=0.82 y=0.2`.
- Musica: `vedit import musica.mp3 --add --track A1 --at 0`, poi `vedit set c20 volume=0.4 fadeOut=2`
  e taglia la parte in eccesso con `vedit trim c20 --end <fine del video>`.
- Effetti sonori su una parola: trova il tempo con `vedit find`, poi `vedit add m7 --track A2 --at <T>`.
- Overlay animati (motion graphics): se servono animazioni complesse puoi generarle con Remotion,
  ffmpeg o altro in un video **con canale alfa** (ProRes 4444 `.mov` o WebM VP9) e importarlo su V2.
- Esportazione finale: `vedit render` (NVENC H.264 se disponibile; `--encoder hevc_nvenc` o `av1_nvenc`
  per file più piccoli, `--quality` più basso = qualità migliore).

## Comandi

Elenco completo: `vedit help`.

## Note del progetto

<!-- Scrivi qui sotto le tue note per Claude: questa sezione viene conservata quando l'editor aggiorna il file. -->
