#nullable enable

// Machine-local diagnostic: drives a REAL taiko beatmap through the REAL production clock chain
// (PlaybackController's DecouplingFramedClock, backed by a genuine BASS-decoded audio track) —
// unlike every other taiko geometry test, which uses a synthetic ManualClock with discrete,
// jitter-free time jumps. Samples a DrawableHit's screen-space Y and Alpha on every real engine
// frame around its hit time, to check whether the post-hit "gravity" fly-away animation
// progresses smoothly (as it does under the synthetic clock) or stalls/oscillates under a real,
// possibly-jittery audio-driven clock. Skipped when no cached taiko beatmap exists on this
// machine — this is an opt-in extra layer of confidence on dev machines, not a CI gate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Screens;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Testing;
using osu.Game.Rulesets.Taiko.Objects.Drawables;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneTaikoFlyupJitter : JukeBoxTestScene
    {
        private static readonly string cache_dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JukeBox", "cache");

        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        private PlaybackController controller = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("enable chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));
            AddStep("create controller", () => Add(controller = new PlaybackController()));
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            AddStep("restore settings", () => config.SetValue(JukeBoxSetting.RenderChart, false));
        }

        private class FrameSample
        {
            public double ClockTime;
            public double NoteY;
            public float NoteAlpha;
            public string State = "";
        }

        // Polls every real Update() call — piggybacks on the scene's own draw loop rather than a
        // second clock, so samples land on genuine engine frames (with whatever real jitter the
        // production clock chain has that frame).
        private partial class Sampler : CompositeDrawable
        {
            public readonly List<FrameSample> Samples = new List<FrameSample>();
            public Func<DrawableHit?>? GetNote;
            public Func<double>? GetClockTime;
            public bool Active;

            protected override void Update()
            {
                base.Update();

                if (!Active)
                    return;

                var note = GetNote?.Invoke();

                Samples.Add(new FrameSample
                {
                    ClockTime = GetClockTime?.Invoke() ?? double.NaN,
                    NoteY = note?.Y ?? double.NaN,
                    NoteAlpha = note?.Alpha ?? -1,
                    State = note?.State.Value.ToString() ?? "absent",
                });
            }
        }

        [Test]
        public void HitNoteFlyAwayProgressesSmoothlyUnderRealPlaybackClock()
        {
            var found = findTaikoSet();

            if (found == null)
            {
                Assert.Ignore("no cached taiko beatmap on this machine");
                return;
            }

            (CachedBeatmapSet set, string osu, double firstHitTime) = found.Value;

            BeatmapVisuals visuals = null!;
            var sampler = new Sampler();

            AddStep("add sampler", () => Add(sampler));

            AddStep("play real audio", () => _ = controller.PlayAsync(set));
            AddUntilStep("clock advances", () => controller.CurrentTimeMs > 0);

            AddStep("create visuals off the REAL playback clock", () =>
                Add(visuals = new BeatmapVisuals(set, controller.PlaybackClock, osu) { RelativeSizeAxes = Axes.Both }));

            AddUntilStep("chart loaded", () => visuals.IsLoaded && visuals.HasChartLayer && visuals.ChartRenderer?.DrawableRuleset != null);
            AddAssert("taiko ruleset chosen", () => visuals.ChartRenderer!.Ruleset?.GetType() == typeof(osu.Game.Rulesets.Taiko.TaikoRuleset));

            AddStep("wire sampler", () =>
            {
                sampler.GetClockTime = () => controller.CurrentTimeMs;
                sampler.GetNote = () => visuals.ChartRenderer?.DrawableRuleset?.Playfield.AllHitObjects
                                                .OfType<DrawableHit>()
                                                .FirstOrDefault(n => Math.Abs(n.HitObject.StartTime - firstHitTime) < 1);
            });

            // Real playback runs at real speed: waiting until we're within 400ms of the hit is
            // just waiting real wall-clock time (rate 1x), same as a user watching the song.
            AddUntilStep("close to the hit", () => controller.CurrentTimeMs > firstHitTime - 400);

            AddStep("start sampling", () => sampler.Active = true);

            // Cover the whole ~900ms fly-away window (gravity_travel_height's 300ms rise + 600ms
            // fall) plus margin, at real speed.
            AddUntilStep("sampled past the animation window", () => controller.CurrentTimeMs > firstHitTime + 1200);

            AddStep("stop sampling", () => sampler.Active = false);

            AddAssert("collected a meaningful number of real frames", () => sampler.Samples.Count > 100);

            // The real production clock chain (BASS-decoded audio -> PlaybackController's
            // DecouplingFramedClock -> FrameStabilityContainer) must never present time moving
            // backwards to gameplay — a backward step would make FrameStabilityContainer treat it
            // as a rewind (see its `direction`/IsRewinding handling), which is the exact
            // mechanism suspected of stalling/replaying the post-hit transform.
            AddAssert("clock time never moves backwards across sampled frames", () =>
            {
                var samples = sampler.Samples;
                for (int i = 1; i < samples.Count; i++)
                {
                    if (samples[i].ClockTime < samples[i - 1].ClockTime)
                        return false;
                }

                return true;
            });

            // DrawableHit.UpdateHitStateTransforms: this.MoveToY(-200, 300ms, Out).Then().MoveToY(400, 600ms, In).
            // The note must actually reach close to its -200 peak (proves the rise isn't stalling
            // partway) within a real-time window around the expected 300ms mark.
            AddAssert("note reaches close to its -200 peak within the expected real-time window", () =>
            {
                var peak = sampler.Samples
                                  .Where(s => s.ClockTime >= firstHitTime && s.ClockTime <= firstHitTime + 500)
                                  .OrderBy(s => s.NoteY)
                                  .FirstOrDefault();

                return peak != null && peak.NoteY <= -175;
            });

            // The whole fly-away (rise + fall + fade) must actually COMPLETE within its designed
            // ~900ms — not hang indefinitely mid-flight (the reported "floating note" symptom).
            // By this point the DrawableHit has usually been expired/pooled already (LifetimeEnd
            // is reached and it's returned to the pool, so it's simply absent from AllHitObjects
            // — itself proof the animation finished cleanly); if it's still present, it must be
            // at/near its resting Y with zero alpha, not stuck mid-flight.
            AddAssert("animation completes (note gone or fully faded at its resting Y) within ~1.1s of the hit", () =>
            {
                var late = sampler.Samples.Where(s => s.ClockTime >= firstHitTime + 1000).ToList();
                return late.Count > 0 && late.All(s => s.State == "absent" || (s.NoteY >= 350 && s.NoteAlpha <= 0.01f));
            });

            AddStep("log full trajectory (for manual inspection)", () =>
            {
                var samples = sampler.Samples;
                Logger.Log($"[FLYUP] collected {samples.Count} frames from t={samples.FirstOrDefault()?.ClockTime:N1} to t={samples.LastOrDefault()?.ClockTime:N1}");
            });

            AddStep("remove visuals", () => Remove(visuals, true));
        }

        private static (CachedBeatmapSet set, string osu, double firstHitTime)? findTaikoSet()
        {
            if (!Directory.Exists(cache_dir))
                return null;

            foreach (string dir in Directory.EnumerateDirectories(cache_dir))
            {
                foreach (string osuFile in Directory.EnumerateFiles(dir, "*.osu"))
                {
                    var lines = File.ReadAllLines(osuFile);

                    if (!lines.Any(l => l.Trim() is "Mode: 1" or "Mode:1"))
                        continue;

                    int hoIndex = Array.IndexOf(lines, "[HitObjects]");
                    if (hoIndex < 0 || hoIndex + 1 >= lines.Length)
                        continue;

                    string[] parts = lines[hoIndex + 1].Split(',');
                    if (parts.Length < 3 || !double.TryParse(parts[2], out double firstHitTime))
                        continue;

                    var cache = new BeatmapCache(Path.Combine(Path.GetTempPath(), "flyup-unused"),
                        new JukeBox.Game.Tests.Beatmaps.FileMirror(Path.Combine(Path.GetTempPath(), "flyup-unused.osz")));
                    var set = cache.LoadFromDirectory(int.TryParse(Path.GetFileName(dir), out int id) ? id : 0, dir);

                    if (set.AudioFile == null)
                        continue;

                    return (set, osuFile, firstHitTime);
                }
            }

            return null;
        }
    }
}
