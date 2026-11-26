using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUtau.Cli {
    // Stands in for the GUI's UI thread: Core posts all document work to one thread.
    sealed class MainLoop : IDisposable {
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();

        public Thread Thread { get; }
        public TaskScheduler Scheduler { get; private set; } = TaskScheduler.Default;
        public Action<Exception>? OnError;

        public MainLoop() {
            using var ready = new ManualResetEventSlim();
            Thread = new Thread(() => {
                SynchronizationContext.SetSynchronizationContext(new LoopContext(this));
                Scheduler = TaskScheduler.FromCurrentSynchronizationContext();
                ready.Set();
                foreach (var action in queue.GetConsumingEnumerable()) {
                    try {
                        action();
                    } catch (Exception e) {
                        OnError?.Invoke(e);
                    }
                }
            }) {
                IsBackground = true,
                Name = "OpenUtau main",
            };
            Thread.Start();
            ready.Wait();
        }

        public void Post(Action action) => TryPost(action);

        bool TryPost(Action action) {
            try {
                queue.Add(action);
                return true;
            } catch (InvalidOperationException) {
                // Shutting down.
                return false;
            }
        }

        public T Invoke<T>(Func<T> func) {
            if (Thread.CurrentThread == Thread) {
                return func();
            }
            T result = default!;
            ExceptionDispatchInfo? error = null;
            var done = new ManualResetEventSlim();
            bool posted = TryPost(() => {
                try {
                    result = func();
                } catch (Exception e) {
                    error = ExceptionDispatchInfo.Capture(e);
                } finally {
                    done.Set();
                }
            });
            // Nothing would ever run it, and the caller would wait forever.
            while (!done.Wait(200)) {
                if (!posted || !Thread.IsAlive) {
                    throw new ObjectDisposedException(nameof(MainLoop), "The main thread has stopped.");
                }
            }
            error?.Throw();
            return result;
        }

        public void Invoke(Action action) {
            Invoke<object?>(() => {
                action();
                return null;
            });
        }

        public void Dispose() {
            queue.CompleteAdding();
            if (Thread.CurrentThread != Thread) {
                // What was queued still runs; a stuck action must not hold the process.
                Thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        sealed class LoopContext : SynchronizationContext {
            readonly MainLoop loop;

            public LoopContext(MainLoop loop) {
                this.loop = loop;
            }

            public override void Post(SendOrPostCallback d, object? state) => loop.Post(() => d(state));
            public override void Send(SendOrPostCallback d, object? state) => loop.Invoke(() => d(state));
            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
