# OpenUtau Chiaki

[Chiaki](https://chiaki.ch/character/chiaki) is a free, multilingual DiffSinger voicebank for OpenUtau, built from the voice of Suzukaze Chiaki. She sings in 28 languages, strongest in Japanese, English and Chinese, and the songs you make with her can be used commercially.

This repo is a build of [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) with Chiaki bundled, so you can install one app and start singing.

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

## Command line

`OpenUtau.Cli` runs the app's phonemizer and renderer without the window, for scripts and agents. It prints one JSON object on stdout, reports failures on stderr with a non-zero exit code, and never writes to your preferences or voicebanks.

```sh
dotnet build OpenUtau.Cli/OpenUtau.Cli.csproj
scripts/openutau-cli.sh ustx score.json --out song.ustx
scripts/openutau-cli.sh phonemize song.ustx --singer Singers/Chiaki
scripts/openutau-cli.sh render song.ustx --singer Singers/Chiaki --seed 1 --out song.wav --dump dump/
```

| Command | What it does |
| --- | --- |
| `phonemize IN.ustx` | Every phoneme with its note and its start and end, in ticks, milliseconds and model frames: the timing the piano roll shows. |
| `render IN.ustx --out OUT.wav` | Renders to wav. `--seed` makes it repeatable, `--load-pitch` runs *Load Rendered Pitch* first, `--bpm`, `--tempo-scale` and `--curve velc=150` override the project, and `--dump DIR` saves the inputs and outputs of every DiffSinger model as `.npz`. |
| `ustx SCORE.json --out OUT.ustx` | Writes a project from a JSON score of lyrics, pitches and lengths. |
| `singers`, `expressions`, `phonemizers` | List the singers, the expressions a singer supports, and the phonemizers. |

For step-by-step work, `serve` keeps a project open and takes edit, query and render requests as JSON lines, `mcp` offers the same as a Model Context Protocol server, and both can instead work on the project open in the app (`send --gui`, `mcp --gui`) once the app is started with `OPENUTAU_AGENT=1`.

`--singer DIR` uses a voicebank folder that is not installed. Setting it up for an agent is in [OpenUtau.Cli/README.md](OpenUtau.Cli/README.md); every option and JSON field is in its [reference](OpenUtau.Cli/REFERENCE.md).

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
