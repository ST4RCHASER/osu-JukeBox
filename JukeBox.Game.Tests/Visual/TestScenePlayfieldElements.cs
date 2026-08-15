#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Beatmaps;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Per-element playfield visibility: the config round-trip, and — the part that can only be
    /// proven against a real hosted DrawableRuleset — that each catalogued element is wired to a
    /// lookup the ruleset actually performs, and that hiding elements never takes gameplay down
    /// with them.
    /// </summary>
    [TestFixture]
    public partial class TestScenePlayfieldElements : JukeBoxTestScene
    {
        private JukeBoxConfigManager config = null!;
        private PlayfieldElementVisibility visibility = null!;
        private SkinSelection skinSelection = null!;

        private readonly ManualClock manual = new ManualClock();

        private string dir = null!;
        private Container host = null!;
        private LazerChartLayer layer = null!;

        // Own config (ini in temp storage) and own visibility service bound to it: the chart layer
        // built below resolves whatever this scene caches, so hiding elements here can never reach
        // the developer's real settings.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-playfield-elements-test", Path.GetRandomFileName())));
            visibility = new PlayfieldElementVisibility();
            skinSelection = new SkinSelection();

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            deps.Cache(visibility);
            deps.Cache(skinSelection);
            return deps;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(visibility);
            Add(skinSelection);
        }

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("show everything", () => config.SetValue(JukeBoxSetting.HiddenPlayfieldElements, string.Empty));
            AddUntilStep("visibility service ready", () => visibility.IsLoaded);
        }

        [Test]
        public void HiddenElementsRoundTripThroughConfig()
        {
            AddStep("hide two elements", () =>
            {
                visibility.Shown(PlayfieldElement.OsuCursor).Value = false;
                visibility.Shown(PlayfieldElement.ManiaBarLines).Value = false;
            });

            AddAssert("persisted by name, in catalogue order",
                () => config.Get<string>(JukeBoxSetting.HiddenPlayfieldElements) == "OsuCursor,ManiaBarLines");

            AddStep("a config written by something else lands",
                () => config.SetValue(JukeBoxSetting.HiddenPlayfieldElements, "Judgements,TaikoMascot"));

            AddAssert("the two named ones are hidden",
                () => visibility.IsHidden(PlayfieldElement.Judgements) && visibility.IsHidden(PlayfieldElement.TaikoMascot));
            AddAssert("and the previously hidden ones came back",
                () => !visibility.IsHidden(PlayfieldElement.OsuCursor) && !visibility.IsHidden(PlayfieldElement.ManiaBarLines));

            // A name from a newer build (or a typo) must not take the rest of the list down with it.
            AddStep("a config with junk in it lands",
                () => config.SetValue(JukeBoxSetting.HiddenPlayfieldElements, "OsuSpinner,NotAnElement,CatchCatcher"));

            AddAssert("the two real ones still applied",
                () => visibility.IsHidden(PlayfieldElement.OsuSpinner) && visibility.IsHidden(PlayfieldElement.CatchCatcher));
            AddAssert("and nothing else was hidden",
                () => PlayfieldElementCatalog.All.Count(e => visibility.IsHidden(e.Element)) == 2);
        }

        /// <summary>
        /// Everything the catalogue claims for a ruleset must be a lookup that ruleset really makes:
        /// an entry pointing at a component nobody asks for is a toggle that silently does nothing.
        ///
        /// <para>
        /// Measured with everything VISIBLE — some components only exist INSIDE another (osu!'s
        /// combo number lives in the hit circle piece), so hiding the lot would hide the very
        /// lookups being counted. And measured across both an Argon and a legacy skin, because a
        /// skin may draw a piece itself instead of asking for it: Argon's own circle piece paints
        /// its combo number directly, where the classic/triangles piece asks the skin chain for
        /// <c>HitCircleText</c>. Either counts — the entry is real as long as SOME skin asks.
        /// </para>
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void EveryCataloguedElementIsALookupTheRulesetActuallyMakes(int mode)
        {
            var lookedUp = new HashSet<PlayfieldElement>();

            foreach (var skin in new[] { JukeBoxSkin.Argon, JukeBoxSkin.Classic })
            {
                var captured = skin;

                AddStep($"select the {captured} skin", () => config.SetValue(JukeBoxSetting.Skin, captured));
                AddUntilStep("skin selection resolved", () => skinSelection.Effective.Value == captured);

                createLayer(mode);

                AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
                AddAssert("the filter is in the skin chain", () => layer.ElementFilter != null);
                AddAssert("built with the selected skin", () => layer.SelectedSkin == captured);

                playThrough();

                AddStep($"record what {captured} looked up", () =>
                {
                    foreach (var element in layer.ElementFilter!.ElementsLookedUp)
                        lookedUp.Add(element);

                    Logger.Log($"[playfield-elements] mode {mode} / {captured} looked up: "
                               + $"{string.Join(", ", layer.ElementFilter!.ElementsLookedUp.OrderBy(e => e.ToString()))}");
                });

                AddStep("remove layer", () => Remove(host, true));
            }

            AddAssert("every element listed for this ruleset was actually looked up", () =>
            {
                var expected = PlayfieldElementCatalog.ForRuleset(mode).Select(e => e.Element).ToHashSet();
                var missing = expected.Except(lookedUp).ToArray();

                if (missing.Length > 0)
                    Logger.Log($"[playfield-elements] mode {mode} never looked up: {string.Join(", ", missing)}", level: LogLevel.Important);

                return missing.Length == 0;
            });

            AddAssert("and nothing from another ruleset was", () =>
            {
                var expected = PlayfieldElementCatalog.ForRuleset(mode).Select(e => e.Element).ToHashSet();
                return lookedUp.All(expected.Contains);
            });
        }

        /// <summary>
        /// Hiding the whole playfield at once must still leave a working chart. Any skin may answer
        /// any component with any drawable, so lazer's consumers are supposed to tolerate the empty
        /// one the filter hands back — but a few of them cast the answer to their own default
        /// implementation's type and abort the game outright when it isn't (osu!'s slider body and
        /// cursor both do; see <c>PlayfieldElementCatalog.Entry.CreateHidden</c>). This is the test
        /// that finds them.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void AFullyHiddenPlayfieldStillPlays(int mode)
        {
            AddStep("hide every element this ruleset draws", () =>
            {
                foreach (var entry in PlayfieldElementCatalog.ForRuleset(mode))
                    visibility.Shown(entry.Element).Value = false;
            });

            createLayer(mode);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            AddUntilStep("playfield populated with everything hidden", () => layer.DrawableRuleset!.Playfield.AllHitObjects.Any());

            playThrough();

            AddStep("report what was intercepted", () => Logger.Log(
                $"[playfield-elements] mode {mode}: {layer.ElementFilter!.SuppressedLookups} lookup(s) suppressed across "
                + $"{string.Join(", ", layer.ElementFilter!.SuppressedElementsSeen.OrderBy(e => e.ToString()))}"));

            AddAssert("lookups really were suppressed", () => layer.ElementFilter!.SuppressedLookups > 0);
            AddAssert("gameplay ran to the end regardless", () => layer.DrawableRuleset!.FrameStableClock.CurrentTime > 4000);

            AddStep("remove layer", () => Remove(host, true));
        }

        /// <summary>
        /// The toggle has to reach the chart already on screen — that is the whole reason hiding
        /// runs through the skin lookup rather than the drawable tree. Nothing is rebuilt here: the
        /// same layer instance must start suppressing lookups after the flip.
        /// </summary>
        [Test]
        public void TogglingAnElementAppliesToTheRunningChartWithoutARebuild()
        {
            createLayer(0);

            AddUntilStep("layer loaded", () => layer.IsLoaded && layer.DrawableRuleset != null);
            playThrough();

            LazerChartLayer built = null!;
            AddStep("remember the layer instance", () => built = layer);

            AddAssert("nothing suppressed while everything is shown", () => layer.ElementFilter!.SuppressedLookups == 0);

            AddStep("hide the cursor", () => visibility.Shown(PlayfieldElement.OsuCursor).Value = false);

            AddUntilStep("the running chart re-asked and got nothing for it",
                () => layer.ElementFilter!.SuppressedElementsSeen.Contains(PlayfieldElement.OsuCursor));

            AddAssert("and it was never rebuilt to do it", () => ReferenceEquals(layer, built) && layer.DrawableRuleset != null);

            AddStep("show it again", () => visibility.Shown(PlayfieldElement.OsuCursor).Value = true);
            AddStep("remove layer", () => Remove(host, true));
        }

        /// <summary>Walks the chart past every object in small enough steps that the frame-stable
        /// clock plays through them normally (a jump over <c>seek_snap_threshold_ms</c> would
        /// hard-seek instead, skipping the objects — and the judgements — in between).</summary>
        private void playThrough()
        {
            for (double t = 500; t <= 5000; t += 500)
            {
                double target = t;

                AddStep($"advance to {target}ms", () => manual.CurrentTime = target);
                AddUntilStep($"gameplay reached {target}ms", () =>
                {
                    var clock = layer.DrawableRuleset?.FrameStableClock;
                    return clock != null && !clock.IsCatchingUp.Value && Math.Abs(clock.CurrentTime - target) < 100;
                });
            }
        }

        private void createLayer(int mode)
        {
            AddStep("create layer", () =>
            {
                manual.CurrentTime = 0;

                string osu = Path.Combine(dir, $"elements [{mode}].osu");
                File.WriteAllText(osu, beatmapForMode(mode));

                Add(host = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manual),
                    Child = layer = new LazerChartLayer(new FlatWorkingBeatmap(osu), osu),
                });
            });
        }

        /// <summary>
        /// A beatmap with one of every object shape the mode has, so the run actually reaches the
        /// components that only exist for sliders/spinners/hold notes: circles, a slider (taiko
        /// drum roll / catch juice stream), a spinner (taiko swell / catch banana shower) and, for
        /// mania, a hold note. Kiai is on throughout so the kiai-only pieces are built too.
        /// </summary>
        private static string beatmapForMode(int mode) =>
            "osu file format v14\n\n" +
            "[General]\nAudioFilename: audio.wav\nMode: " + mode + "\n\n" +
            "[Metadata]\nTitle:test\nArtist:test\nCreator:test\nVersion:test\n\n" +
            "[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n\n" +
            "[TimingPoints]\n0,500,4,1,0,100,1,1\n\n" +
            "[HitObjects]\n" +
            (mode == 3
                ?
                // 4K mania: two notes in different columns, then a hold note.
                "64,192,1000,1,0\n" +
                "192,192,1500,1,0\n" +
                "320,192,2000,128,0,3500:0:0:0:0:\n" +
                "448,192,4000,1,0\n"
                :
                // Circle (centre/don), circle with a whistle+finish (rim/kat), a reversing slider
                // (drum roll / juice stream) and a spinner (swell / banana shower).
                "64,192,1000,1,0\n" +
                "192,192,1500,1,10\n" +
                "128,192,2000,2,0,L|320:192,2,192\n" +
                "256,192,3500,12,0,4500\n");
    }
}
