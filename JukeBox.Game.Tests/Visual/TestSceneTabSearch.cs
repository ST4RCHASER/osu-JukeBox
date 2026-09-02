#nullable enable

using System.Linq;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Live filtering in the Settings and Chart tabs. Both tabs share one search host, so these
    /// exercise the behaviour once against each panel rather than testing the host in isolation —
    /// what matters is that a real row in a real panel disappears when it stops matching.
    /// </summary>
    [TestFixture]
    public partial class TestSceneTabSearch : JukeBoxManualInputTestScene
    {
        private SettingsOverlay settings = null!;
        private ChartPanel chart = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create both tab bodies", () => Children = new Drawable[]
            {
                settings = new SettingsOverlay(docked: true) { RelativeSizeAxes = Axes.Both },
                chart = new ChartPanel { RelativeSizeAxes = Axes.Both, Alpha = 0 },
            });

            AddStep("show settings", () => settings.Show());
        }

        /// <summary>
        /// Visible as a PERSON would judge it: a row inside a section that filtered away is not on
        /// screen, however present the row itself claims to be.
        /// </summary>
        private static bool onScreen(Drawable drawable)
        {
            for (Drawable? d = drawable; d != null; d = d.Parent)
            {
                if (!d.IsPresent)
                    return false;
            }

            return true;
        }

        /// <summary>Whether a row carrying exactly this label is currently on screen.</summary>
        private static bool rowShown(Drawable panel, string label)
            => panel.ChildrenOfType<Drawable>()
                    .Where(d => d is IHasFilterTerms terms && terms.FilterTerms.Any(t => t.ToString() == label))
                    .Any(onScreen);

        private static bool sectionShown(Drawable panel, string header) => rowShown(panel, header);

        /// <summary>
        /// Types a term the way a person does — one character at a time through the same entry
        /// point MainScreen routes keystrokes to, rather than assigning the box's value.
        /// </summary>
        private static void type(ITabSearch tab, string term)
        {
            foreach (char c in term)
                tab.BeginSearch(c);
        }

        [Test]
        public void TypingKeepsMatchingRowsAndCollapsesTheRest()
        {
            AddAssert("everything starts shown", () => rowShown(settings, "Background dim") && rowShown(settings, "Master"));

            AddStep("search 'dim'", () => type(settings, "dim"));

            AddUntilStep("the matching row stays", () => rowShown(settings, "Background dim"));
            AddAssert("a non-matching row is gone", () => !rowShown(settings, "Master"));
        }

        // A section whose every row filtered out must take its header with it — a lone heading over
        // nothing reads as a rendering bug.
        [Test]
        public void ASectionHeaderLeavesWithItsLastRow()
        {
            AddAssert("the Audio section starts shown", () => sectionShown(settings, "Audio"));

            AddStep("search 'dim'", () => type(settings, "dim"));

            AddUntilStep("Gameplay stays, holding the match", () => sectionShown(settings, "Gameplay"));
            AddAssert("Audio is gone with its rows", () => !sectionShown(settings, "Audio"));
        }

        // Searching a section's own name is how you browse rather than hunt.
        [Test]
        public void AHeaderMatchKeepsItsWholeSection()
        {
            AddStep("search 'audio'", () => type(settings, "audio"));

            AddUntilStep("the section stays", () => sectionShown(settings, "Audio"));
            AddAssert("and its rows come with it", () => rowShown(settings, "Master"));
        }

        [Test]
        public void ClearingTheTermBringsEverythingBack()
        {
            AddStep("search 'dim'", () => type(settings, "dim"));
            AddUntilStep("filtered", () => !rowShown(settings, "Master"));

            AddStep("clear", () => settings.ClearSearch());

            AddUntilStep("everything is back", () => rowShown(settings, "Master") && rowShown(settings, "Background dim"));
        }

        // The version stamp is not a setting, so it is not searchable — and must therefore never be
        // filtered away, or the panel looks broken mid-search.
        [Test]
        public void TheVersionStampSurvivesAnyFilter()
        {
            AddStep("search something that matches nothing", () => type(settings, "zzzzzz"));

            AddUntilStep("no rows left", () => !rowShown(settings, "Master"));
            AddAssert("but the version is still there", () => onScreen(settings.VersionDrawable));
        }

        // Rows hidden because their backend cannot express them must not answer a search either —
        // a filter walks the drawable tree without consulting Alpha, so this needs CanBeShown.
        [Test]
        public void RowsHiddenBehindMirrorModeDoNotAnswerASearch()
        {
            AddAssert("credentials start hidden", () => !rowShown(settings, "Client ID"));

            AddStep("search 'client'", () => type(settings, "client"));

            AddUntilStep("still hidden", () => !rowShown(settings, "Client ID"));
            AddAssert("and the Online section did not reopen around them", () => !sectionShown(settings, "Online"));
        }

        // ---- Chart tab -------------------------------------------------------------------------

        [Test]
        public void TheChartTabFiltersItsOwnRows()
        {
            AddStep("show chart", () =>
            {
                settings.Hide();
                chart.Alpha = 1;
            });

            AddAssert("rows start shown", () => rowShown(chart, "Render chart") && rowShown(chart, "Play hit sounds"));

            AddStep("search 'hit'", () => type(chart, "hit"));

            AddUntilStep("the matching row stays", () => rowShown(chart, "Play hit sounds"));
            AddAssert("a non-matching row is gone", () => !rowShown(chart, "Render chart"));
        }

        // A dependent row is indented inside a plain wrapper, which the filter walks straight
        // through — so it matches on its OWN label rather than only with its parent.
        [Test]
        public void ADependentRowMatchesOnItsOwnLabel()
        {
            AddStep("show chart", () =>
            {
                settings.Hide();
                chart.Alpha = 1;
            });

            AddStep("search 'opacity'", () => type(chart, "opacity"));

            AddUntilStep("the indented row is found", () => rowShown(chart, "Chart opacity"));
            AddAssert("its unrelated sibling is not", () => !rowShown(chart, "Play hit sounds"));
        }

        [Test]
        public void StoryboardLayerRowsAreFoundByTheirOwnNames()
        {
            AddStep("search 'foreground'", () => type(settings, "foreground"));

            AddUntilStep("the layer row is found", () => rowShown(settings, "Foreground"));
            AddAssert("and unrelated rows are gone", () => !rowShown(settings, "Master"));
        }
    }
}
