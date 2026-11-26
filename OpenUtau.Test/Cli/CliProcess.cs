#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OpenUtau.Cli {
    // Runs the built OpenUtau.Cli as its own process: it sets up process-wide singletons that the
    // other tests in this process rely on, and its contract is its stdout, stderr and exit code.
    static class CliProcess {
        public sealed class Result {
            public int ExitCode;
            public string Stdout = string.Empty;
            public string Stderr = string.Empty;

            public JsonElement Json() => JsonDocument.Parse(Stdout).RootElement;

            // The error is the last line of stderr; log lines may come before it.
            public JsonElement Error() {
                string last = Stderr.Split('\n').Select(l => l.Trim()).Last(l => l.Length > 0);
                return JsonDocument.Parse(last).RootElement.GetProperty("error");
            }
        }

        public static Result Run(params string[] args) => RunWithInput(null, args);

        // input is fed to stdin, which is then closed.
        public static Result RunWithInput(string? input, params string[] args) {
            using var process = Start(input != null, args);
            if (input != null) {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            return Finish(process, args);
        }

        public static Process Start(bool redirectInput, params string[] args) {
            var info = new ProcessStartInfo(FindDotnet()) {
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(FindCli());
            foreach (string arg in args) {
                info.ArgumentList.Add(arg);
            }
            return Process.Start(info)!;
        }

        public static Result Finish(Process process, params string[] args) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds)) {
                process.Kill(true);
                throw new TimeoutException($"OpenUtau.Cli {string.Join(" ", args)} did not finish.");
            }
            process.WaitForExit();
            return new Result { ExitCode = process.ExitCode, Stdout = stdout.Result, Stderr = stderr.Result };
        }

        static string FindDotnet() {
            string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath)) {
                return hostPath;
            }
            string? current = Environment.ProcessPath;
            if (current != null && Path.GetFileName(current) == name) {
                return current;
            }
            string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, name))) {
                return Path.Combine(root, name);
            }
            // The runtime this test runs on lives under <dotnet>/shared/Microsoft.NETCore.App/<version>.
            var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
            string? fromRuntime = runtime.Parent?.Parent?.Parent?.FullName;
            if (fromRuntime != null && File.Exists(Path.Combine(fromRuntime, name))) {
                return Path.Combine(fromRuntime, name);
            }
            return name;
        }

        static string FindCli() {
            // For builds that do not use the default output folders.
            string? given = Environment.GetEnvironmentVariable("OPENUTAU_CLI_DLL");
            if (!string.IsNullOrEmpty(given)) {
                return given;
            }
            string configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
                string project = Path.Combine(dir.FullName, "OpenUtau.Cli");
                if (File.Exists(Path.Combine(project, "OpenUtau.Cli.csproj"))) {
                    string dll = Path.Combine(project, "bin", configuration, "net10.0", "OpenUtau.Cli.dll");
                    if (File.Exists(dll)) {
                        return dll;
                    }
                    throw new FileNotFoundException($"OpenUtau.Cli is not built: {dll}");
                }
            }
            throw new FileNotFoundException("OpenUtau.Cli project not found above " + AppContext.BaseDirectory);
        }
    }
}
