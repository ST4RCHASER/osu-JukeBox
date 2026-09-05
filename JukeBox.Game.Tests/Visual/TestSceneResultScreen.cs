#nullable enable

using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.UI.Result;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osuTK.Graphics;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// The result screen and one player's panel read straight from hand-made <see cref="PlayerResultData"/>,
    /// so what the RANKING panel draws (score, hit counts, combo, accuracy, grade, mods, name) is checked
    /// against the exact record it was given, and the screen's per-player layout and Next button are
    /// exercised as real behaviour rather than restated.
    /// </summary>
    [TestFixture]
    public partial class TestSceneResultScreen : JukeBoxTestScene
    {
        private Container host = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            Clear();
            Add(host = new Container { RelativeSizeAxes = Axes.Both });
        });

        private static PlayerResultData sampleData() => new PlayerResultData(
            PlayerName: "evil grape",
            TotalScore: 493309,
            Count300: 921,
            Count100: 258,
            Count50: 23,
            CountMiss: 6,
            MaxCombo: 708,
            Accuracy: 0.975,
            Grade: "A",
            Mods: new[] { "HD", "HR" },
            Colour: Color4.HotPink);

        private static ResultBeatmapHeader sampleHeader() => new ResultBeatmapHeader(
            Title: "I love you Orchestra - Red Ocean [Ex]",
            Artist: "I love you Orchestra",
            Mapper: "captin1",
            PlayedByLine: "Played by evil grape on 8/12/2025 4:05:52 AM.");

        [Test]
        public void APanelShowsEveryNumberFromItsData()
        {
            ResultPanel panel = null!;

            AddStep("build a panel from a known play", () =>
                host.Child = panel = new ResultPanel(sampleData()));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            AddAssert("total score is zero-padded", () => panel.ScoreText == "0493309");
            AddAssert("300 count matches", () => panel.Count300Text == "921x");
            AddAssert("100 count matches", () => panel.Count100Text == "258x");
            AddAssert("50 count matches", () => panel.Count50Text == "23x");
            AddAssert("miss count matches", () => panel.CountMissText == "6x");
            AddAssert("max combo matches", () => panel.MaxComboText == "708x");
            AddAssert("accuracy is two-dp percent", () => panel.AccuracyText == "97.50%");
            AddAssert("grade matches", () => panel.GradeText == "A");
            AddAssert("mods are joined acronyms", () => panel.ModsText == "HD HR");
            AddAssert("name matches", () => panel.NameText == "evil grape");
        }

        [Test]
        public void APanelDrawsAKnownGradeAsAGraphicNotALetter()
        {
            ResultPanel panel = null!;

            AddStep("build a panel with an S grade", () =>
                host.Child = panel = new ResultPanel(sampleData() with { Grade = "S" }));
            AddUntilStep("panel loaded", () => panel.IsLoaded);

            // "S" parses to a ScoreRank, so even with no skin resolved the DrawableRank badge draws it —
            // never the bare-letter fallback. A grade that could not be a rank would flip this false.
            AddAssert("the grade is drawn as a graphic", () => panel.GradeIsGraphic);
        }

        [Test]
        public void TheScreenLaysOutOnePanelPerPlayer()
        {
            ResultScreen screen = null!;

            AddStep("show a single-player result", () =>
            {
                host.Child = screen = new ResultScreen();
                screen.Show(sampleHeader(), new[] { sampleData() });
            });
            AddUntilStep("one panel is laid out", () => screen.PanelCount == 1);
            AddAssert("the header title is shown", () =>
                screen.HeaderText == "I love you Orchestra - Red Ocean [Ex]");

            AddStep("re-show with five players", () =>
                screen.Show(sampleHeader(), Enumerable.Range(0, 5)
                    .Select(i => sampleData() with { PlayerName = $"p{i}" })
                    .ToList()));
            AddUntilStep("five panels are laid out", () => screen.PanelCount == 5);
            AddAssert("each panel carries its own player's name", () =>
                screen.Panels.Select(p => p.NameText)
                      .SequenceEqual(new[] { "p0", "p1", "p2", "p3", "p4" }));
        }

        [Test]
        public void TheNextButtonInvokesNextRequested()
        {
            ResultScreen screen = null!;
            int nextCount = 0;
            int restartCount = 0;

            AddStep("show a result and wire the callbacks", () =>
            {
                host.Child = screen = new ResultScreen
                {
                    NextRequested = () => nextCount++,
                    RestartRequested = () => restartCount++,
                };
                screen.Show(sampleHeader(), new[] { sampleData() });
            });
            AddUntilStep("panel laid out", () => screen.PanelCount == 1);

            AddStep("click Next", () => screen.NextButton.TriggerClick());
            AddAssert("NextRequested fired once", () => nextCount == 1);
            AddAssert("RestartRequested did not fire", () => restartCount == 0);

            AddStep("click Restart", () => screen.RestartButton.TriggerClick());
            AddAssert("RestartRequested fired once", () => restartCount == 1);
            AddAssert("NextRequested still at one", () => nextCount == 1);
        }
    }
}
