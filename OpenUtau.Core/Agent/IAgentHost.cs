using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Agent {
    // What an agent session needs from whatever runs OpenUtau.Core: the app, or a process without a window.
    public interface IAgentHost {
        // "headless" or "gui".
        string Kind { get; }
        TaskScheduler Scheduler { get; }
        IList<string> Warnings { get; }
        IReadOnlyList<string> Diagnostics { get; }
        // The diagnostics since the last call: a long session reports each once and keeps none.
        IReadOnlyList<string> TakeDiagnostics();
        uint? Seed { get; }

        // Runs on the thread that owns the document.
        T Invoke<T>(Func<T> func);

        void SearchInstalledSingers();
        // Makes a voicebank folder usable without installing it.
        USinger AddSingerDirectory(string directory);

        UProject OpenProject(string path);
        void LoadProject(UProject project);
        void SaveProject(UProject project, string path);

        // What the user is looking at, where there is a user: the open part, the selection, the playhead.
        JsonObject? UiState(Func<UNote, int> noteId);
        // Brings a part the agent wrote to in front of the user, unless they have another one open.
        void ShowPart(UVoicePart part, int tick);
    }

    public static class AgentHostExtensions {
        public static void Invoke(this IAgentHost host, Action action) {
            host.Invoke<object?>(() => {
                action();
                return null;
            });
        }
    }
}
