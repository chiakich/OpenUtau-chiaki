#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

using OpenUtau.Core.Agent;

namespace OpenUtau.Cli {
    public class ArgsTest {
        [Fact]
        public void ReadsCommandPositionalsAndOptions() {
            var args = Args.Parse(new[] {
                "render", "song.ustx", "--out", "a.wav", "--seed=3", "--curve", "velc=150", "--curve", "genc=-20", "--pretty",
            });
            Assert.Equal("render", args.Command);
            Assert.Equal(new[] { "song.ustx" }, args.Positionals);
            Assert.Equal("a.wav", args.Get("out"));
            Assert.Equal(3, args.GetInt("seed"));
            Assert.Equal(new[] { "velc=150", "genc=-20" }, args.GetAll("curve"));
            Assert.True(args.Flag("pretty"));
            Assert.False(args.Flag("verbose"));
            Assert.Null(args.Get("dump"));
            args.EnsureAllUsed(1);
        }

        [Fact]
        public void ANegativeNumberIsAValueNotAnOption() {
            var args = Args.Parse(new[] { "render", "--depth", "-0.5", "--track", "-1" });
            Assert.Equal(-0.5, args.GetDouble("depth"));
            Assert.Equal(-1, args.GetInt("track"));
        }

        static AgentException Usage(Action action) {
            var e = Assert.Throws<AgentException>(action);
            Assert.Equal("usage", e.Code);
            Assert.Equal(AgentException.ExitUsage, e.ExitCode);
            return e;
        }

        [Fact]
        public void AnOptionNoCommandReadIsAnError() {
            var args = Args.Parse(new[] { "phonemize", "song.ustx", "--stepz", "3" });
            Assert.Contains("--stepz", Usage(() => args.EnsureAllUsed(1)).Message);
        }

        [Fact]
        public void RejectsMalformedArguments() {
            Usage(() => Args.Parse(new[] { "render", "--out" }));
            Usage(() => Args.Parse(new[] { "render", "--pretty=yes" }));
            Usage(() => Args.Parse(new[] { "render", "--seed", "x" }).GetInt("seed"));
            Usage(() => Args.Parse(new[] { "render", "--depth", "nan" }).GetDouble("depth"));
            Usage(() => Args.Parse(new[] { "render", "--out", "a", "--out", "b" }).Get("out"));
            Usage(() => Args.Parse(new[] { "render", "a", "b" }).EnsureAllUsed(1));
            Usage(() => Args.Parse(new[] { "render" }).Positional(0, "the project file"));
        }
    }

    public class MainLoopTest {
        [Fact]
        public void RunsWorkOnItsOwnThreadAndReturnsTheResult() {
            using var loop = new MainLoop();
            Assert.NotEqual(Thread.CurrentThread, loop.Thread);
            Assert.Equal(loop.Thread, loop.Invoke(() => Thread.CurrentThread));
            // Nested calls from the loop thread run inline instead of deadlocking.
            Assert.Equal(7, loop.Invoke(() => loop.Invoke(() => 7)));
        }

        [Fact]
        public async Task TasksOnItsSchedulerRunOnItsThread() {
            using var loop = new MainLoop();
            var thread = await Task.Factory.StartNew(() => Thread.CurrentThread,
                CancellationToken.None, TaskCreationOptions.None, loop.Scheduler);
            Assert.Equal(loop.Thread, thread);
        }

        [Fact]
        public void AFailureReachesTheCallerAndTheLoopKeepsRunning() {
            using var loop = new MainLoop();
            Exception? reported = null;
            loop.OnError = e => reported = e;
            Assert.Throws<InvalidOperationException>(() => loop.Invoke(() => throw new InvalidOperationException("boom")));
            loop.Post(() => throw new ArgumentException("posted"));
            Assert.Equal(3, loop.Invoke(() => 3));
            Assert.IsType<ArgumentException>(reported);
        }
    }
}
