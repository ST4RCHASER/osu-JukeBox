#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using JukeBox.Game.Configuration;
using JukeBox.Game.Detach;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using JukeBox.Game.Screens;
using JukeBox.Game.Tests.Import;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;

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

        [Resolved]
        private SkinSelection skinSelection { get; set; } = null!;

        [Resolved]
        private BeatmapOffsetStore offsets { get; set; } = null!;

        [Resolved]
        private ReplayStore replays { get; set; } = null!;

        [Resolved]
        private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

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
                skinSelection.SetExternalCustomSkinDirectory(null);

                if (rulesetConfigs.GetConfigFor(new ManiaRuleset()) is ManiaRulesetConfigManager mania)
                    mania.SetValue(ManiaRulesetSetting.ScrollSpeed, 8.0);

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
            Settings = new Dictionary<string, string>
            {
                ["jukebox:BackgroundDim"] = "0.3",
                ["jukebox:PlayfieldZoom"] = "1",
            },
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
                state.Settings["jukebox:BackgroundDim"] = "0.7";
                state.Settings["jukebox:RenderChart"] = "1";
                reader.Push(state.ToJson());
            });

            AddUntilStep("dim applied to config", () => Precision.AlmostEquals(config.Get<double>(JukeBoxSetting.BackgroundDim), 0.7));
            AddUntilStep("chart applied to config", () => config.Get<bool>(JukeBoxSetting.RenderChart));
        }

        /// <summary>
        /// A LATER snapshot changing a setting must move it — the whole difference between "honours
        /// the setting" and "honours it once at startup and then freezes" (which is what a
        /// weakly-held <c>GetBindable</c> copy would do). Same reason this asserts on a second
        /// value rather than just the first.
        /// </summary>
        [Test]
        public void LaterSnapshotsKeepMovingSettings()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("send dim 0.7", () => push(s => s.Settings["jukebox:BackgroundDim"] = "0.7"));
            AddUntilStep("dim is 0.7", () => Precision.AlmostEquals(config.Get<double>(JukeBoxSetting.BackgroundDim), 0.7));

            AddStep("collect garbage", () =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            });

            AddStep("send dim 0.1", () => push(s => s.Settings["jukebox:BackgroundDim"] = "0.1"));
            AddUntilStep("dim is 0.1", () => Precision.AlmostEquals(config.Get<double>(JukeBoxSetting.BackgroundDim), 0.1));
        }

        /// <summary>
        /// The mania scroll speed stands in for every per-ruleset row (the osu! snaking/cursor/
        /// analysis ones, taiko's hit animations, mania's direction and note colouring): they all
        /// travel through the same registry into the same realm-backed managers, so one of them
        /// arriving proves the channel for all of them. Coverage of the registry as a WHOLE is
        /// <see cref="TestSceneSettingsMirror"/>'s job.
        /// </summary>
        [Test]
        public void RulesetSettingsMirrorIntoTheirOwnConfig()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("send mania scroll speed 22", () => push(s => s.Settings["ruleset:ManiaRulesetSetting.ScrollSpeed"] = "22"));

            AddUntilStep("mania config took it", () => maniaScrollSpeed() == 22);

            AddStep("send mania scroll speed 9", () => push(s => s.Settings["ruleset:ManiaRulesetSetting.ScrollSpeed"] = "9"));
            AddUntilStep("mania config moved again", () => maniaScrollSpeed() == 9);
        }

        [Test]
        public void CustomSkinDirectoryComesFromTheSnapshotNotLocalStorage()
        {
            string skinDir = null!;

            AddStep("push viewer screen", pushScreen);
            AddStep("send a custom skin directory", () =>
            {
                skinDir = Path.Combine(tmp, "skins", "Aristia");
                Directory.CreateDirectory(skinDir);
                push(s => s.CustomSkinDirectory = skinDir);
            });

            // Nothing was imported into THIS process's storage, so without the override the
            // resolved directory would be null and the imported skin would degrade to Argon.
            AddUntilStep("resolved against the sent path", () => skinSelection.CustomSkinDirectory == skinDir);

            AddStep("send no custom skin", () => push(s => s.CustomSkinDirectory = null));
            AddUntilStep("back to nothing imported", () => skinSelection.CustomSkinDirectory == null);
        }

        [Test]
        public void BeatmapAudioOffsetApplies()
        {
            AddStep("push viewer screen", pushScreen);
            AddStep("send -40ms for this mapset", () => push(s => s.BeatmapAudioOffset = -40));
            AddUntilStep("offset store took it", () => Precision.AlmostEquals(offsets.CurrentOffset.Value, -40));

            AddStep("send +25ms", () => push(s => s.BeatmapAudioOffset = 25));
            AddUntilStep("offset store moved", () => Precision.AlmostEquals(offsets.CurrentOffset.Value, 25));
        }

        /// <summary>
        /// A replay crosses the pipe as a PATH; the viewer decodes it into its own registry so the
        /// chart plays the real play rather than autoplay, exactly as the main window does.
        /// </summary>
        [Test]
        public void ReplayIsDecodedFromItsPathAndRegistered()
        {
            string osuFile = null!;
            string osrPath = null!;

            AddStep("push viewer screen", pushScreen);
            AddStep("write a genuine .osr for the fixture difficulty", () =>
            {
                osuFile = Path.Combine(setDir, "happy [Easy].osu");
                osrPath = Path.Combine(tmp, "someone - happy.osr");
                ReplayFixture.Write(osrPath, osuFile, "someone");
            });

            AddStep("send the replay's path", () => push(s =>
            {
                s.ReplayOsrPath = osrPath;
                s.ReplayOsuFile = osuFile;
            }));

            AddUntilStep("registered against its difficulty", () => replays.ForOsuFile(osuFile)?.Score != null);
        }

        private void push(Action<ViewerSyncState> mutate)
        {
            var state = makeState(0, false);
            mutate(state);
            reader!.Push(state.ToJson());
        }

        private double maniaScrollSpeed()
            => rulesetConfigs.GetConfigFor(new ManiaRuleset()) is ManiaRulesetConfigManager mania
                ? mania.Get<double>(ManiaRulesetSetting.ScrollSpeed)
                : double.NaN;

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
