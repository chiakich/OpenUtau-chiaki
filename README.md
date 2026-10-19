# OpenUtau Chiaki

Personal build of [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) with the Chiaki DiffSinger voicebank built in.

## Differences from Lunai

- **Chiaki voicebank bundled** in `Singers/Chiaki` (Git LFS) and loaded ahead of any installed copy.
- **DiffSinger Finnish phonemizer built in**, so Finnish tracks need no extra plugin DLL.
- **Singer Hub removed.**
- **Own identity**: the app is `OpenUtau-Chiaki` with its own data folder, so it can sit next to Lunai.
- **Updates come from this repo's releases.** On Windows the Velopack updater reads a GitHub token from `OPENUTAU_CHIAKI_GITHUB_TOKEN` because the repo is private; on macOS and Linux the updater opens the releases page.
- Fixes on top of Lunai: portrait cropping on HiDPI displays, and expanding a detached piano roll.

## Building

Git LFS is required; without it the voicebank models are only pointer files.

```sh
git lfs install
git clone https://github.com/chiakich/OpenUtau-chiaki.git
dotnet build OpenUtau/OpenUtau.csproj
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj
```

The build copies `Singers/` next to the executable.

## Syncing with Lunai

```sh
git fetch upstream
git merge upstream/master
```

## Credits and license

Based on [OpenUtau Lunai](https://github.com/keirokeer/OpenUtau-lunai) and [OpenUtau](https://github.com/openutau/OpenUtau); credit belongs to their authors and everyone listed in **Help → About OpenUtau**. Code is under the upstream license (`LICENSE.txt`).

The Chiaki voicebank is for personal use only. Its `tgm_hifigan` vocoder is CC BY-NC 4.0 (no commercial use); see `Singers/Chiaki/tgm_hifigan/documents`.
