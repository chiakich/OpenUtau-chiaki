using System.Threading.Tasks;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.App.Views {
    // Fork-only: what the agent connection reads from the window, kept out of MainWindow.axaml.cs.
    public partial class MainWindow {
        internal NotesViewModel? AgentNotesViewModel => pianoRoll?.ViewModel?.NotesViewModel;

        // As PartsCanvasDoubleTapped, for a part an agent wrote to. A part the user has open stays open.
        internal async Task AgentShowPart(UVoicePart part, int tick) {
            if (AgentNotesViewModel?.Part != null || !DocManager.Inst.Project.parts.Contains(part)) {
                return;
            }
            if (pianoRoll == null) {
                var model = await Task.Run(() => new PianoRollViewModel());
                if (pianoRoll == null) {
                    pianoRoll = new PianoRoll(model) { MainWindow = this };
                    if (Preferences.Default.DetachPianoRoll) {
                        viewModel.ShowPianoRoll = false;
                        pianoRollWindow = new(pianoRoll);
                    } else {
                        PianoRollContainer.Content = pianoRoll;
                    }
                    pianoRoll.InitializePianoRollWindowAsync();
                    pianoRoll.ViewModel.PlaybackViewModel = viewModel.PlaybackViewModel;
                    pianoRoll.ViewModel.PianoRollFullscreen = viewModel.PianoRollFullscreen;
                }
            }
            if (AgentNotesViewModel?.Part != null || !DocManager.Inst.Project.parts.Contains(part)) {
                return;
            }
            if (pianoRollWindow != null) {
                pianoRollWindow.Show();
            } else {
                viewModel.ShowPianoRoll = true;
            }
            DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, DocManager.Inst.Project, tick));
            pianoRoll.AttachExpressions();
        }
    }
}
