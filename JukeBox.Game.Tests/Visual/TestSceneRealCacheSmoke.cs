#nullable enable

// Machine-local smoke test: loads REAL cached maps of all four modes through the full
// BeatmapVisuals stack with the lazer gameplay layer on, starting mid-song, seeking forward and
// backward (frame stability), and switching difficulty. Skips (per mode) when no JukeBox cache
// with a map of that mode exists on this machine — this is an opt-in extra layer of confidence on
// dev machines, not a CI gate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Timing;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneRealCacheSmoke : JukeBoxTestScene
    {
        private static readonly string cache_dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JukeBox", "cache");

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        private readonly ManualClock manual = new ManualClock();

        // BeatmapVisuals expects its playback clock to be pumped externally (PlaybackController
        // does this in production); this wrapper container pumps it once per frame instead.
        private osu.Framework.Graphics.Containers.Container pump(FramedClock clock, Drawable child)
            => new osu.Framework.Graphics.Containers.Container
            {
                RelativeSizeAxes = Axes.Both,
                Clock = clock,
                Child = child,
            };

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("enable chart + hitsounds", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, true);
                config.SetValue(JukeBoxSetting.PlayHitSounds, true);
            });
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            AddStep("restore settings", () =>
            {
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.PlayHitSounds, false);
            });
        }

        [TestCase(0), TestCase(1), TestCase(2), TestCase(3)]
        public void RealMapRendersSeeksAndSwitches(int mode)
        {
            BeatmapVisuals visuals = null!;
            CachedBeatmapSet set = null!;
            string osu = null!;
            Drawable wrapper = null!;

            if (!Directory.Exists(cache_dir) || findSetWithMode(mode) == null)
            {
                Assert.Ignore($"no cached beatmap of mode {mode} on this machine");
                return;
            }

            AddStep("find real cached map", () =>
            {
                (set, osu) = findSetWithMode(mode)!.Value;
                manual.CurrentTime = 30000; // mid-song start, the production-normal case
            });

            AddStep("create visuals", () =>
            {
                var clock = new FramedClock(manual);
                Add(wrapper = pump(clock, visuals = new BeatmapVisuals(set, clock, osu)
                {
                    RelativeSizeAxes = Axes.Both,
                }));
            });

            AddUntilStep("visuals loaded", () => visuals.IsLoaded);
            AddAssert("chart unavailable reason is null", () => visuals.ChartUnavailableReason == null);
            AddUntilStep("chart layer present", () => visuals.HasChartLayer);
            AddUntilStep("chart has objects", () => visuals.ChartObjectCount > 0);
            AddAssert("hitsounds active", () => visuals.HasHitSoundPlayer);

            AddUntilStep("frame-stable clock reached mid-song", () => frameStableNear(visuals, 30000));

            AddStep("seek forward", () => manual.CurrentTime = 60000);
            AddUntilStep("clock followed forward seek", () => frameStableNear(visuals, 60000));

            AddStep("seek backward", () => manual.CurrentTime = 10000);
            AddUntilStep("clock followed backward seek", () => frameStableNear(visuals, 10000));

            AddStep("remove visuals", () => Remove(wrapper, true));

            // Difficulty switch: rebuild on another diff of the same set, as DifficultySwitcher does.
            AddStep("switch difficulty", () =>
            {
                string other = set.OsuFiles.FirstOrDefault(f => f != osu) ?? osu;
                var clock = new FramedClock(manual);
                Add(wrapper = pump(clock, visuals = new BeatmapVisuals(set, clock, other)
                {
                    RelativeSizeAxes = Axes.Both,
                }));
            });

            AddUntilStep("switched visuals loaded", () => visuals.IsLoaded);
            AddUntilStep("switched chart present or reason recorded",
                () => visuals.HasChartLayer || visuals.ChartUnavailableReason != null);

            AddStep("remove visuals", () => Remove(wrapper, true));
        }

        private static bool frameStableNear(BeatmapVisuals visuals, double target)
        {
            var clock = visuals.ChartRenderer?.DrawableRuleset?.FrameStableClock;
            return clock != null && !clock.IsCatchingUp.Value && Math.Abs(clock.CurrentTime - target) < 200;
        }

        private static (CachedBeatmapSet set, string osu)? findSetWithMode(int mode)
        {
            foreach (string dir in Directory.EnumerateDirectories(cache_dir))
            {
                foreach (string osu in Directory.EnumerateFiles(dir, "*.osu"))
                {
                    var lines = File.ReadLines(osu).Take(30);
                    if (lines.Any(l => l.Trim() == $"Mode: {mode}" || l.Trim() == $"Mode:{mode}"))
                    {
                        var cache = new BeatmapCache(Path.Combine(Path.GetTempPath(), "smoke-unused"),
                            new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(Path.GetTempPath(), "smoke-unused.osz")));
                        var set = cache.LoadFromDirectory(int.TryParse(Path.GetFileName(dir), out int id) ? id : 0, dir);
                        return (set, osu);
                    }
                }
            }

            return null;
        }
    }
}
