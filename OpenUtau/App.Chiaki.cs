using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using OpenUtau.App.Views;
using Serilog;

namespace OpenUtau.App {
    // Fork-only App behavior, kept out of App.axaml.cs so upstream merges stay clean.
    public partial class App {
        const string ChiakiStringsPrefix = "chiaki-strings-";

        // macOS delivers Finder double-clicks as Apple Events, not argv; may arrive before MainWindow exists.
        public static string? PendingOpenFile { get; set; }

        void SubscribeFileActivation() {
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable) {
                activatable.Activated += OnActivated;
            }
        }

        void OnActivated(object? sender, ActivatedEventArgs e) {
            if (e is not FileActivatedEventArgs fileArgs) {
                return;
            }
            var file = fileArgs.Files
                .Select(item => item.TryGetLocalPath())
                .FirstOrDefault(path => !string.IsNullOrEmpty(path));
            if (file == null) {
                return;
            }
            Log.Information($"Activated with file {file}");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is MainWindow mainWindow) {
                mainWindow.OpenFromSystem(file);
            } else {
                PendingOpenFile = file;
            }
        }

        // Layered on top of the Lunai overlays so Chiaki branding wins in every locale.
        static void ApplyChiakiOverlay(string language) {
            if (Current is not { } app) {
                return;
            }
            var overlays = app.Resources.Keys.OfType<string>()
                .Where(key => key.StartsWith(ChiakiStringsPrefix))
                .ToDictionary(key => key.Substring(ChiakiStringsPrefix.Length),
                    key => app.Resources.TryGetResource(key, ThemeVariant.Default, out var res) ? res as IResourceProvider : null);
            foreach (var res in overlays.Values) {
                if (res != null) {
                    app.Resources.MergedDictionaries.Remove(res);
                }
            }
            if (overlays.TryGetValue("en-US", out var en) && en != null) {
                app.Resources.MergedDictionaries.Add(en);
            }
            if (language != "en-US" && overlays.TryGetValue(language, out var locale) && locale != null) {
                app.Resources.MergedDictionaries.Add(locale);
            }
        }
    }
}
