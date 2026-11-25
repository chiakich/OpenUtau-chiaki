using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;
using OpenUtau.App.ViewModels;
using OpenUtau.App.Views;
using OpenUtau.Core;
using OpenUtau.Core.Agent;
using OpenUtau.Core.Ustx;
using ReactiveUI;

namespace OpenUtau.App.Agent {
    // Lets an agent session work on the project the user has open: its edits show up live and can be undone.
    public sealed class GuiAgentHost : IAgentHost, ICmdSubscriber, IDisposable {
        const int MaxDiagnostics = 100;
        readonly MainWindow window;
        readonly List<string> diagnostics = new List<string>();

        public string Kind => "gui";
        public TaskScheduler Scheduler => DocManager.Inst.MainScheduler;
        public IList<string> Warnings { get; } = new List<string>();
        public uint? Seed => null;

        public IReadOnlyList<string> Diagnostics {
            get {
                lock (diagnostics) {
                    return diagnostics.ToArray();
                }
            }
        }

        public GuiAgentHost(MainWindow window) {
            this.window = window;
            Invoke(() => {
                DocManager.Inst.AddSubscriber(this);
                return true;
            });
        }

        public T Invoke<T>(Func<T> func) => Dispatcher.UIThread.CheckAccess() ? func() : Dispatcher.UIThread.Invoke(func);

        // The app keeps its own singer list current.
        public void SearchInstalledSingers() { }

        public USinger AddSingerDirectory(string directory) {
            throw new AgentException("unsupported",
                "The app only uses installed singers: install the voicebank, or add its parent folder in Preferences > Paths.");
        }

        public UProject OpenProject(string path) {
            return Invoke(() => {
                ViewModel.OpenProject(new[] { path });
                return DocManager.Inst.Project;
            });
        }

        public void LoadProject(UProject project) {
            Invoke(() => {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                DocManager.Inst.Recovered = false;
                // Loading validated against the previous document; phonemize again now that this one is current.
                project.ValidateFull();
                ViewModel.RaisePropertyChanged(nameof(MainWindowViewModel.Title));
                return true;
            });
        }

        public void SaveProject(UProject project, string path) {
            Invoke(() => {
                AgentOps.Save(project, path);
                ViewModel.RaisePropertyChanged(nameof(MainWindowViewModel.Title));
                return true;
            });
        }

        public JsonObject? UiState(Func<UNote, int> noteId) {
            return Invoke(() => {
                var project = DocManager.Inst.Project;
                var notes = window.AgentNotesViewModel;
                var part = notes?.Part;
                var selection = new JsonArray();
                if (notes != null && part != null) {
                    foreach (var note in notes.Selection.OrderBy(n => n.position)) {
                        selection.Add(noteId(note));
                    }
                }
                return new JsonObject {
                    ["part"] = part == null ? null : project.parts.IndexOf(part),
                    ["selected_notes"] = selection,
                    ["playhead_tick"] = DocManager.Inst.playPosTick,
                    ["playing"] = PlaybackManager.Inst.PlayingMaster,
                };
            });
        }

        public void OnNext(UCommand cmd, bool isUndo) {
            if (cmd is ErrorMessageNotification error) {
                string message = error.e is MessageCustomizableException mce
                    ? $"{mce.Message} {mce.SubstanceException?.Message}".Trim()
                    : $"{error.message} {error.e?.Message}".Trim();
                lock (diagnostics) {
                    if (diagnostics.Count < MaxDiagnostics) {
                        diagnostics.Add(message);
                    }
                }
            }
        }

        public void Dispose() {
            Invoke(() => {
                DocManager.Inst.RemoveSubscriber(this);
                return true;
            });
        }

        MainWindowViewModel ViewModel => window.DataContext as MainWindowViewModel
            ?? throw new AgentException("not_ready", "The app's main window is not ready yet.");
    }
}
