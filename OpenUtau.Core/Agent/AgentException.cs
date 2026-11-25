using System;

namespace OpenUtau.Core.Agent {
    public sealed class AgentException : Exception {
        public const int ExitFailure = 1;
        public const int ExitUsage = 2;

        public string Code { get; }
        public int ExitCode { get; }

        public AgentException(string code, string message, int exitCode = ExitFailure, Exception? inner = null)
            : base(message, inner) {
            Code = code;
            ExitCode = exitCode;
        }

        public static AgentException Usage(string message) => new AgentException("usage", message, ExitUsage);
    }
}
