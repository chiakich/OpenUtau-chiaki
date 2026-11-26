# OpenUtau.Cli

OpenUtau without the window: phonemize a project, render it to wav, and save what each DiffSinger model was fed. It runs the same `OpenUtau.Core` code as the app (the same phonemizer runner, phrase builder and renderers), so its numbers are the app's numbers. It is made for agents and scripts: the result is JSON on stdout, nothing is interactive, and no window opens.

## Build and run

It needs the .NET 10 SDK and nothing else beyond what the app already uses.

```sh
dotnet build OpenUtau.Cli/OpenUtau.Cli.csproj
scripts/openutau-cli.sh help
```

`scripts/openutau-cli.sh` runs the built CLI and builds it first if it has never been built. It does not notice later source changes: run once with `OPENUTAU_CLI_REBUILD=1` after pulling or editing. It looks for `dotnet` on `PATH`, then in `~/.dotnet`; set `DOTNET=/path/to/dotnet` to name another one. Without the script: `dotnet OpenUtau.Cli/bin/Debug/net10.0/OpenUtau.Cli.dll <command>`. Developed and tested on macOS (Apple Silicon).

A sentence from text to audio, with the voicebank in a folder that is not installed:

```sh
cat > score.json <<'EOF'
{"bpm": 120, "singer": "/path/to/Chiaki",
 "phonemizer": "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer",
 "notes": [["sing", "C4", "1/4"], ["now", "D4", "1/4"]]}
EOF
scripts/openutau-cli.sh ustx score.json --out song.ustx
scripts/openutau-cli.sh phonemize song.ustx --singer /path/to/Chiaki
scripts/openutau-cli.sh render song.ustx --singer /path/to/Chiaki --seed 1 --out song.wav --dump dump/
```

## Conventions

- **stdout** is exactly one JSON object on one line (`--pretty` indents it).
- **On failure** stdout is empty, the last line of stderr is `{"ok":false,"error":{"code":"…","message":"…"}}`, and the exit code is 1, or 2 for bad arguments. Earlier stderr lines are logs (ONNX Runtime warnings; everything with `--verbose`).
- **Phoneme errors** (an unknown lyric, for instance) still print the full result, with `"ok": false` and the failing notes in `errors`, and exit 1. `render` stops before rendering. `--allow-errors` accepts them.
- **Units**: ticks are 480 per quarter note, counted from the start of the project. Milliseconds are from the start of the project. Every index starts at 0.
- **Your data is read, never written**: preferences, note presets, installed singers and plugins are loaded as the app loads them; nothing is saved back, and the voicebank folder is not touched. Render caches go to a temporary folder that is removed afterwards, not to the app's cache.
- **Settings come from the app's `prefs.json`** (steps, depth, runner, phrase merging, …), so a render uses what the app would use. The values in effect are echoed in `settings`. `--prefs default` ignores the file; `--set` and the render options override single values.

### Singers

`--singer DIR` loads the voicebank in `DIR` for every track (`--singer 1=DIR` for track 1 only); it does not have to be installed. Without it, singers are found where the app finds them, with one exception: the voicebanks the app ships next to its own executable (`Singers/` in this repo) are not visible to the CLI, so pass `--singer Singers/Chiaki` for those.

A project names its singer by id only, and the id is the folder name. `ustx` with `"singer": "/path/to/Chiaki"` writes `singer: Chiaki`, which the app resolves to its bundled `Chiaki`, whatever folder you meant. Always pass `--singer` to the CLI when the voicebank matters.

## Commands

