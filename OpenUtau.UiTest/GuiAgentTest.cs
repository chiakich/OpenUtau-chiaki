using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.App.Agent;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Agent;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.UiTest {
    // An agent session attached to the app: its requests arrive on another thread, as from the socket,
    // and edit the project the main window shows.
    public class GuiAgentTest {
        [Fact]
        public void AgentEditsTheOpenProjectAndTheUserCanUndoThem() {
            HeadlessUi.Run(() => {
                MainWindowTest.InitCore();
                HeadlessUi.Errors.Clear();
                var window = new MainWindow { Width = 1280, Height = 800 };
                GuiAgentHost? host = null;
                AgentSession? session = null;
                try {
                    window.Show();
                    window.InitProject();
                    HeadlessUi.Flush();
                    host = new GuiAgentHost(window);
                    session = new AgentSession(host);

                    var status = Send(session, "{\"op\":\"status\"}");
                    Assert.True(status.GetProperty("ok").GetBoolean(), status.GetRawText());
                    Assert.Equal("gui", status.GetProperty("host").GetString());
                    Assert.Equal(JsonValueKind.Object, status.GetProperty("ui").ValueKind);
                    var viewModel = Assert.IsType<OpenUtau.App.ViewModels.MainWindowViewModel>(window.DataContext);
                    Assert.Equal(0, viewModel.Page);

                    var added = Send(session, "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"C4\",\"1/4\"],[\"la\",\"D4\",\"1/4\"]]}");
                    Assert.True(added.GetProperty("ok").GetBoolean(), added.GetRawText());
                    long afterAdd = added.GetProperty("revision").GetInt64();
                    var part = Assert.Single(DocManager.Inst.Project.parts.OfType<UVoicePart>());
                    Assert.Equal(new[] { 60, 62 }, part.notes.Select(n => n.tone));
                    // The user must see what was edited, not the welcome page.
                    Assert.Equal(1, viewModel.Page);
                    // And the part itself, in the piano roll, as nothing else was open there.
                    var shown = DateTime.UtcNow.AddSeconds(30);
                    JsonElement ui;
                    do {
                        HeadlessUi.Flush();
                        Thread.Sleep(10);
                        ui = Send(session, "{\"op\":\"status\"}").GetProperty("ui");
                    } while (ui.GetProperty("part").ValueKind != JsonValueKind.Number && DateTime.UtcNow < shown);
                    Assert.Equal(0, ui.GetProperty("part").GetInt32());

                    // One request is one step of the app's own undo.
                    DocManager.Inst.Undo();
                    HeadlessUi.Flush();
                    Assert.Empty(DocManager.Inst.Project.parts.OfType<UVoicePart>().SelectMany(p => p.notes));

                    // The user's edit moved the project on, so a request made for the old state is refused.
                    var stale = Send(session, "{\"op\":\"add_notes\",\"notes\":[[\"la\",\"E4\",\"1/4\"]],\"expect_revision\":" + afterAdd + "}");
                    Assert.Equal("conflict", stale.GetProperty("error").GetProperty("code").GetString());

                    DocManager.Inst.Redo();
                    HeadlessUi.Flush();
                    var got = Send(session, "{\"op\":\"get\"}");
                    var notes = got.GetProperty("parts")[0].GetProperty("notes").EnumerateArray().ToArray();
                    Assert.Equal(new[] { 1, 2 }, notes.Select(n => n.GetProperty("id").GetInt32()));

                    // The project has unsaved work the user has not agreed to lose.
                    var replaced = Send(session, "{\"op\":\"new\",\"score\":{\"bpm\":120,\"singer\":\"nobody\",\"phonemizer\":\"OpenUtau.Core.DefaultPhonemizer\"}}");
                    Assert.Equal("unsaved_changes", replaced.GetProperty("error").GetProperty("code").GetString());
                    // The app does not run in the agent's folder, and a missing file is the agent's mistake.
                    var relative = Send(session, "{\"op\":\"open\",\"path\":\"song.ustx\",\"discard_changes\":true}");
                    Assert.Equal("invalid_request", relative.GetProperty("error").GetProperty("code").GetString());
                    var missing = Send(session, "{\"op\":\"open\",\"path\":\"/nowhere/song.ustx\",\"discard_changes\":true}");
                    Assert.Equal("file_not_found", missing.GetProperty("error").GetProperty("code").GetString());
                    // And over the socket the app listens on, from a thread that is not the UI's.
                    string socket = System.IO.Path.Combine(OperatingSystem.IsWindows() ? System.IO.Path.GetTempPath() : "/tmp", $"ou-{Guid.NewGuid():N}.sock");
                    using (var server = new AgentSocketServer(session, socket)) {
                        var overSocket = Pump(Task.Run(() => AgentTransport.Send(socket, "{\"op\":\"edit_notes\",\"notes\":[{\"id\":1,\"tone\":\"G4\"}]}", TimeSpan.FromMinutes(1))));
                        Assert.True(overSocket.GetProperty("ok").GetBoolean(), overSocket.GetRawText());
                        Assert.Equal(67, part.notes.First().tone);
                    }
                    Assert.False(System.IO.File.Exists(socket));

                    var quit = Send(session, "{\"op\":\"shutdown\"}");
                    Assert.Equal("unsupported", quit.GetProperty("error").GetProperty("code").GetString());
                    Assert.False(session.ShutdownRequested);
                    // The piano roll's own binding messages aside, which it logs whoever opens it.
                    var errors = HeadlessUi.Errors.Snapshot().Where(e => !e.StartsWith("Avalonia Binding")).ToArray();
                    Assert.True(errors.Length == 0, string.Join(" | ", errors));
                } finally {
                    session?.Dispose();
                    host?.Dispose();
                    DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                    window.Close();
                    HeadlessUi.Flush();
                }
            });
        }

        // The UI thread is this test's thread: it has to keep running while the request waits on it.
        static JsonElement Send(AgentSession session, string request) {
            return Pump(Task.Run(() => session.HandleLine(request)));
        }

        static JsonElement Pump(Task<string> task) {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!task.IsCompleted) {
                Assert.True(DateTime.UtcNow < deadline, "No reply.");
                HeadlessUi.Flush();
                Thread.Sleep(5);
            }
            return JsonDocument.Parse(task.Result).RootElement;
        }
    }
}
