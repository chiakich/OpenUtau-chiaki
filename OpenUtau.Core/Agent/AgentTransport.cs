using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Serilog;

namespace OpenUtau.Core.Agent {
    // Carries session requests and replies as lines of JSON: over a pair of streams, or over a local socket.
    public static class AgentTransport {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // Where the app listens when its user turned the agent connection on.
        public static string GuiSocketPath => Environment.GetEnvironmentVariable("OPENUTAU_AGENT_SOCKET") is { Length: > 0 } path
            ? path
            : Path.Combine(PathManager.Inst.DataPath, "agent.sock");

        // Unix socket paths are short (104 bytes on macOS); say so instead of failing inside the runtime.
        internal static UnixDomainSocketEndPoint EndPoint(string path) {
            try {
                return new UnixDomainSocketEndPoint(path);
            } catch (ArgumentException e) {
                throw new AgentException("socket_path", $"{path} cannot be a socket path (too long?): pick a shorter one.", inner: e);
            }
        }

        // Serves until the input ends or the session is told to shut down.
        public static void Serve(AgentSession session, Stream input, Stream output, Func<bool>? accept = null) {
            using var reader = new StreamReader(input, Utf8, false, 4096, leaveOpen: true);
            string? line;
            while (!session.ShutdownRequested && (line = reader.ReadLine()) != null) {
                if (line.Trim().Length == 0) {
                    continue;
                }
                // Asked inside the session's turn: a request that waited there must not run after a close.
                string? handled = session.HandleLine(line, accept);
                if (handled == null) {
                    return;
                }
                byte[] reply = Utf8.GetBytes(handled + "\n");
                output.Write(reply, 0, reply.Length);
                output.Flush();
            }
        }

