# Developing OpenUtau.Cli

## What "the same as the app" rests on

The CLI does not reimplement anything: it starts `OpenUtau.Core` with a main thread of its own in place of the UI thread, opens the project through the same loader, and waits for the same phonemizer runner and phrase builder the app uses. What differs from the app is listed here:

- Live pitch and pre-render are off: nothing runs unless the command asks for it. Use `--load-pitch untuned` for what live pitch does on opening a project.
- The render cache is separate and starts empty.
- The app's bundled `Singers/` folder is not searched (see the [README](README.md#singers)).

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
