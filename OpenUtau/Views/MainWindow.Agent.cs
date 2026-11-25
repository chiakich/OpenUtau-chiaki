using OpenUtau.App.ViewModels;

namespace OpenUtau.App.Views {
    // Fork-only: what the agent connection reads from the window, kept out of MainWindow.axaml.cs.
    public partial class MainWindow {
        internal NotesViewModel? AgentNotesViewModel => pianoRoll?.ViewModel?.NotesViewModel;
    }
}
