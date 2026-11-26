# OpenUtau Chiaki

[Chiaki](https://chiaki.ch/character/chiaki) is a free, multilingual DiffSinger voicebank for OpenUtau, built from the voice of Suzukaze Chiaki. She sings in 28 languages, strongest in Japanese, English and Chinese, and the songs you make with her can be used commercially.

This repo is a build of [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) with Chiaki bundled, so you can install one app and start singing.

It also comes with an [MCP server](#for-ai-agents), so an AI agent such as Claude can write, edit and render a song with any DiffSinger voicebank, on its own or in the project you have open in the app.

## Download

- **App with Chiaki built in**: [Windows](https://github.com/chiakich/OpenUtau-chiaki/releases/latest/download/OpenUtau-Chiaki-win-x64-Setup.exe), [macOS (Apple Silicon)](https://github.com/chiakich/OpenUtau-chiaki/releases/latest/download/OpenUtau-Chiaki-osx-arm64.dmg), other platforms on the [releases page](https://github.com/chiakich/OpenUtau-chiaki/releases/latest).
- **Voicebank only**, for an existing OpenUtau: [Chiaki.zip](https://github.com/chiakich/OpenUtau-chiaki/releases/download/voicebank-v7/Chiaki.zip). Drag it into OpenUtau and pick the DiffSinger phonemizer for your language. It is also on [Hugging Face](https://huggingface.co/chiakic/chiaki-diffsinger), with every version tagged.
- Demos in every language, the terms of use and the character profile: [chiaki.ch/character/chiaki](https://chiaki.ch/character/chiaki).

## Languages

- **Trained**: Japanese, Chinese, English, Korean, French, Spanish, Italian, Portuguese, German, Russian, Polish, Ukrainian, Thai.
- **Finnish and Latin (Ecclesiastical)**: phonemizers are built into this app; plain OpenUtau needs the DiffSinger Finnish and Latin phonemizer plugins.
- **Built from existing phonemes, with a stronger accent**: Belarusian, Bulgarian, Macedonian, Serbo-Croatian, Lao, Catalan, Galician, Indonesian, Malay, Esperanto, Swahili, Hawaiian, Māori. These need the [Chiaki Extra Languages phonemizers](https://github.com/chiakich/OpenUtau-chiaki/releases/download/phonemizers-v2/ChiakiPhonemizers.zip), which this app also has built in.

## Differences from Lunai

- **Chiaki voicebank bundled**: stored as a release asset, placed in `Singers/Chiaki` before building, and loaded ahead of any installed copy.
- **DiffSinger Finnish and Latin phonemizers built in**, so Finnish and Latin (Ecclesiastical) tracks need no extra plugin DLL.
- **An MCP server and command line for AI agents and scripts**, inside the app: see [For AI agents](#for-ai-agents).
- **Singer Hub removed.**
- **Own identity**: the app is `OpenUtau-Chiaki` with its own data folder, so it can sit next to Lunai.
- **Updates come from this repo's releases.** On Windows the Velopack updater installs them (it uses a GitHub token from `OPENUTAU_CHIAKI_GITHUB_TOKEN` if one is set); on macOS and Linux it opens the releases page.
- Fixes on top of Lunai: portrait cropping on HiDPI displays, and expanding a detached piano roll.

## Building

The voicebank is not in git. `scripts/voicebank.json` names the release asset and its SHA256; fetch it before building:

```sh
scripts/fetch-voicebank.sh
dotnet build OpenUtau/OpenUtau.csproj
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj
```

The script downloads with `gh`, checks the SHA256, and skips work when `Singers/Chiaki` is already current. To use a local copy instead of downloading:

```sh
scripts/fetch-voicebank.sh --from ~/Library/OpenUtau/Singers/Chiaki
```

The build copies `Singers/` next to the executable. To publish a new voicebank, upload a zip whose top folder is `Chiaki` to a new release tag and update `scripts/voicebank.json`.

## For AI agents

`OpenUtau.Cli` is OpenUtau without the window. As a [Model Context Protocol](https://modelcontextprotocol.io) server it gives an agent tools to start a project, add and edit notes and lyrics, pick a singer and phonemizer, draw curves, load the singer's own pitch, render to wav and save. It runs the same code as the app, so what the agent hears is what the app would sing.

Releases after v1.0.1 carry it inside the app, as `OpenUtau.Cli` next to the app's own executable, so there is nothing else to install. Add it to the agent's MCP configuration:

```json
{"mcpServers": {"openutau-chiaki": {
  "command": "/Applications/OpenUtau-Chiaki.app/Contents/MacOS/OpenUtau.Cli",
  "args": ["mcp"]}}}
```

| | Where it is |
| --- | --- |
| macOS | `/Applications/OpenUtau-Chiaki.app/Contents/MacOS/OpenUtau.Cli` |
| Windows (Setup) | `%LocalAppData%\OpenUtau.Chiaki\current\OpenUtau.Cli.exe` |
| Windows (Portable), Linux (`.tar.gz`) | `OpenUtau.Cli` in the unpacked folder; the AppImage does not expose it |

For Claude Desktop there is a shortcut: each release has `OpenUtau-Chiaki.mcpb`. Install the app, then open that file (or drag it into Settings → Extensions); it finds the installed `OpenUtau.Cli` by itself and has a switch for working on the project open in the app.

The bundled Chiaki is singer `Chiaki`, and installed singers are available by id. From a checkout, `scripts/openutau-cli.sh` builds and runs it with the .NET 10 SDK instead.

- **On its own**: the agent keeps a project of its own and writes wav and `.ustx` files. `--singer DIR` adds a voicebank folder that is not installed.
- **With the app open**: `"args": ["mcp", "--gui"]` works on the project open in the app, once you turn the agent connection on (off by default). You see each edit as it happens, and each one is a step of Undo.

The same requests are available to scripts as JSON lines (`serve`, `send`), and there are one-shot commands that print one JSON object each:

| Command | What it does |
| --- | --- |
| `phonemize IN.ustx` | Every phoneme with its note and its start and end, in ticks, milliseconds and model frames: the timing the piano roll shows. |
| `render IN.ustx --out OUT.wav` | Renders to wav; `--seed` makes it repeatable, and `--dump DIR` saves the inputs and outputs of every DiffSinger model. |
| `ustx SCORE.json --out OUT.ustx` | Writes a project from a JSON score of lyrics, pitches and lengths. |
| `singers`, `expressions`, `phonemizers` | List the singers, the expressions a singer supports, and the phonemizers. |

It never writes to your preferences or voicebanks. Setting it up is in [OpenUtau.Cli/README.md](OpenUtau.Cli/README.md); every option and JSON field is in its [reference](OpenUtau.Cli/REFERENCE.md).

## Releases

`.github/workflows/release.yml` tests once, then builds Windows (x64, ARM64), macOS (Apple Silicon, Intel) and Linux (x64, ARM64). Push a `v1.2.3` tag to publish a release, or run the workflow by hand to get build artifacts only. macOS builds are signed and notarized once the signing secrets are set; see [RELEASING.md](RELEASING.md).

## Syncing with Lunai

```sh
git fetch upstream
git merge upstream/master
```

## Credits and license

Based on [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) and [OpenUtau](https://github.com/openutau/OpenUtau); credit belongs to their authors and everyone listed in **Help → About OpenUtau**. Code is under the upstream license (`LICENSE.txt`).

The Chiaki voicebank has its own terms of use (`Singers/Chiaki/terms.txt`, also on [chiaki.ch](https://chiaki.ch/character/chiaki)). In short: works made with it may be used and sold commercially, with a credit such as "Vocal: Chiaki"; the voicebank itself may not be redistributed on its own, and models trained on its output are limited to research and non-commercial use.

Its vocoder, `chiaki_vocoder 2026.11`, is an NSF-HiFiGAN trained from scratch for this voicebank and licensed under Apache License 2.0 (`Singers/Chiaki/dsvocoder/LICENSE`, `NOTICE.txt`). The portrait and icon are by shONe, who keeps the copyright. The portrait may be used on pages and works related to this voicebank, such as wiki entries and the cover art or music video of a song made with Chiaki; the icon may not be extracted or reused.
