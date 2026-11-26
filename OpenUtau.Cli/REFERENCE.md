# OpenUtau.Cli reference

Options, session requests and result fields in full. The [README](README.md) has the short version.

## Options

For `phonemize` and `render`:

| Option | Meaning |
| --- | --- |
| `--singer DIR` or `N=DIR` | Voicebank folder for every track, or for track `N`. |
| `--phonemizer ID` or `N=ID` | Phonemizer by id (see `phonemizers`). A tag or name works when only one phonemizer for the track's singer has it. |
| `--bpm N` | Replace the tempo map with one tempo. |
| `--tempo-scale X` | Multiply every tempo by `X`; `2` plays the song twice as fast. |
| `--allow-errors` | Exit 0 even if some phonemes have errors. |

For `render`:

| Option | Meaning |
| --- | --- |
| `--out FILE.wav` | Required. One mono 16-bit 44.1 kHz file per track, as the app's *Export Wav*. With several tracks the files are named `FILE_<track name>.wav`; the result lists them. |
| `--mixdown` | One stereo file of all tracks instead, with volume, pan and mix effects, as *Export Mixdown*. |
| `--track N` | Render only track `N`. |
| `--seed N` | Fix the random numbers (0 to 4294967295). See [Reproducibility](#reproducibility). |
| `--load-pitch none\|untuned\|all` | Run *Load Rendered Pitch* before rendering. `all` is the menu command on every part. `untuned` only fills phrases that have no pitch curve yet, which is what the app does by itself when it opens a project with live pitch on. Default `none`: render the project as it is. |
| `--curve ABBR=VALUE` | Set an expression curve to one value for the whole track, replacing any drawn curve: `--curve velc=150 --curve genc=-20`. Repeatable. |
| `--dump DIR` | Save every model's inputs and outputs. See [Model dump](MODEL-DUMP.md). |
| `--runner cpu\|webgpu\|directml\|cuda` | ONNX runner, overriding the preference. |
| `--steps N`, `--steps-variance N`, `--steps-pitch N`, `--depth X` | Diffusion steps and depth, overriding the preferences. |

For every command:

| Option | Meaning |
| --- | --- |
| `--prefs gui\|default` | Take settings from the app's `prefs.json` (default) or from built-in defaults. |
| `--set Key=Value` | Override one preference by its name in `prefs.json`, e.g. `--set DiffSingerMergeOverlappingPhrases=true`. Repeatable. |
| `--cache-dir DIR` | Keep render caches in `DIR/openutau-cli/` and reuse them between runs. |
| `--log FILE`, `--verbose` | Write the log to a file, or to stderr. |
| `--pretty` | Indent the JSON. |

## Session requests

A request is one line, `{"op": name, ...fields}`; a reply is one line with `ok`, `op`, `revision`, `warnings`, `diagnostics`, the op's own fields, and `error: {code, message}` when `ok` is false. An `id` in the request is echoed. Unknown ops and unknown fields are refused, never ignored. `{"op":"help"}` returns the full list with every field; in short:

| Op | Does |
| --- | --- |
| `status` | project, tempo, tracks, parts, undo state; in the app also `ui`: the open part, selected note ids, playhead |
| `new`, `open`, `save` | replace the project from a score (as the `ustx` command takes), open a file, save as `.ustx` |
| `get` | notes with their ids and phonemes; `detail`: `notes`, `phonemes` (timing), `phrases` (what the renderer is given); `from_tick`/`to_tick`, `curves` |
| `add_notes`, `edit_notes`, `delete_notes` | `[[lyric, tone, duration], ...]` at a tick or after a note; `{id, lyric?, tone?, tick?, duration?}`; ids |
| `set_curve` | draw `points`, `clear`, or set the track `default` of an expression such as `pitd` (cents) or `genc`; `stored_points` in the reply counts what the curve keeps, after snapping to its grid |
| `set_tempo`, `set_track` | tempo; a track's singer and phonemizer (a new singer brings the phonemizer last used with it in the app, else its default, unless one is given) |
| `load_pitch` | write the model's predicted pitch into `pitd`, as *Load Rendered Pitch*; the default `mode` `untuned` skips phrases that already have a curve (`written` is then false), `all` rewrites every phrase and is the one to use after changing notes |
| `render` | wav of the project, or of `from_tick`..`to_tick` only (the file then starts at `from_tick`); under `serve`, warns for a part with no pitch curve, which is sung at plain note pitch until `load_pitch`, and for a part whose notes changed after its pitch was written; a range ends with the last sound before `to_tick` |
| `undo`, `redo` | the app's undo stack, the user's edits included; `undone` names the op when it was this session's latest edit, else null |
| `singers`, `phonemizers`, `expressions` | as the commands of the same name |
| `shutdown` | ends a `serve` session; refused in the app |

Ticks are 480 per quarter note from the start of the project. A note's `id` stays the same for as long as the project is loaded, through edits and undo.

`get` waits for the phonemizer, so what it returns is never stale. After a request that changes what is sung (`new`, `open`, `add_notes`, `edit_notes`, `delete_notes`, `set_track`, `set_tempo`, `undo`, `redo`) the reply carries `phoneme_errors`, the number of phonemes that cannot be sung, and a warning with the first of them.

`revision` goes up by one with every request that changes the project and with each change the user makes in the app, and not when nothing changed (a refused request, a phonemizer answer). A request that changes the project may carry `"expect_revision": N` and is then refused with code `conflict` if the project moved on since the reply that said `N`. A request that fails part-way is rolled back whole. Each editing request is one undo step, except `load_pitch`, which is one step per part as in the app. `open`, `save` and `render` take paths relative to the folder the session runs in; in the app they must be absolute.

Socket paths must be short (104 bytes on macOS). The socket file is readable and writable by its owner only.

## Results

### `phonemize`

```json
{
  "ok": true,
  "command": "phonemize",
  "project": {"path": "/abs/song.ustx", "name": "New Project", "resolution": 480,
              "tempos": [{"tick": 0, "bpm": 120}],
              "time_signatures": [{"bar": 0, "beat_per_bar": 4, "beat_unit": 4}]},
  "tracks": [{"index": 0, "name": "Track1", "singer_id": "Chiaki", "singer_name": "Chiaki v1.5",
              "singer_path": "/path/to/Chiaki", "singer_type": "diffsinger",
              "phonemizer": "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer",
              "phonemizer_tag": "English", "renderer": "DIFFSINGER", "muted": false}],
  "parts": [{
    "index": 0, "track": 0, "name": "Part", "position_tick": 0, "curve_points": {},
    "notes": [{"index": 0, "lyric": "sing", "tone": 60, "tone_name": "C4",
               "tick": 1920, "end_tick": 2400, "ms": 2000, "end_ms": 2500, "phonemes": [0, 1, 2]}],
    "phonemes": [{"index": 0, "phoneme": "en/s", "note": 0, "index_in_note": 0,
                  "tick": 1743, "end_tick": 1920, "ms": 1815.625, "end_ms": 2000, "duration_ms": 184.375,
                  "phrase": 0, "frame": 8, "frames": 16, "error": null}],
    "phrases": [{"index": 0, "tick": 1743, "end_tick": 2880, "ms": 1815.625, "end_ms": 3000,
                 "phonemes": [0, 1, 2, 3, 4], "input_hash": "d1bac834f377…",
                 "frame_ms": 11.609978, "head_frames": 8, "tail_frames": 8, "total_frames": 119,
                 "frame0_ms": 1722.745178,
                 "segments": [{"phoneme": "SP", "frame": 0, "frames": 8, "phoneme_index": null},
                              {"phoneme": "en/s", "frame": 8, "frames": 16, "phoneme_index": 0}]}]
  }],
  "settings": {"prefs": "gui", "runner": "WebGPU", "steps": 20, "…": "…"},
  "errors": [],
  "warnings": [],
  "diagnostics": []
}
```

| Field | Meaning |
| --- | --- |
| `parts[]` | The voice parts. `index` is the part's place among all parts of the project. |
| `notes[].phonemes` | Indexes into the part's `phonemes`. A note continued by `+` notes owns the phonemes sung on them. |
| `phonemes[].tick`, `end_tick`, `ms`, `end_ms` | Where the phoneme starts and ends, as the piano roll shows it: after phoneme overrides, and with the consonants before a note placed ahead of it. |
| `phonemes[].note`, `index_in_note` | The note the phoneme belongs to, and its place within that note. |
| `phonemes[].phrase` | The render phrase it is rendered in: an index into `phrases`. |
| `phonemes[].frame`, `frames` | DiffSinger only. Its first frame on the phrase's model timeline, and its length in frames: the value it gets in the acoustic model's `durations`. |
| `phonemes[].error` | Why the phoneme could not be produced, or `null`. An errored phoneme has no end, phrase or frames. |
| `phrases[]` | What the renderer renders in one go: phonemes that touch, or that the renderer merges. |
| `phrases[].input_hash` | A fingerprint of everything the renderer is given for the phrase: the phonemes with their timing, the pitch curve and the expression curves. Two runs with the same hash feed the models the same data, up to the models' own randomness. |
| `phrases[].frame_ms`, `head_frames`, `tail_frames`, `total_frames` | DiffSinger only. The frame length, the silent padding before and after the phrase, and the phrase's whole length in frames. |
| `phrases[].frame0_ms` | When frame 0 of the phrase starts. |
| `phrases[].segments` | The phrase exactly as tokens with durations: the `SP` padding, each phoneme, and `SP` for any gap between phonemes. `frames` sum to `total_frames`. |
| `curve_points` | How many points each drawn curve of the part has; `pitd` grows when pitch is loaded. |
| `settings` | The settings in effect: where the preferences came from, runner, steps, depth, phrase merging and the other DiffSinger switches, and the overrides given. |
| `errors[]` | The phonemes with errors: `part`, `phoneme`, `note`, `lyric`, `message`. |
| `warnings[]` | Things done differently from what was asked, e.g. a curve the singer does not use. |
| `diagnostics[]` | Errors OpenUtau logged or would have shown in a dialog. |

### `render`

Everything above, plus:

```json
{
  "outputs": [{"track": 0, "path": "/abs/song.wav", "channels": 1, "sample_rate": 44100, "bits": 16,
               "samples": 132300, "duration_ms": 3000, "sha256": "8e0f…"}],
  "settings": {"seed": 1, "seeded_random_nodes": 8, "load_pitch": "none", "mixdown": false,
               "curves": {"velc": 150}, "…": "…"},
  "dump": {"dir": "/abs/dump", "manifest": "/abs/dump/manifest.json"}
}
```

`sha256` is of the wav file, for comparing two renders without reading them. `track` is `null` for a mixdown. `dump` is `null` without `--dump`.

### `ustx`

The score:

```json
{
  "name": "demo",
  "bpm": 120,
  "time_signature": [4, 4],
  "start": 1920,
  "singer": "/path/to/Chiaki",
  "phonemizer": "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer",
  "notes": [
    ["strange", "A3", "1/4"],
    ["things", 59, 480],
    {"rest": "1/8"},
    {"lyric": "+", "tone": "D4", "duration": "1/8", "position": 3600}
  ],
  "curves": {"velc": 150, "genc": [[1920, 0], [2400, -30]]}
}
```

| Field | Meaning |
| --- | --- |
| `singer` | Required. A voicebank folder, or the id of an installed singer. A name works too, with a warning when it is also another singer's id or name. |
| `phonemizer` | Id; a tag or name works when only one phonemizer for the singer has it. Optional when the singer names a default phonemizer. |
| `bpm`, `time_signature`, `name` | Default 120, `[4, 4]`, "New Project". |
| `start` | Tick of the first note. Default 1920, one bar, which leaves room for the consonants sung before it. |
| `notes[]` | `[lyric, tone, duration]`, or an object with the same fields and an optional `position` in ticks; notes follow one another otherwise. `{"rest": duration}` leaves a gap. |
| tone | A MIDI number (60 is C4) or a name such as `"F#3"`. |
| duration | Ticks, or a fraction of a whole note as text: `"1/4"`, `"1/8."` (dotted). |
| lyric | As typed in the app: `+` continues the previous note, `word[p h o n e m e s]` gives phonemes directly. |
| `curves` | Per expression: a number sets the whole track; `[[tick, value], …]` draws a curve. |

Notes get the portamento and vibrato a note drawn in the app gets, from your note presets. The result reports `path`, `singer_id`, `singer_path`, `phonemizer`, `tempos`, the number of `notes`, and `start_tick` and `end_tick`.

### `singers`, `expressions`, `phonemizers`

- `singers`: `search_paths`, and per singer `id`, `name`, `type`, `path`, `version`, `author`, `default_phonemizer`, `errors`.
- `expressions`: `singer` (as above, plus `renderer` and, for DiffSinger, `diffsinger`: sample rate, hop size, `frame_ms`, languages, voice colors, whether it has pitch and variance predictors, `max_depth`, and which embeddings the acoustic model takes), and `expressions[]`: `abbr`, `name`, `type` (`numerical`, `options`, `curve`), `min`, `max`, `default`, `options`.
- `phonemizers`: per phonemizer `id`, `name`, `tag`, `language`, `author`.

## Reproducibility

**Phoneme timing is deterministic.** The linguistic and duration models have no randomness and always run on the CPU, so `phonemize` gives the same result every time, on any runner.

**Rendering is random unless seeded**, here as in the app. DiffSinger models draw their diffusion noise inside the model, from random nodes that ONNX Runtime seeds from the clock; the vocoder draws noise too. Two unseeded renders of the same project differ.

**`--seed N` makes a render repeatable.** Before a model is loaded, its random nodes are given a `seed` attribute in memory (the file on disk is not changed); `settings.seeded_random_nodes` counts them. For models that take their noise as an input instead, the seed is combined with the per-phrase one (not tried on a voicebank of that kind). With the same seed, project, voicebank, settings, runner and machine, the wav is byte-identical: check `outputs[].sha256`. Verified on Apple Silicon for both the CPU and the WebGPU runner, with a voicebank of the first kind. Things to know:

- Repeatable means: a fresh process doing the same runs in the same order. A model's random nodes advance with every run, so a second render in one `serve` session, or a render that takes some phrases from `--cache-dir`, draws other numbers than a first, uncached one.
- Seeds are told apart modulo 16777215: `--seed 1` and `--seed 16777216` give the same render.
- A model that keeps its weights in a separate data file cannot be loaded with `--seed`.
- CPU and WebGPU give different audio for the same seed (their arithmetic differs), each repeatable on its own.
- A seeded render is one of the renders the app could have produced, not a particular one: the app cannot be seeded.
- Each model keeps drawing from one random stream for the whole run, so the noise a phrase gets depends on the phrases rendered before it. Changing an early phrase changes the noise of the later ones.
- Do not expect identical audio across machines or ONNX Runtime versions.
