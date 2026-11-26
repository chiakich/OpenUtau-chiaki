using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Cli;
using OpenUtau.Core;
using OpenUtau.Core.Agent;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// OpenUtau.Cli must report what the app shows: the same project is opened by the app and phonemized
    /// by the CLI, and every phoneme and every render phrase has to agree.
    ///
    /// Opt-in, because it needs a DiffSinger voicebank on disk and runs its models:
    /// <code>
    /// OPENUTAU_TEST_SINGER=/path/to/voicebank dotnet run --project OpenUtau.UiTest -- -class OpenUtau.UiTest.CliParityTest
    /// </code>
    /// OPENUTAU_TEST_PHONEMIZER picks the phonemizer (default: the English DiffSinger one, for the lyrics here).
    /// </summary>
    public class CliParityTest {
        sealed record Phoneme(int Part, string Name, int Note, int Tick, int EndTick, double Ms, double EndMs);
        sealed record Phrase(int Part, int Tick, int EndTick, int Phones, string InputHash);

        [Fact]
        public void CliPhonemesMatchTheApp() {
            string? singer = Environment.GetEnvironmentVariable("OPENUTAU_TEST_SINGER");
            if (string.IsNullOrEmpty(singer) || !Directory.Exists(singer)) {
                Assert.Skip("Set OPENUTAU_TEST_SINGER to a DiffSinger voicebank folder to run this live test.");
            }
            // The app saves its preferences as it runs; this test changes some and must not leave them behind.
            // Set here as well as in InitCore: the app reads its preferences before InitCore runs.
            Headless.ReadOnlyUserData = true;
            string dir = Path.Combine(Path.GetTempPath(), $"openutau-parity-{Guid.NewGuid():N}");
            // A singer's id is its folder name, and the app prefers a bundled or installed singer of the
            // same id. A link with a name of its own makes both sides load this voicebank.
            string link = Path.Combine(dir, "singers", $"cli-parity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            try {
                try {
                    Directory.CreateSymbolicLink(link, Path.GetFullPath(singer!));
                } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
                    Assert.Skip($"Cannot create symbolic links: {e.Message}");
                }
                singer = link;
                string ustx = WriteProject(dir, singer);
                var cli = CliProcess.Run("phonemize", ustx, "--singer", singer);
                Assert.True(cli.ExitCode == 0, cli.Stderr);
                var (cliPhonemes, cliPhrases) = FromCli(cli.Json());
                Assert.NotEmpty(cliPhonemes);

                var (appPhonemes, appPhrases) = FromApp(ustx, singer);

                Assert.Equal(appPhonemes.Count, cliPhonemes.Count);
                for (int i = 0; i < cliPhonemes.Count; i++) {
                    var expected = appPhonemes[i];
                    var actual = cliPhonemes[i];
                    Assert.Equal((expected.Part, expected.Name, expected.Note, expected.Tick, expected.EndTick),
                        (actual.Part, actual.Name, actual.Note, actual.Tick, actual.EndTick));
                    Assert.Equal(expected.Ms, actual.Ms, 5);
                    Assert.Equal(expected.EndMs, actual.EndMs, 5);
                }
                // Including the hash of each phrase's pitch and curves: what the models are fed from.
                Assert.Equal(appPhrases, cliPhrases);
            } finally {
                // The link first, on its own: nothing here may ever reach into the voicebank it points to.
                File.Delete(link);
                if (!File.Exists(link) && !Directory.Exists(link)) {
                    Directory.Delete(dir, true);
                }
            }
        }

        // Several sentences, a slur, rests and consonant clusters at a tempo where consonants get tight.
        static string WriteProject(string dir, string singer) {
            string phonemizer = Environment.GetEnvironmentVariable("OPENUTAU_TEST_PHONEMIZER")
                ?? "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer";
            string score = Path.Combine(dir, "score.json");
            File.WriteAllText(score, JsonSerializer.Serialize(new {
                bpm = 168,
                singer,
                phonemizer,
                notes = new object[] {
                    new object[] { "strange", "A3", "1/8" },
                    new object[] { "things", "B3", "1/8" },
                    new object[] { "happen", "C4", "1/8" },
                    new object[] { "+", "D4", "1/8" },
                    new object[] { "tonight", "E4", "1/4" },
                    new { rest = "1/4" },
                    new object[] { "sprint", "E4", "1/16" },
                    new object[] { "through", "D4", "1/16" },
                    new object[] { "the", "C4", "1/16" },
                    new object[] { "streets", "B3", "1/8." },
                    new { rest = "1/8" },
                    new object[] { "glimpsed", "A3", "1/2" },
                },
                curves = new { genc = 20 },
            }));
            string ustx = Path.Combine(dir, "song.ustx");
            var result = CliProcess.Run("ustx", score, "--out", ustx);
            Assert.True(result.ExitCode == 0, result.Stderr);
            return ustx;
        }

        static (List<Phoneme>, List<Phrase>) FromCli(JsonElement json) {
            var phonemes = new List<Phoneme>();
            var phrases = new List<Phrase>();
            foreach (var part in json.GetProperty("parts").EnumerateArray()) {
                int index = part.GetProperty("index").GetInt32();
                foreach (var p in part.GetProperty("phonemes").EnumerateArray()) {
                    phonemes.Add(new Phoneme(index, p.GetProperty("phoneme").GetString()!, p.GetProperty("note").GetInt32(),
                        p.GetProperty("tick").GetInt32(), p.GetProperty("end_tick").GetInt32(),
                        p.GetProperty("ms").GetDouble(), p.GetProperty("end_ms").GetDouble()));
                }
                foreach (var p in part.GetProperty("phrases").EnumerateArray()) {
                    phrases.Add(new Phrase(index, p.GetProperty("tick").GetInt32(), p.GetProperty("end_tick").GetInt32(),
                        p.GetProperty("phonemes").GetArrayLength(), p.GetProperty("input_hash").GetString()!));
                }
            }
            return (phonemes, phrases);
        }

        // Opens the project the way File > Open does, in the real main window.
        static (List<Phoneme>, List<Phrase>) FromApp(string ustx, string singer) {
            var phonemes = new List<Phoneme>();
            var phrases = new List<Phrase>();
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                // As Program.Main, which the test host does not run: character.txt is read as Shift-JIS.
                // After InitCore, whose own singer search runs before DocManager can take its warnings.
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var prefs = Preferences.Default;
                string previousSingerPath = prefs.AdditionalSingerPath;
                int previousPitchMode = prefs.RealTimePitchMode;
                bool previousPreRender = prefs.PreRender;
                bool previousTensorCache = prefs.DiffSingerTensorCache;
                MainWindow? window = null;
                try {
                    prefs.AdditionalSingerPath = Path.GetDirectoryName(singer)!;
                    // Neither changes phonemes; both would run the heavier models for nothing.
                    prefs.RealTimePitchMode = (int)LivePitchMode.Off;
                    prefs.PreRender = false;
                    // The cache only saves work, and its files would land in the user's cache folder.
                    prefs.DiffSingerTensorCache = false;
                    SingerManager.Inst.SearchAllSingers();

                    window = new MainWindow { Width = 1280, Height = 800 };
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    Assert.IsType<MainWindowViewModel>(window.DataContext).OpenProject(new[] { ustx });

                    var project = DocManager.Inst.Project;
                    foreach (var track in project.tracks) {
                        Assert.True(track.Singer != null && track.Singer.Found && track.Singer.Location == singer,
                            $"The app resolved singer \"{track.Singer?.Name}\" to \"{track.Singer?.Location}\", not {singer}. "
                            + $"Errors: {string.Join(" | ", HeadlessUi.Errors.Snapshot())}");
                    }
                    WaitPhonemized(project);

                    for (int i = 0; i < project.parts.Count; i++) {
                        if (!(project.parts[i] is UVoicePart part)) {
                            continue;
                        }
                        var notes = part.notes.ToList();
                        foreach (var p in part.phonemes) {
                            Assert.False(p.Error, $"\"{p.phoneme}\" has an error in the app: {p.ErrorException?.Message}");
                            phonemes.Add(new Phoneme(i, p.phoneme, notes.IndexOf(p.Parent),
                                part.position + p.position, part.position + p.End, p.PositionMs, p.EndMs));
                        }
                        foreach (var phrase in part.renderPhrases) {
                            phrases.Add(new Phrase(i, phrase.position, phrase.end, phrase.phones.Length,
                                PhraseInputHash.Of(phrase)));
                        }
                    }
                } finally {
                    window?.Close();
                    // Singers of the project must not be in use when the search below disposes them.
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                    prefs.AdditionalSingerPath = previousSingerPath;
                    prefs.RealTimePitchMode = previousPitchMode;
                    prefs.PreRender = previousPreRender;
                    prefs.DiffSingerTensorCache = previousTensorCache;
                    SingerManager.Inst.SearchAllSingers();
                    HeadlessUi.Flush();
                }
            });
            return (phonemes, phrases);
        }

        // The phonemizer and the phrase builder answer through the UI thread, which this test is blocking.
        static void WaitPhonemized(UProject project) {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            int settled = 0;
            while (settled < 10) {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the app's phonemizer.");
                HeadlessUi.Flush();
                Thread.Sleep(30);
                var parts = project.parts.OfType<UVoicePart>().Where(p => p.notes.Count > 0).ToArray();
                bool ready = parts.All(p => p.PhonemesUpToDate && p.phonemes.Count > 0 && p.renderPhrases.Count > 0);
                settled = ready ? settled + 1 : 0;
            }
        }
    }
}
