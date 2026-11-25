#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace OpenUtau.Cli {
    public class CliCommandTest {
        [Fact]
        public void HelpListsEveryCommand() {
            var result = CliProcess.Run("help");
            Assert.Equal(0, result.ExitCode);
            var commands = result.Json().GetProperty("commands").EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "phonemize", "render", "ustx", "singers", "expressions", "phonemizers", "serve", "send", "mcp" }, commands);
        }

        [Theory]
        [InlineData("frobnicate")]
        [InlineData("render", "song.ustx")]
        [InlineData("phonemize", "song.ustx", "--seed", "1")]
        [InlineData("phonemize", "song.ustx", "--bpm", "0")]
        public void BadArgumentsExitWith2AndNoOutput(params string[] args) {
            var result = CliProcess.Run(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Equal("usage", result.Error().GetProperty("code").GetString());
        }

        [Fact]
        public void AMissingProjectExitsWith1AndNoOutput() {
            var result = CliProcess.Run("phonemize", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ustx"));
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Equal("file_not_found", result.Error().GetProperty("code").GetString());
        }
    }

    // Opt-in, because it needs a DiffSinger voicebank on disk and runs its models:
    //   OPENUTAU_TEST_SINGER=/path/to/voicebank dotnet run --project OpenUtau.Test -- -class OpenUtau.Cli.CliLiveTest
    // OPENUTAU_TEST_PHONEMIZER picks the phonemizer (default: the English DiffSinger one, for the English lyrics here).
    public class CliLiveTest {
        static string Singer() {
            string? singer = Environment.GetEnvironmentVariable("OPENUTAU_TEST_SINGER");
            if (string.IsNullOrEmpty(singer) || !Directory.Exists(singer)) {
                Assert.Skip("Set OPENUTAU_TEST_SINGER to a DiffSinger voicebank folder to run this live test.");
            }
            return singer!;
        }

        static string WriteProject(string dir, string singer) {
            string phonemizer = Environment.GetEnvironmentVariable("OPENUTAU_TEST_PHONEMIZER")
                ?? "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer";
            string score = Path.Combine(dir, "score.json");
            File.WriteAllText(score, JsonSerializer.Serialize(new {
                bpm = 120,
                singer,
                phonemizer,
                notes = new object[] {
                    new object[] { "strange", "A3", "1/4" },
                    new object[] { "things", "B3", 480 },
                    new { rest = "1/4" },
                    new object[] { "tonight", "E4", "1/2" },
                },
            }));
            string ustx = Path.Combine(dir, "song.ustx");
            var result = CliProcess.Run("ustx", score, "--out", ustx);
            Assert.True(result.ExitCode == 0, result.Stderr);
            Assert.Equal(3, result.Json().GetProperty("notes").GetInt32());
            Assert.True(File.Exists(ustx));
            return ustx;
        }

        static string TempDir() {
            string dir = Path.Combine(Path.GetTempPath(), $"openutau-cli-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void PhonemizeReportsEveryPhonemeOnTheFrameGrid() {
            string singer = Singer();
            string dir = TempDir();
            try {
                string ustx = WriteProject(dir, singer);
                var result = CliProcess.Run("phonemize", ustx, "--singer", singer);
                Assert.True(result.ExitCode == 0, result.Stderr);
                var json = result.Json();
                Assert.True(json.GetProperty("ok").GetBoolean());
                var part = json.GetProperty("parts")[0];
                var notes = part.GetProperty("notes").EnumerateArray().ToArray();
                Assert.Equal(new[] { 1920, 2400, 3360 }, notes.Select(n => n.GetProperty("tick").GetInt32()).ToArray());
                var phonemes = part.GetProperty("phonemes").EnumerateArray().ToArray();
                Assert.True(phonemes.Length >= 6);
                // Two sentences, split at the rest; each phrase's segments tile its frames exactly.
                var phrases = part.GetProperty("phrases").EnumerateArray().ToArray();
                Assert.Equal(2, phrases.Length);
                foreach (var phrase in phrases) {
                    var segments = phrase.GetProperty("segments").EnumerateArray().ToArray();
                    Assert.Equal("SP", segments.First().GetProperty("phoneme").GetString());
                    Assert.Equal("SP", segments.Last().GetProperty("phoneme").GetString());
                    Assert.Equal(phrase.GetProperty("total_frames").GetInt32(), segments.Sum(s => s.GetProperty("frames").GetInt32()));
                }
                foreach (var phoneme in phonemes) {
                    Assert.True(phoneme.GetProperty("end_tick").GetInt32() > phoneme.GetProperty("tick").GetInt32());
                    Assert.True(phoneme.GetProperty("frames").GetInt32() > 0);
                    Assert.Equal(JsonValueKind.Null, phoneme.GetProperty("error").ValueKind);
                }

                // Twice the tempo: the same ticks, half the time for whatever follows the notes.
                var fast = CliProcess.Run("phonemize", ustx, "--singer", singer, "--tempo-scale", "2");
                Assert.True(fast.ExitCode == 0, fast.Stderr);
                var fastNotes = fast.Json().GetProperty("parts")[0].GetProperty("notes").EnumerateArray().ToArray();
                for (int i = 0; i < notes.Length; i++) {
                    Assert.Equal(notes[i].GetProperty("tick").GetInt32(), fastNotes[i].GetProperty("tick").GetInt32());
                    Assert.Equal(notes[i].GetProperty("ms").GetDouble() / 2, fastNotes[i].GetProperty("ms").GetDouble(), 3);
                }
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ASeededRenderIsReproducibleDownToTheModelInputs() {
            string singer = Singer();
            string dir = TempDir();
            try {
                string ustx = WriteProject(dir, singer);
                JsonElement Render(string name) {
                    var result = CliProcess.Run("render", ustx, "--singer", singer, "--seed", "11", "--runner", "cpu",
                        "--out", Path.Combine(dir, name + ".wav"), "--dump", Path.Combine(dir, name));
                    Assert.True(result.ExitCode == 0, result.Stderr);
                    return result.Json();
                }
                var first = Render("a");
                var second = Render("b");
                string Sha(JsonElement json) => json.GetProperty("outputs")[0].GetProperty("sha256").GetString()!;
                Assert.Equal(Sha(first), Sha(second));
                Assert.True(first.GetProperty("outputs")[0].GetProperty("samples").GetInt64() > 44100);
                Assert.True(first.GetProperty("settings").GetProperty("seeded_random_nodes").GetInt32() > 0);

                var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "a", "manifest.json"))).RootElement;
                var entries = manifest.GetProperty("entries").EnumerateArray().ToArray();
                foreach (string stage in new[] { "dur_linguistic", "dur", "variance_linguistic", "variance", "acoustic", "vocoder" }) {
                    Assert.Equal(2, entries.Count(e => e.GetProperty("stage").GetString() == stage));
                }
                var acoustic = entries.First(e => e.GetProperty("stage").GetString() == "acoustic");
                foreach (string input in new[] { "tokens", "durations", "f0" }) {
                    Assert.True(acoustic.GetProperty("inputs").TryGetProperty(input, out _), input);
                }
                foreach (var entry in entries) {
                    string file = entry.GetProperty("file").GetString()!;
                    Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "a", file)), File.ReadAllBytes(Path.Combine(dir, "b", file)));
                }
            } finally {
                Directory.Delete(dir, true);
            }
        }
    }
}
