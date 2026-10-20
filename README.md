# OpenUtau Chiaki

Personal build of [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) with the Chiaki DiffSinger voicebank built in.

## Differences from Lunai

- **Chiaki voicebank bundled**: stored as a release asset, placed in `Singers/Chiaki` before building, and loaded ahead of any installed copy.
- **DiffSinger Finnish and Latin phonemizers built in**, so Finnish and Latin (Ecclesiastical) tracks need no extra plugin DLL.
- **Singer Hub removed.**
- **Own identity**: the app is `OpenUtau-Chiaki` with its own data folder, so it can sit next to Lunai.
- **Updates come from this repo's releases.** On Windows the Velopack updater reads a GitHub token from `OPENUTAU_CHIAKI_GITHUB_TOKEN` because the repo is private; on macOS and Linux the updater opens the releases page.
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

## Releases

`.github/workflows/release.yml` tests once, then builds Windows (x64, ARM64), macOS (Apple Silicon, Intel) and Linux (x64, ARM64). Push a `v1.2.3` tag to publish a release, or run the workflow by hand to get build artifacts only. macOS builds are signed and notarized once the signing secrets are set; see [RELEASING.md](RELEASING.md).

## Syncing with Lunai

```sh
git fetch upstream
git merge upstream/master
```

## Credits and license

Based on [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) and [OpenUtau](https://github.com/openutau/OpenUtau); credit belongs to their authors and everyone listed in **Help → About OpenUtau**. Code is under the upstream license (`LICENSE.txt`).

The Chiaki voicebank is licensed under CC BY-NC 4.0 (no commercial use), as is its `tgm_hifigan` vocoder; see `Singers/Chiaki/readme.txt` and `Singers/Chiaki/tgm_hifigan/documents`. The illustration is not covered by that license.
