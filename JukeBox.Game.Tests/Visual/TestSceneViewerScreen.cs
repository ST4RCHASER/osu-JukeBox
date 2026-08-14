#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using JukeBox.Game.Configuration;
using JukeBox.Game.Detach;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Utils;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The detached viewer window's screen, driven headless: sync snapshots are fed through a
    /// test reader standing in for the viewer process's stdin, and the screen must build the
    /// real <see cref="BeatmapVisuals"/> stack for the referenced on-disk set, keep its clock on
    /// the reported position, mirror settings into config, and request exit on EOF or a
    /// protocol-version mismatch (in the real process that exit closes the window, which the
    /// main process observes to uncheck the setting).
    /// </summary>
    [TestFixture]
    public partial class TestSceneViewerScreen : JukeBoxTestScene
    {
        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        private string tmp = null!;
        private string setDir = null!;

        private FeedReader? reader;
        private ViewerScreen screen = null!;
        private int exitRequests;

        // No [TearDown] deleting tmp: NUnit runs a derived class's teardown BEFORE the base
        // TestScene teardown that actually executes the queued steps, so a deletion there rips
        // the fixture out from under the test. Best-effort cleanup happens in TearDownSteps
        // (a step, so correctly ordered); anything left leaks to the OS temp dir, same as
        // TestSceneChartGating's fixtures.
        [SetUp]
        public void SetUp()
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            // Same fixture layout the cache extracts to (see TestSceneChartGating).
            setDir = Path.Combine(tmp, "set");
            Directory.CreateDirectory(setDir);
            File.Copy(fixture("happy_people_easy.osu"), Path.Combine(setDir, "happy [Easy].osu"));
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            // Snapshots write straight into the shared test-browser config — put back the
            // defaults so later scenes aren't affected, and unblock the reader thread.
            AddStep("restore config + close feed", () =>
            {
                config.SetValue(JukeBoxSetting.BackgroundDim, 0.3);
                config.SetValue(JukeBoxSetting.RenderChart, false);
                reader?.SignalEof();

                try
                {
                    Directory.Delete(tmp, true);
                }
                catch (IOException)
                {
                }
            });
        }

        private static string fixture(string name)
            => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name);

        private void pushScreen()
        {
            reader = new FeedReader();
            exitRequests = 0;

            var stack = new ScreenStack { RelativeSizeAxes = Axes.Both };
            Child = stack;
            stack.Push(screen = new ViewerScreen(reader)
            {
                // The real screen exits its GameHost — which here would be the test browser's.
                ExitAction = () => exitRequests++,
            });
        }

        private ViewerSyncState makeState(double positionMs, bool playing) => new ViewerSyncState
        {
            SetId = 5880,
            SetDirectory = setDir,
            PositionMs = positionMs,
            Rate = 1,
            Playing = playing,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BackgroundDim = 0.3,
            PlayfieldZoom = 1,
            Skin = nameof(JukeBoxSkin.Argon),
        };

        [Test]
        public void SnapshotBuildsVisualsAndDrivesClock()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("send playing snapshot at 5s", () => reader.Push(makeState(5000, true).ToJson()));

            AddUntilStep("visuals built for the set", () => screen.CurrentVisuals?.IsLoaded == true);
            AddUntilStep("clock running from reported position", () => screen.SyncClock.IsRunning && screen.SyncClock.CurrentTime >= 5000);

            AddStep("send paused snapshot at 2s", () => reader.Push(makeState(2000, false).ToJson()));
            AddUntilStep("clock paused exactly at 2s", () => !screen.SyncClock.IsRunning && Precision.AlmostEquals(screen.SyncClock.CurrentTime, 2000));

            AddAssert("no drift snaps were needed", () => screen.SyncClock.SnapCount == 0);
        }

        [Test]
        public void SettingsMirrorIntoConfig()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("send snapshot with dim 0.7 + chart on", () =>
            {
                var state = makeState(0, false);
                state.BackgroundDim = 0.7;
                state.RenderChart = true;
                reader.Push(state.ToJson());
            });

            AddUntilStep("dim applied to config", () => Precision.AlmostEquals(config.Get<double>(JukeBoxSetting.BackgroundDim), 0.7));
            AddUntilStep("chart applied to config", () => config.Get<bool>(JukeBoxSetting.RenderChart));
        }

        [Test]
        public void VersionMismatchRequestsExit()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("send foreign-version snapshot", () =>
            {
                var state = makeState(0, false);
                state.Version = 999;
                reader.Push(state.ToJson());
            });

            AddUntilStep("exit requested", () => exitRequests == 1);
        }

        [Test]
        public void EofRequestsExit()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("close the feed", () => reader.SignalEof());

            AddUntilStep("exit requested", () => exitRequests == 1);
        }

        /// <summary>
        /// Stands in for the viewer process's stdin: blocks on <see cref="ReadLine"/> until a
        /// line is pushed (a StringReader would EOF the screen the moment it ran dry).
        /// </summary>
        private class FeedReader : TextReader
        {
            private readonly BlockingCollection<string> lines = new BlockingCollection<string>();

            public void Push(string line) => lines.Add(line);

            public void SignalEof()
            {
                if (!lines.IsAddingCompleted)
                    lines.CompleteAdding();
            }

            public override string? ReadLine()
            {
                try
                {
                    return lines.Take();
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding with an empty collection — the EOF signal.
                    return null;
                }
            }
        }
    }
}