        // One request, one reply.
        public static string Send(string socketPath, string request, TimeSpan timeout) {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                socket.Connect(EndPoint(socketPath));
            } catch (SocketException e) {
                throw new AgentException("not_listening", $"Nothing is listening on {socketPath}: {e.Message}", inner: e);
            }
            socket.ReceiveTimeout = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
            using var stream = new NetworkStream(socket);
            // A request is one line; line breaks inside it are only layout.
            byte[] bytes = Utf8.GetBytes(request.Replace("\r", " ").Replace("\n", " ") + "\n");
            try {
                stream.Write(bytes, 0, bytes.Length);
                using var reader = new StreamReader(stream, Utf8);
                return reader.ReadLine() ?? throw new AgentException("no_reply", "The connection closed without a reply.");
            } catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) {
                throw new AgentException("timeout", $"No reply within {timeout.TotalSeconds:0} s.", inner: e);
            } catch (IOException e) {
                throw new AgentException("no_reply", $"The connection was lost: {e.Message}", inner: e);
            }
        }
    }

    // Listens on a Unix domain socket: reachable only from this machine, and only by this user.
    public sealed class AgentSocketServer : IDisposable {
        readonly AgentSession session;
        readonly Socket listener;
        readonly Thread thread;
        readonly CancellationTokenSource closing = new CancellationTokenSource();
        volatile bool disposed;
        readonly System.Collections.Generic.HashSet<Socket> clients = new System.Collections.Generic.HashSet<Socket>();

        public string Path { get; }
        // Set when a request asked the session to shut down.
        public ManualResetEventSlim Stopped { get; } = new ManualResetEventSlim();

        public AgentSocketServer(AgentSession session, string path) {
            this.session = session;
            Path = System.IO.Path.GetFullPath(path);
            var endPoint = AgentTransport.EndPoint(Path);
            if (File.Exists(Path)) {
                // A socket file outlives a process that did not close it; a live one must not be taken over.
                if (IsListening(Path)) {
                    throw new AgentException("socket_in_use", $"Another process is already listening on {Path}.");
                }
                // On Linux connecting to an ordinary file is refused just like a dead socket.
                if (!IsSocketFile(Path)) {
                    throw new AgentException("socket_failed", $"{Path} exists and is not a socket: it is left alone. Pick another path.");
                }
                File.Delete(Path);
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                listener.Bind(endPoint);
                if (!OperatingSystem.IsWindows()) {
                    File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                listener.Listen(8);
            } catch (Exception e) {
                listener.Dispose();
                throw new AgentException("socket_failed", $"Cannot listen on {Path}: {e.Message}", inner: e);
            }
            thread = new Thread(Accept) { IsBackground = true, Name = "Agent socket" };
            thread.Start();
        }

        // Only a path known to be a socket is deleted: anything else, or no answer, leaves it alone.
        static bool IsSocketFile(string path) {
            try {
                if (OperatingSystem.IsWindows()) {
                    // Other reparse points are links and cloud placeholders: only the socket tag counts.
                    return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && ReparseTag(path) == AfUnixTag;
                }
                // .NET does not tell the file type; the shell's test does. A link to a socket is not ours to delete.
                var info = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                foreach (string arg in new[] { "-c", "test -S \"$1\" && test ! -L \"$1\"", "sh", path }) {
                    info.ArgumentList.Add(arg);
                }
                using var test = System.Diagnostics.Process.Start(info);
                if (test == null || !test.WaitForExit(5000)) {
                    return false;
                }
                return test.ExitCode == 0;
            } catch (Exception) {
                return false;
            }
        }

        const uint AfUnixTag = 0x80000023;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct FindData {
            public uint attributes;
            public long creationTime;
            public long lastAccessTime;
            public long lastWriteTime;
            public uint sizeHigh;
            public uint sizeLow;
            public uint reserved0;
            public uint reserved1;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string fileName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 14)]
            public string alternateFileName;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string fileName, out FindData data);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr handle);

        // For a reparse point the find data carries its tag; 0 when it cannot be read.
        static uint ReparseTag(string path) {
            IntPtr handle = FindFirstFileW(path, out var data);
            if (handle == new IntPtr(-1)) {
                return 0;
            }
            FindClose(handle);
            return data.reserved0;
        }

        static bool IsListening(string path) {
            try {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(AgentTransport.EndPoint(path));
                return true;
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) {
                // A socket nobody listens on any more.
                return false;
            } catch (SocketException e) {
                // Anything else at this path is someone's file, not ours to replace.
                throw new AgentException("socket_failed", $"{path} exists and is not a socket of a session: {e.Message}", inner: e);
            }
        }

        void Accept() {
            while (!disposed) {
                Socket client;
                try {
                    // Not the blocking Accept: closing the listener would wait on it forever on macOS.
                    client = listener.AcceptAsync(closing.Token).AsTask().GetAwaiter().GetResult();
                } catch (Exception) {
                    return;
                }
                lock (clients) {
                    if (disposed) {
                        client.Dispose();
                        return;
                    }
                    clients.Add(client);
                }
                var worker = new Thread(() => {
                    try {
                        using (client)
                        using (var stream = new NetworkStream(client)) {
                            AgentTransport.Serve(session, stream, stream, () => !disposed);
                        }
                    } catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException) {
                        // The client went away.
                    } catch (Exception e) {
                        Log.Error(e, "Agent connection failed.");
                    }
                    lock (clients) {
                        clients.Remove(client);
                    }
                    if (session.ShutdownRequested) {
                        Stopped.Set();
                    }
                }) { IsBackground = true, Name = "Agent connection" };
                worker.Start();
            }
        }

        public void Dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            closing.Cancel();
            listener.Dispose();
            // A client waiting to send its next request would otherwise keep its worker, and could still send one.
            lock (clients) {
                foreach (var client in clients) {
                    try {
                        client.Dispose();
                    } catch (Exception) {
                    }
                }
                clients.Clear();
            }
            try {
                File.Delete(Path);
            } catch (Exception) {
                // Left behind, it is replaced the next time.
            }
        }
    }
}
