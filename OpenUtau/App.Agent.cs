using System;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OpenUtau.App.Agent;
using OpenUtau.App.Views;
using OpenUtau.Core.Agent;
using Serilog;

namespace OpenUtau.App {
    // Fork-only: the agent connection, a local socket that takes the same requests as openutau-cli serve.
    public partial class App {
        AgentSocketServer? agentServer;

        // Off unless asked for: any process of this user that can reach the socket can edit the open project.
        static bool AgentRequested =>
            Core.Util.Preferences.Default.AgentServer
            || Environment.GetEnvironmentVariable("OPENUTAU_AGENT") == "1";

        void StartAgentServerWhenReady() {
            if (!AgentRequested || !(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)) {
                return;
            }
            // The splash window comes first; the document is ready once the main window is up.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => {
                if (!(desktop.MainWindow is MainWindow window) || !(window.DataContext is ViewModels.MainWindowViewModel)) {
                    return;
                }
                timer.Stop();
                try {
                    var host = new GuiAgentHost(window);
                    agentServer = new AgentSocketServer(new AgentSession(host), AgentTransport.GuiSocketPath);
                    // Before the document goes away: a request arriving later is refused instead of lost.
                    window.Closed += (_, _) => agentServer?.Dispose();
                    Log.Information($"Agent connection listening on {agentServer.Path}");
                } catch (Exception e) {
                    Log.Error(e, "Failed to start the agent connection.");
                }
            };
            timer.Start();
            desktop.Exit += (_, _) => agentServer?.Dispose();
        }
    }
}
