using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using OpenUtau.Core.Agent;

namespace OpenUtau.Cli {
    // "--name value", "--name=value", flags without a value, and positionals.
    sealed class Args {
        static readonly HashSet<string> flags = new HashSet<string> {
            "pretty", "verbose", "mixdown", "allow-errors", "help", "gui",
        };

        readonly Dictionary<string, List<string>> options = new Dictionary<string, List<string>>();
        readonly HashSet<string> used = new HashSet<string>();
        readonly List<string> positionals = new List<string>();

        public string Command { get; private set; } = string.Empty;
        public IReadOnlyList<string> Positionals => positionals;

        public static Args Parse(string[] argv) {
            var args = new Args();
            for (int i = 0; i < argv.Length; i++) {
                string arg = argv[i];
                if (arg == "-h") {
                    arg = "--help";
                }
                if (!arg.StartsWith("--") || arg.Length == 2) {
                    if (args.Command.Length == 0) {
                        args.Command = arg;
                    } else {
                        args.positionals.Add(arg);
                    }
                    continue;
                }
                string name = arg.Substring(2);
                string? value = null;
                int eq = name.IndexOf('=');
                if (eq >= 0) {
                    value = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
                if (flags.Contains(name)) {
                    if (value != null) {
                        throw AgentException.Usage($"--{name} takes no value.");
                    }
                    value = "true";
                } else if (value == null) {
                    if (i + 1 >= argv.Length) {
                        throw AgentException.Usage($"--{name} needs a value.");
                    }
                    value = argv[++i];
                }
                if (!args.options.TryGetValue(name, out var list)) {
                    args.options[name] = list = new List<string>();
                }
                list.Add(value);
            }
            return args;
        }

        public IReadOnlyList<string> GetAll(string name) {
            used.Add(name);
            return options.TryGetValue(name, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
        }

        public string? Get(string name) {
            var all = GetAll(name);
            if (all.Count > 1) {
                throw AgentException.Usage($"--{name} was given more than once.");
            }
            return all.Count == 0 ? null : all[0];
        }

        public bool Flag(string name) => Get(name) != null;

        public int? GetInt(string name) {
            string? value = Get(name);
            if (value == null) {
                return null;
            }
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)) {
                throw AgentException.Usage($"--{name} must be an integer, got \"{value}\".");
            }
            return result;
        }

        public double? GetDouble(string name) {
            string? value = Get(name);
            if (value == null) {
                return null;
            }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
                    || !double.IsFinite(result)) {
                throw AgentException.Usage($"--{name} must be a number, got \"{value}\".");
            }
            return result;
        }

        public string Positional(int index, string what) {
            if (index >= positionals.Count) {
                throw AgentException.Usage($"Missing {what}.");
            }
            return positionals[index];
        }

        // Call after reading every option the command knows, so a typo is an error instead of being ignored.
        public void EnsureAllUsed(int positionalCount) {
            var unknown = options.Keys.Where(name => !used.Contains(name)).OrderBy(name => name).ToArray();
            if (unknown.Length > 0) {
                throw AgentException.Usage($"Unknown option for {Command}: {string.Join(", ", unknown.Select(n => "--" + n))}.");
            }
            if (positionals.Count > positionalCount) {
                throw AgentException.Usage($"Unexpected argument: {positionals[positionalCount]}.");
            }
        }
    }
}
