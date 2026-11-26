#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace OpenUtau.Cli {
    // The session protocol, through serve as an agent would use it. No voicebank needed: notes can be
    // written, edited and saved without one.
    public class AgentServeTest {
        static JsonElement[] Session(params string[] requests) {
            var result = CliProcess.RunWithInput(string.Join("\n", requests) + "\n", "serve");
            Assert.True(result.ExitCode == 0, result.Stderr);
            return result.Stdout.Split('\n').Where(l => l.Trim().Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        }

        static string Code(JsonElement reply) => reply.GetProperty("error").GetProperty("code").GetString()!;

        [Fact]
        public void EditsAreNumberedAndCanBeUndone() {
            var replies = Session(
                "{\"op\":\"status\"}",
                "{\"id\":7,\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"],[\"la\",\"D4\",\"1/4\"]]}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":2,\"tone\":\"E4\",\"lyric\":\"li\"}]}",
                "{\"op\":\"get\"}",
                "{\"op\":\"delete_notes\",\"ids\":[1]}",
                "{\"op\":\"undo\"}",
                "{\"op\":\"get\"}");
            Assert.Equal(7, replies.Length);
            Assert.All(replies, r => Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText()));
            Assert.Equal("headless", replies[0].GetProperty("host").GetString());
            Assert.Equal(0, replies[0].GetProperty("revision").GetInt64());

            Assert.Equal(7, replies[1].GetProperty("id").GetInt32());
            var added = replies[1].GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(new[] { 1, 2 }, added.Select(n => n.GetProperty("id").GetInt32()));
            Assert.Equal(added[0].GetProperty("end_tick").GetInt32(), added[1].GetProperty("tick").GetInt32());

            var revisions = replies.Select(r => r.GetProperty("revision").GetInt64()).ToArray();
            Assert.True(revisions[1] > revisions[0] && revisions[2] > revisions[1], "Edits must raise the revision.");
            Assert.Equal(revisions[2], revisions[3]);

            var edited = replies[3].GetProperty("parts")[0].GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(2, edited.Length);
            Assert.Equal(("li", 64, 2), (edited[1].GetProperty("lyric").GetString(), edited[1].GetProperty("tone").GetInt32(),
                edited[1].GetProperty("id").GetInt32()));

            // The deleted note is back, under the id it had.
            var restored = replies[6].GetProperty("parts")[0].GetProperty("notes").EnumerateArray().ToArray();
            Assert.Equal(new[] { 1, 2 }, restored.Select(n => n.GetProperty("id").GetInt32()));
        }

        [Fact]
        public void BadRequestsAreAnsweredAndChangeNothing() {
            var replies = Session(
                "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"]]}",
                "not json",
                "{\"op\":\"frobnicate\"}",
                "{\"op\":\"get\",\"bogus\":1}",
                "{\"op\":\"delete_notes\",\"ids\":[99]}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"E4\"}],\"expect_revision\":0}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"E4\"},{\"id\":99,\"tone\":\"E4\"}]}",
                "{\"op\":\"render\",\"out\":\"" + Path.Combine(Path.GetTempPath(), $"never-{Guid.NewGuid():N}.wav").Replace("\\", "\\\\") + "\"}",
                "{\"op\":\"get\"}");
            Assert.Equal(9, replies.Length);
            Assert.Equal(new[] { "invalid_request", "invalid_request", "invalid_request", "note_not_found", "conflict", "note_not_found", "singer_not_found" },
                replies.Skip(1).Take(7).Select(Code));
            long revision = replies[0].GetProperty("revision").GetInt64();
            Assert.All(replies.Skip(1), r => Assert.Equal(revision, r.GetProperty("revision").GetInt64()));
            var note = replies[8].GetProperty("parts")[0].GetProperty("notes")[0];
            Assert.Equal(60, note.GetProperty("tone").GetInt32());
        }

        [Fact]
        public void ARefusedEditLeavesTheSessionUsable() {
            var replies = Session(
                "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"]]}",
                // The first change is made and rolled back when the second is refused.
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"E4\"},{\"id\":99,\"tone\":\"E4\"}]}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"G4\"}]}",
                "{\"op\":\"undo\"}",
                "{\"op\":\"get\"}");
            Assert.Equal("note_not_found", Code(replies[1]));
            Assert.True(replies[2].GetProperty("ok").GetBoolean(), replies[2].GetRawText());
            Assert.True(replies[3].GetProperty("ok").GetBoolean(), replies[3].GetRawText());
            // One undo takes back one request, and nothing of the refused one.
            Assert.Equal(60, replies[4].GetProperty("parts")[0].GetProperty("notes")[0].GetProperty("tone").GetInt32());
        }

        [Fact]
        public void ARequestIsOneRevisionAndSaysWhatCannotBeSung() {
            var replies = Session(
                "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"],[\"la\",\"D4\",\"1/4\"],[\"la\",\"E4\",\"1/4\"]]}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"G4\",\"lyric\":\"a\"},{\"id\":2,\"tone\":\"A4\"},{\"id\":3,\"duration\":\"1/2\"}]}",
                "{\"op\":\"undo\"}",
                // A tag several phonemizers share picks none of them.
                "{\"op\":\"set_track\",\"phonemizer\":\"English\"}");
            long first = replies[0].GetProperty("revision").GetInt64();
            Assert.Equal(new[] { first + 1, first + 2 }, replies.Skip(1).Take(2).Select(r => r.GetProperty("revision").GetInt64()));
            Assert.Equal(JsonValueKind.Number, replies[0].GetProperty("phoneme_errors").ValueKind);
            Assert.Equal("ambiguous_phonemizer", Code(replies[3]));
            Assert.Equal("edit_notes", replies[2].GetProperty("undone").GetString());
        }

        [Fact]
        public void RevisionsAndValuesAreChecked() {
            string missing = Path.Combine(Path.GetTempPath(), $"never-{Guid.NewGuid():N}.ustx").Replace("\\", "\\\\");
            var replies = Session(
                "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"]]}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"E4\"}],\"expect_revision\":\"1\"}",
                "{\"op\":\"get\",\"expect_revision\":1}",
                "{\"op\":\"undo\",\"expect_revision\":0}",
                "{\"op\":\"open\",\"path\":\"" + missing + "\"}",
                "{\"op\":\"new\",\"score\":{\"bpm\":120,\"start\":-480}}",
                "{\"op\":\"render\",\"out\":\"x.wav\",\"track\":-5}",
                "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"lyric\":\"la\"}]}",
                "{\"op\":\"get\"}");
            Assert.Equal(new[] { "invalid_request", "invalid_request", "conflict", "file_not_found", "invalid_score", "invalid_request" },
                replies.Skip(1).Take(6).Select(Code));
            long revision = replies[0].GetProperty("revision").GetInt64();
            // Writing the lyric a note already has changes nothing.
            Assert.All(replies.Skip(1), r => Assert.Equal(revision, r.GetProperty("revision").GetInt64()));
            Assert.Single(replies[8].GetProperty("parts")[0].GetProperty("notes").EnumerateArray());
        }

        [Fact]
        public void SaveWritesAProjectThatOpensAgain() {
            string dir = Path.Combine(Path.GetTempPath(), $"openutau-serve-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try {
                string path = Path.Combine(dir, "song.ustx");
                string quoted = JsonSerializer.Serialize(path);
                var replies = Session(
                    "{\"op\":\"set_tempo\",\"bpm\":96}",
                    "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"],[\"la\",\"D4\",\"1/2\"]]}",
                    "{\"op\":\"save\",\"path\":" + quoted + "}",
                    "{\"op\":\"status\"}",
                    "{\"op\":\"new\",\"score\":{\"bpm\":120,\"singer\":\"nobody\",\"phonemizer\":\"OpenUtau.Core.DefaultPhonemizer\"}}",
                    "{\"op\":\"open\",\"path\":" + quoted + "}",
                    "{\"op\":\"get\"}");
                Assert.All(replies, r => Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText()));
                Assert.True(File.Exists(path));
                Assert.True(replies[3].GetProperty("project").GetProperty("saved").GetBoolean());
                Assert.Equal(96, replies[6].GetProperty("project").GetProperty("tempos")[0].GetProperty("bpm").GetDouble());
                var notes = replies[6].GetProperty("parts")[0].GetProperty("notes").EnumerateArray().ToArray();
                Assert.Equal(new[] { 60, 62 }, notes.Select(n => n.GetProperty("tone").GetInt32()));
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ShutdownEndsTheSession() {
            var replies = Session("{\"op\":\"shutdown\"}", "{\"op\":\"status\"}");
            Assert.Single(replies);
            Assert.Equal("shutdown", replies[0].GetProperty("op").GetString());
        }

        [Fact]
        public void ASocketSessionIsSharedBySendCalls() {
            // Unix socket paths are short; the temp folder of a test run may not be.
            string socket = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", $"ou-{Guid.NewGuid():N}.sock");
            using var server = CliProcess.Start(false, "serve", "--socket", socket);
            try {
                var hello = JsonDocument.Parse(server.StandardOutput.ReadLine()!).RootElement;
                Assert.Equal(socket, hello.GetProperty("socket").GetString());

                var added = CliProcess.Run("send", "--socket", socket, "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"]]}");
                Assert.True(added.ExitCode == 0, added.Stderr);
                var got = CliProcess.Run("send", "--socket", socket, "get");
                Assert.True(got.ExitCode == 0, got.Stderr);
                Assert.Equal(1, got.Json().GetProperty("parts")[0].GetProperty("notes").GetArrayLength());

                var bad = CliProcess.Run("send", "--socket", socket, "frobnicate");
                Assert.Equal(1, bad.ExitCode);
                Assert.Equal(string.Empty, bad.Stdout);
                Assert.Equal("invalid_request", bad.Error().GetProperty("code").GetString());

                var second = CliProcess.Run("serve", "--socket", socket);
                Assert.Equal(1, second.ExitCode);
                Assert.Equal("socket_in_use", second.Error().GetProperty("code").GetString());

                Assert.Equal(0, CliProcess.Run("send", "--socket", socket, "shutdown").ExitCode);
                Assert.True(server.WaitForExit(30000), "serve did not end after shutdown.");
                Assert.False(File.Exists(socket), "The socket file was left behind.");

                var gone = CliProcess.Run("send", "--socket", socket, "status");
                Assert.Equal("not_listening", gone.Error().GetProperty("code").GetString());
            } finally {
                if (!server.HasExited) {
                    server.Kill(true);
                }
                File.Delete(socket);
            }
        }
    }

    public class McpTest {
        // Tool calls are answered when they finish, other requests at once: replies are matched by id.
        static System.Collections.Generic.Dictionary<string, JsonElement> Replies(string stdout) {
            return stdout.Split('\n').Where(l => l.Trim().Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(r => r.TryGetProperty("id", out _))
                .ToDictionary(r => r.GetProperty("id").ToString(), r => r);
        }

        [Fact]
        public void ToolsAreListedAndCalled() {
            string input = string.Join("\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"0\"}}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"tools/call\",\"params\":{\"name\":\"add_notes\",\"arguments\":{\"notes\":[[\"la\",\"C4\",\"1/4\"]]}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"delete_notes\",\"arguments\":{\"ids\":[99]}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"frobnicate\"}}",
                "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"resources/list\"}") + "\n";
            var result = CliProcess.RunWithInput(input, "mcp");
            Assert.True(result.ExitCode == 0, result.Stderr);
            var replies = Replies(result.Stdout);
            // The notification gets no reply.
            Assert.Equal(6, replies.Count);
            Assert.All(replies.Values, r => Assert.Equal("2.0", r.GetProperty("jsonrpc").GetString()));

            var init = replies["1"].GetProperty("result");
            Assert.Equal("2025-03-26", init.GetProperty("protocolVersion").GetString());
            Assert.Equal(JsonValueKind.Object, init.GetProperty("capabilities").GetProperty("tools").ValueKind);
            // Its own name: this fork's server must not pass for an official one.
            Assert.Equal("openutau-chiaki", init.GetProperty("serverInfo").GetProperty("name").GetString());

            var tools = replies["2"].GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Contains("get_notes", tools.Select(t => t.GetProperty("name").GetString()));
            Assert.DoesNotContain("shutdown", tools.Select(t => t.GetProperty("name").GetString()));
            Assert.All(tools, t => {
                Assert.False(string.IsNullOrEmpty(t.GetProperty("description").GetString()));
                Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());
            });

            var added = replies["a"].GetProperty("result");
            Assert.False(added.GetProperty("isError").GetBoolean());
            var reply = JsonDocument.Parse(added.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
            Assert.Equal(1, reply.GetProperty("notes")[0].GetProperty("id").GetInt32());

            // A refused request is a tool error the model can read, not a protocol error.
            var refused = replies["4"].GetProperty("result");
            Assert.True(refused.GetProperty("isError").GetBoolean());
            Assert.Contains("note_not_found", refused.GetProperty("content")[0].GetProperty("text").GetString());

            Assert.Equal(-32602, replies["5"].GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(-32601, replies["6"].GetProperty("error").GetProperty("code").GetInt32());
        }

        [Fact]
        public void WithoutTheAppRunningToolsFailButTheServerStays() {
            string socket = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", $"ou-{Guid.NewGuid():N}.sock");
            string input = string.Join("\n",
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"status\"}}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}") + "\n";
            var result = CliProcess.RunWithInput(input, "mcp", "--socket", socket);
            Assert.True(result.ExitCode == 0, result.Stderr);
            var replies = Replies(result.Stdout);
            Assert.Equal(2, replies.Count);
            Assert.True(replies["1"].GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("not_listening", replies["1"].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal(JsonValueKind.Object, replies["2"].GetProperty("result").ValueKind);
        }
    }

    // Opt-in, as CliLiveTest: OPENUTAU_TEST_SINGER=/path/to/voicebank.
    public class AgentServeLiveTest {
        [Fact]
        public void ASessionPhonemizesAndRendersARange() {
            string? singer = Environment.GetEnvironmentVariable("OPENUTAU_TEST_SINGER");
            if (string.IsNullOrEmpty(singer) || !Directory.Exists(singer)) {
                Assert.Skip("Set OPENUTAU_TEST_SINGER to a DiffSinger voicebank folder to run this live test.");
            }
            string phonemizer = Environment.GetEnvironmentVariable("OPENUTAU_TEST_PHONEMIZER")
                ?? "OpenUtau.Core.DiffSinger.DiffSingerEnglishPhonemizer";
            string dir = Path.Combine(Path.GetTempPath(), $"openutau-serve-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try {
                string wav = Path.Combine(dir, "range.wav");
                string score = JsonSerializer.Serialize(new {
                    bpm = 120,
                    singer,
                    phonemizer,
                    notes = new object[] {
                        new object[] { "strange", "A3", "1/4" },
                        new object[] { "things", "B3", "1/4" },
                        new object[] { "happen", "C4", "1/4" },
                        new object[] { "tonight", "D4", "1/4" },
                    },
                });
                var input = string.Join("\n",
                    "{\"op\":\"new\",\"score\":" + score + "}",
                    "{\"op\":\"get\",\"detail\":\"phonemes\"}",
                    "{\"op\":\"edit_notes\",\"notes\":[{\"id\":2,\"lyric\":\"dreams\"}]}",
                    "{\"op\":\"get\",\"detail\":\"phonemes\"}",
                    "{\"op\":\"render\",\"out\":" + JsonSerializer.Serialize(wav) + ",\"from_tick\":1920,\"to_tick\":2880}",
                    "{\"op\":\"load_pitch\"}",
                    "{\"op\":\"edit_notes\",\"notes\":[{\"id\":3,\"tone\":\"E4\"}]}",
                    "{\"op\":\"load_pitch\"}",
                    "{\"op\":\"render\",\"out\":" + JsonSerializer.Serialize(Path.Combine(dir, "stale.wav")) + ",\"from_tick\":1920,\"to_tick\":2880}",
                    "{\"op\":\"load_pitch\",\"mode\":\"all\"}",
                    "{\"op\":\"undo\"}",
                    "{\"op\":\"phonemizers\",\"singer\":" + JsonSerializer.Serialize(singer) + ",\"query\":\"english\",\"lyrics\":[\"strange\",\"things\"]}") + "\n";
                var result = CliProcess.RunWithInput(input, "serve", "--singer", singer!, "--seed", "1", "--runner", "cpu");
                Assert.True(result.ExitCode == 0, result.Stderr);
                var replies = result.Stdout.Split('\n').Where(l => l.Trim().Length > 0)
                    .Select(l => JsonDocument.Parse(l).RootElement).ToArray();
                Assert.All(replies, r => Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText()));

                string[] Phonemes(JsonElement reply) => reply.GetProperty("parts")[0].GetProperty("phonemes")
                    .EnumerateArray().Select(p => p.GetProperty("phoneme").GetString()!).ToArray();
                var before = Phonemes(replies[1]);
                var after = Phonemes(replies[3]);
                Assert.NotEmpty(before);
                // The edit is phonemized before the next reply: no stale phonemes.
                Assert.NotEqual(before, after);

                // A search narrows the singer's phonemizers, and a trial sings the words outside the project.
                var found = replies[11].GetProperty("phonemizers").EnumerateArray().ToArray();
                Assert.InRange(found.Length, 1, 5);
                Assert.Equal(phonemizer, found[0].GetProperty("id").GetString());
                Assert.Equal(2, found[0].GetProperty("trial").GetProperty("sung").GetInt32());
                Assert.Equal(replies[10].GetProperty("revision").GetInt64(), replies[11].GetProperty("revision").GetInt64());

                // Pitch written before a note moved is not rewritten by the default mode, and the render says so.
                string[] Warnings(JsonElement reply) => reply.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
                Assert.Empty(Warnings(replies[4]).Where(w => w.Contains("notes changed")));
                Assert.True(replies[5].GetProperty("written").GetBoolean());
                Assert.False(replies[7].GetProperty("written").GetBoolean());
                Assert.Contains(Warnings(replies[8]), w => w.Contains("notes changed"));
                Assert.True(replies[9].GetProperty("written").GetBoolean());
                Assert.Equal("load_pitch", replies[10].GetProperty("undone").GetString());

                // Two beats at 120 bpm.
                long samples = (new FileInfo(wav).Length - 44) / 2;
                Assert.InRange(samples, 44100 - 64, 44100 + 64);
            } finally {
                Directory.Delete(dir, true);
            }
        }
    }
}