| Command | What it does |
| --- | --- |
| `phonemize IN.ustx` | Notes, phonemes and render phrases with their timing. |
| `render IN.ustx --out OUT.wav` | Renders to wav. The result also carries everything `phonemize` reports. |
| `ustx SCORE.json --out OUT.ustx` | Writes a project from a small JSON score (`-` reads the score from stdin). |
| `singers` | Lists the installed singers. |
| `expressions --singer DIR\|ID` | The expressions a singer supports, with ranges and defaults, and its DiffSinger configuration. |
| `phonemizers [--singer DIR\|ID]` | Lists the phonemizers, or those the app offers for a singer. |
| `serve [--socket PATH]` | Keeps a project open and edits it request by request; see [Sessions](#sessions-editing-a-project-step-by-step). |
| `send (--gui \| --socket PATH) REQUEST` | Sends one request to a running session, or to the app. |
| `mcp [--gui \| --socket PATH]` | The session as a Model Context Protocol server. |
| `help` | The commands and options, as JSON. |

The other project formats the app opens (`.ust`, `.vsqx`, `.mid`, `.svp`, …) go through the same loader, but only `.ustx` has been tested.

### Options

For `phonemize` and `render`:

| Option | Meaning |
| --- | --- |
| `--singer DIR` or `N=DIR` | Voicebank folder for every track, or for track `N`. |
| `--phonemizer ID` or `N=ID` | Phonemizer by id, tag or name (see `phonemizers`). |
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
| `--dump DIR` | Save every model's inputs and outputs. See [Model dump](#model-dump). |
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

## Sessions: editing a project step by step

The commands above each start, do one thing and exit. A session keeps one project open and takes requests one at a time, which is what an agent composing or tuning a song needs: add a few notes, look at the phonemes, fix a lyric, render eight bars, undo. The same requests work in two places:

| | Starts with | The project | Who sees the edits |
| --- | --- | --- | --- |
| Without a window | `serve` | its own, in memory until `save` | nobody; results are files |
| In the app | the app, with its agent connection on | the one the user has open | the user, live, and each request is one step of the app's Undo |

```sh
scripts/openutau-cli.sh serve --singer /path/to/voicebank      # JSON lines on stdin and stdout
scripts/openutau-cli.sh serve --socket /tmp/ou.sock --singer /path/to/voicebank &
scripts/openutau-cli.sh send --socket /tmp/ou.sock status      # one request to that session
scripts/openutau-cli.sh send --gui '{"op":"get","detail":"phonemes"}'   # one request to the app
```

A request is one line, `{"op": name, ...fields}`; a reply is one line with `ok`, `op`, `revision`, `warnings`, `diagnostics`, the op's own fields, and `error: {code, message}` when `ok` is false. An `id` in the request is echoed. Unknown ops and unknown fields are refused, never ignored. `{"op":"help"}` returns the full list with every field; in short:

| Op | Does |
| --- | --- |
| `status` | project, tempo, tracks, parts, undo state; in the app also `ui`: the open part, selected note ids, playhead |
| `new`, `open`, `save` | replace the project from a score (as the `ustx` command takes), open a file, save as `.ustx` |
| `get` | notes with their ids and phonemes; `detail`: `notes`, `phonemes` (timing), `phrases` (what the renderer is given); `from_tick`/`to_tick`, `curves` |
| `add_notes`, `edit_notes`, `delete_notes` | `[[lyric, tone, duration], ...]` at a tick or after a note; `{id, lyric?, tone?, tick?, duration?}`; ids |
| `set_curve` | draw `points`, `clear`, or set the track `default` of an expression such as `pitd` or `genc` |
| `set_tempo`, `set_track` | tempo; a track's singer and phonemizer (a new singer brings the phonemizer last used with it in the app, else its default, unless one is given) |
| `load_pitch` | write the model's predicted pitch into `pitd`, as *Load Rendered Pitch* |
| `render` | wav of the project, or of `from_tick`..`to_tick` only (the file then starts at `from_tick`) |
| `undo`, `redo` | the app's undo stack, the user's edits included |
| `singers`, `phonemizers`, `expressions` | as the commands of the same name |
| `shutdown` | ends a `serve` session; refused in the app |

Ticks are 480 per quarter note from the start of the project. A note's `id` stays the same for as long as the project is loaded, through edits and undo.

`get` waits for the phonemizer, so what it returns is never stale. `revision` goes up with every change to the project, the user's in the app included, and not when nothing changed (a refused request, a phonemizer answer). An editing request (also `undo`, `redo`, `new`, `open` and `load_pitch`) may carry `"expect_revision": N` and is then refused with code `conflict` if the project moved on since the reply that said `N`. A request that fails part-way is rolled back whole. Each editing request is one undo step, except `load_pitch`, which is one step per part as in the app. `open`, `save` and `render` take paths relative to the folder the session runs in; in the app they must be absolute.

Socket paths must be short (104 bytes on macOS). The socket file is readable and writable by its owner only.

### In the app

The app listens only when asked to, by either `"AgentServer": true` in its `prefs.json` or the environment variable `OPENUTAU_AGENT=1`. It then listens on `agent.sock` in its data folder (`OPENUTAU_AGENT_SOCKET` overrides the path), which is where `send --gui` and `mcp --gui` connect. Any process of the same user that can reach that socket can edit the open project and write files through `save` and `render`, which is why it is off by default.

What differs from `serve`: singers are the installed ones (`--singer DIR` does not exist there), `--seed` does not apply, `new` and `open` are refused with code `unsaved_changes` while the project has unsaved changes unless the request says `"discard_changes": true`, and `add_notes` without a `part` goes to the part open in the piano roll. When no part is open there, `add_notes` opens the one it wrote to, so the user sees the notes.

### MCP

`mcp` is the same session as a [Model Context Protocol](https://modelcontextprotocol.io) server on stdin and stdout, one tool per op (`status`, `new_project`, `open_project`, `save_project`, `get_notes`, `add_notes`, `edit_notes`, `delete_notes`, `set_curve`, `set_tempo`, `set_track`, `load_pitch`, `render`, `undo`, `redo`, `list_singers`, `list_phonemizers`, `list_expressions`). Each tool returns the session's reply as JSON text, and marks it as an error when `ok` is false.

```json
{"mcpServers": {"openutau-chiaki": {
  "command": "/path/to/OpenUtau/scripts/openutau-cli.sh",
  "args": ["mcp", "--singer", "/path/to/voicebank"]}}}
```

With `"args": ["mcp", "--gui"]` the tools work on the project open in the app instead; the server starts whether or not the app is running, and tools fail with code `not_listening` until it is.

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
| `singer` | Required. A voicebank folder, or the id or name of an installed singer. |
| `phonemizer` | Id, tag or name. Optional when the singer names a default phonemizer. |
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

## Model dump

`render --dump DIR` writes `DIR/manifest.json` and one `.npz` per model run:

| File | Stage | Model |
| --- | --- | --- |
| `part00_sentence000.dur_linguistic.npz`, `….dur.npz` | Phonemizer, per sentence (notes that touch) | `dsdur` linguistic encoder and duration predictor |
| `part00_phrase000.pitch_linguistic.npz`, `….pitch.npz` | *Load Rendered Pitch*, per phrase; only with `--load-pitch` | `dspitch` |
| `part00_phrase000.variance_linguistic.npz`, `….variance.npz` | Render, per phrase | `dsvariance` |
| `part00_phrase000.acoustic.npz` | Render, per phrase | acoustic model |
| `part00_phrase000.vocoder.npz` | Render, per phrase | vocoder |

Each `.npz` holds the tensors exactly as fed, named `in.<input name>` (`in.tokens`, `in.durations`, `in.f0`, `in.velocity`, `in.gender`, `in.spk_embed`, `in.depth`, `in.steps`, …), and what the model returned, named `out.<output name>` (`out.mel`, `out.waveform`, …). Phrase numbers match `phrases[].index` of the result.

Files of an earlier run in the same folder are not removed: go by the manifest, or use a fresh folder.

`manifest.json` lists `models` (per track, the model file behind each stage) and `entries`:

| Field | Meaning |
| --- | --- |
| `file`, `stage`, `part` | The `.npz`, its stage, and the part. |
| `phrase` | The render phrase, for the render and pitch stages. |
| `sentence`, `tick` | For the phonemizer stages: the sentence's number within the part, and the tick of its first note. |
| `inputs`, `outputs` | Name, `dtype` and `shape` of every tensor. |
| `cached` | The outputs came from the tensor cache; the model did not run. Usual for the phonemizer stages, which the dump records on a second pass; otherwise only with `--cache-dir`. |
| `run_index` | The model's run count before this run, for [replaying](#replaying-a-dump) the random numbers. `null` when cached, and for the phonemizer stages. |
| `runs` | How many times this stage ran for this phrase; the file holds the last run. |

With the same seed, two dumps are byte-identical.

### Replaying a dump

```python
import json, numpy as np, onnxruntime as ort

dump = "dump"
manifest = json.load(open(f"{dump}/manifest.json"))
stage = "acoustic"
session = ort.InferenceSession(manifest["models"][0]["stages"][stage])
entry = next(e for e in manifest["entries"] if e["stage"] == stage and e["phrase"] == 0)
data = np.load(f"{dump}/{entry['file']}")
feeds = {name[3:]: data[name] for name in data.files if name.startswith("in.")}
# OpenUtau feeds these as shape (1,); the models declare scalars.
feeds = {k: v.reshape(()) if k in ("steps", "depth", "speedup") else v for k, v in feeds.items()}
mel = session.run(None, feeds)[0]
```

That reproduces the inputs. To reproduce the outputs too, the random numbers must match: give the model's random nodes (`RandomNormal`, `RandomNormalLike`, `RandomUniform`, `RandomUniformLike`, `Multinomial`, `Bernoulli`) the seeds the CLI gave them, the k-th such node in graph order getting

```python
float(1 + (seed + 7919 * k) % 16777215)
```

as its `seed` attribute, and run the entries of a stage on one session in `run_index` order. Replayed this way with `onnxruntime` 1.19 on the CPU, against a CPU render, every output of every stage came within 1e-4 of the dump. (Patch with `onnx` in a separate process from the one that runs `onnxruntime` if importing both crashes; on this setup, `onnxruntime` 1.19 also needed `graph_optimization_level = ORT_ENABLE_BASIC` for these models.)

## What "the same as the app" rests on

The CLI does not reimplement anything: it starts `OpenUtau.Core` with a main thread of its own in place of the UI thread, opens the project through the same loader, and waits for the same phonemizer runner and phrase builder the app uses. What differs from the app is listed here:

- Live pitch and pre-render are off: nothing runs unless the command asks for it. Use `--load-pitch untuned` for what live pitch does on opening a project.
- The render cache is separate and starts empty.
- The app's bundled `Singers/` folder is not searched (see [Singers](#singers)).

`OpenUtau.UiTest/CliParityTest.cs` checks the claim: it opens a project the way *File > Open* does (the main window's view model, on the app's UI thread, with the app's own initialization) and in the CLI, and compares every phoneme (name, note, start, end) and every render phrase, `input_hash` included.

## Tests

```sh
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj -- -namespace OpenUtau.Cli
```

runs the unit tests and the process-level tests of the error paths. The tests that need a DiffSinger voicebank are opt-in:

```sh
export OPENUTAU_TEST_SINGER=/path/to/voicebank
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj -- -class OpenUtau.Cli.CliLiveTest -class OpenUtau.Cli.AgentServeLiveTest
dotnet run --project OpenUtau.UiTest/OpenUtau.UiTest.csproj -- -class OpenUtau.UiTest.CliParityTest
```

Run `CliParityTest` on its own, as above: it re-scans the singers when it finishes, which can reach into a singers-dialog test running after it in the same process.

`OpenUtau.UiTest/GuiAgentTest.cs` runs a session against the app's main window (no voicebank needed). The process-level tests run the CLI from its default build folder; `OPENUTAU_CLI_DLL` points them at another build.

They use English lyrics with the English DiffSinger phonemizer; set `OPENUTAU_TEST_PHONEMIZER` for a voicebank that needs another one.
