# OpenUtau.Cli

OpenUtau without the window, for agents and scripts. It runs the same `OpenUtau.Core` code as the app (the same phonemizer runner, phrase builder and renderers), so its phonemes and renders are the app's. There are three ways to use it:

- **MCP server**: an agent writes, edits and renders a song through tools, on a project of its own or on the one open in the app.
- **Session** (`serve`, `send`): the same requests as JSON lines, for scripts.
- **One-shot commands** (`ustx`, `phonemize`, `render`): one file in, one JSON result out.

## Set it up for an agent

It needs the .NET 10 SDK and a DiffSinger voicebank folder. Developed and tested on macOS (Apple Silicon).

```sh
dotnet build OpenUtau.Cli/OpenUtau.Cli.csproj
scripts/openutau-cli.sh help
```

Then add the server to the agent's MCP configuration:

```json
{"mcpServers": {"openutau-chiaki": {
  "command": "/path/to/OpenUtau/scripts/openutau-cli.sh",
  "args": ["mcp", "--singer", "/path/to/voicebank"]}}}
```

`--singer DIR` makes that voicebank available without installing it; its id is the folder name, and the server tells the agent so. Installed singers are available too.

To have the agent work on the project open in the app instead, use `"args": ["mcp", "--gui"]` and turn the app's agent connection on: `"AgentServer": true` in its `prefs.json`, or start it with `OPENUTAU_AGENT=1`. The user sees every edit live, and each request is one step of the app's Undo. The connection is off by default because any process of the same user that reaches the socket (`agent.sock` in the app's data folder, or `OPENUTAU_AGENT_SOCKET`) can edit the open project and write files through save and render.

`scripts/openutau-cli.sh` builds the CLI if it has never been built. It does not notice later source changes: run it once with `OPENUTAU_CLI_REBUILD=1` after pulling. It looks for `dotnet` on `PATH`, then in `~/.dotnet`; `DOTNET=/path/to/dotnet` names another one.

## What an agent should know

The server says all of this itself, in its instructions and tool descriptions; an agent does not need this file. It is here for the person reading along.

- Refer to singers and phonemizers by id. Names and tags are not unique: a tag shared by several phonemizers is refused unless only one of them fits the singer.
- A singer's default phonemizer is often not the one for the language of the lyrics. `list_phonemizers` with `singer` and `query` (the language) finds the fitting ones, and with `lyrics` tries them on those words without touching the project.
- After a request that changes what is sung, the reply's `phoneme_errors` counts the phonemes that cannot be sung and a warning shows the first; `get_notes` lists them all. `render` refuses while there are any.
- A lyric the phonemizer does not know can spell its phonemes: `word[p h o n e m e s]`. `+` continues the syllable of the note before.
- A new project's first note is at tick 1920 (one 4/4 bar in), which leaves room for the consonants sung before it. Ticks are 480 per quarter note.
- Without the app, notes are sung at their plain pitch until `load_pitch` writes the singer's own pitch into the `pitd` curve. Do it before a render meant for listening; `render` warns when a part has none.
- After changing notes, the curve still follows the old ones. `load_pitch` with `"mode": "all"` rewrites it, replacing everything in `pitd`; the default mode skips phrases that already have a curve. `render` warns about a part left that way.
- There is no vibrato tool: after the last `load_pitch`, read `pitd` over the note with `get_notes` (`"curves": ["pitd"]`, `from_tick`, `to_tick`), add a sine to its values (in cents), and write them all back with `set_curve`.
- `save_project` names a voicebank folder by its folder name only, and warns so: the app opens the project with its own singer of that id, which may be another voice. When an installed singer is the same voicebank (same name and `dsconfig.yaml`), the server says so at the start and in that warning, and a project saved with the installed id opens with the right voice.
- One request that changes the project is one revision and one step of undo (`load_pitch`: one step per part). `expect_revision` refuses a request if the project moved on.
- Two renders of the same project in one session can differ by a hair, seeded or not.

In the app, what differs: singers are the installed ones, `new_project` and `open_project` need `"discard_changes": true` while the project has unsaved changes, paths must be absolute, and `add_notes` without a `part` goes to the part open in the piano roll (and opens the one it wrote to when none is).

## Sessions without MCP

```sh
scripts/openutau-cli.sh serve --singer /path/to/voicebank      # JSON lines on stdin and stdout
scripts/openutau-cli.sh serve --socket /tmp/ou.sock --singer /path/to/voicebank &
scripts/openutau-cli.sh send --socket /tmp/ou.sock status      # one request to that session
scripts/openutau-cli.sh send --gui '{"op":"get","detail":"phonemes"}'   # one request to the app
```

A request is one line, `{"op": name, ...fields}`; a reply is one line with `ok`, `revision`, `warnings`, `diagnostics`, the op's own fields, and `error: {code, message}` when `ok` is false. `{"op":"help"}` returns every op with its fields. The ops are the MCP tools under shorter names (`new`, `open`, `save`, `get`, `singers`, `phonemizers`, `expressions`), plus `shutdown`. More in the [reference](REFERENCE.md#session-requests).

## One-shot commands

A sentence from text to audio:

```sh
cat > score.json <<'EOF'
{"bpm": 120, "singer": "/path/to/Chiaki",
 "phonemizer": "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer",
 "notes": [["sing", "C4", "1/4"], ["now", "D4", "1/4"]]}
EOF
scripts/openutau-cli.sh ustx score.json --out song.ustx
scripts/openutau-cli.sh phonemize song.ustx --singer /path/to/Chiaki
scripts/openutau-cli.sh render song.ustx --singer /path/to/Chiaki --load-pitch untuned --seed 1 --out song.wav
```

| Command | What it does |
| --- | --- |
| `ustx SCORE.json --out OUT.ustx` | Writes a project from a small JSON score (`-` reads it from stdin). |
| `phonemize IN.ustx` | Notes, phonemes and render phrases with their timing. |
| `render IN.ustx --out OUT.wav` | Renders to wav; the result also carries everything `phonemize` reports. |
| `singers`, `phonemizers [--singer DIR\|ID]`, `expressions --singer DIR\|ID` | What is installed, and what a singer supports. |
| `help` | Every command and option, as JSON. |

Options, the score format and every result field are in the [reference](REFERENCE.md). The other project formats the app opens (`.ust`, `.vsqx`, `.mid`, `.svp`, …) go through the same loader, but only `.ustx` has been tested.

### Singers

`--singer DIR` loads the voicebank in `DIR` for every track of the project a one-shot command reads (`--singer 1=DIR` for track 1 only). Without it, singers are found where the app finds them, with one exception: the voicebanks the app ships next to its own executable (`Singers/` in this repo) are not visible to the CLI, so pass `--singer Singers/Chiaki` for those.

A project names its singer by id only, and the id is the folder name. `ustx` with `"singer": "/path/to/Chiaki"` writes `singer: Chiaki`, which the app resolves to its bundled `Chiaki`, whatever folder you meant. Always pass `--singer` when the voicebank matters.

## Conventions

- **stdout** is exactly one JSON object on one line (`--pretty` indents it); a session writes one per request.
- **On failure** of a one-shot command stdout is empty, the last line of stderr is `{"ok":false,"error":{"code":"…","message":"…"}}`, and the exit code is 1, or 2 for bad arguments. Earlier stderr lines are logs.
- **Phoneme errors** still print the full result, with `"ok": false` and the failing notes in `errors`, and exit 1. `render` stops before rendering; `--allow-errors` accepts them.
- **Units**: ticks are 480 per quarter note and milliseconds are counted from the start of the project. Every index starts at 0.
- **Your data is read, never written**: preferences, note presets, installed singers and plugins are loaded as the app loads them; nothing is saved back, and the voicebank folder is not touched. Render caches go to a temporary folder that is removed afterwards.
- **Settings come from the app's `prefs.json`** (steps, depth, runner, …), so a render uses what the app would use; the values in effect are echoed in `settings`. `--prefs default` ignores the file, and `--set` overrides single values.
- **Live pitch is off.** Nothing runs unless asked: `--load-pitch untuned`, or `load_pitch` in a session, does what the app's live pitch does when it opens a project.

## Reproducibility

Phoneme timing is deterministic. Rendering is random unless seeded, here as in the app. `render --seed N` in a fresh process gives a byte-identical wav for the same project, voicebank, settings, runner and machine (check `outputs[].sha256`); in a session only the first render after it starts is repeatable that way. The limits are in the [reference](REFERENCE.md#reproducibility).

## More

- [REFERENCE.md](REFERENCE.md): options, session requests, the score format, result fields, reproducibility.
- [MODEL-DUMP.md](MODEL-DUMP.md): `render --dump`, which saves every model's inputs and outputs, and how to replay them.
- [DEVELOPMENT.md](DEVELOPMENT.md): what "the same as the app" rests on, and the tests.
