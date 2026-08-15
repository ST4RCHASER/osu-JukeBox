#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Tests.Beatmaps;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using osu.Game.Graphics;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace JukeBox.Game.Tests.Visual
{
    [TestFixture]
    public partial class TestSceneNowPlayingPanel : JukeBoxManualInputTestScene
    {
        private PlaybackController playback = null!;
        private MusicQueue queue = null!;
        private BeatmapCache cache = null!;
        private Jukebox jukebox = null!;

        private NowPlayingPanel nowPlaying = null!;
        private QueuePanel queuePanel = null!;

        private string tmp = null!;
        private CachedBeatmapSet fixtureSet = null!;
        private CachedBeatmapSet fixtureSetLong = null!;
        private BeatmapSetInfo fixtureInfo = null!;

        // The panel is hosted at the real right-column content width (see MainScreen) rather than
        // the full test window: it lays out for a narrow column, so anything that only breaks when
        // space is tight breaks here too.
        private const float column_content_width = 340 - 2 * Theme.PanelPadding;

        // CreateChildDependencies runs once for the whole scene (shared across every [Test] in
        // this fixture, see TestSceneBeatmapListing) — playback/jukebox/queue are created here once
        // and reused/reset across tests in SetUpSteps rather than recreated, so the cached
        // instances [Resolved] fields pick up stay valid.
        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);

            string audioFile = Path.Combine(tmp, "audio.wav");
            writeSilentWav(audioFile, 1);
            fixtureSet = new CachedBeatmapSet { SetId = 1, Directory = tmp, AudioFile = audioFile };
            fixtureInfo = new BeatmapSetInfo { Id = 1, Title = "Fixture Title", Artist = "Fixture Artist" };

            // A separate, much longer fixture for the drag test: with only ~1s of track, the real
            // wall-clock time spent stepping through AddStep/AddWaitStep could itself carry
            // CurrentTimeMs a meaningful fraction of the way through LengthMs, making the "did the
            // handle stay put mid-drag / did it land near the drag target" assertions flaky.
            string longDir = Path.Combine(tmp, "long");
            Directory.CreateDirectory(longDir);
            string longAudioFile = Path.Combine(longDir, "audio.wav");
            writeSilentWav(longAudioFile, 20);
            fixtureSetLong = new CachedBeatmapSet { SetId = 2, Directory = longDir, AudioFile = longAudioFile };

            playback = new PlaybackController();
            queue = new MusicQueue();
            var emptyMirror = new EmptyMirror();

            // Backed by a real (if fake-content) mirror rather than EmptyMirror, so
            // QueuePanelRowShowsDownloadingThenReadyStatus below can exercise a real
            // download/extract round trip through GetAsync.
            cache = new BeatmapCache(Path.Combine(tmp, "cache"), new FileMirror(makeOsz()));

            jukebox = new Jukebox(queue, new RadioService(emptyMirror), cache, playback);

            var deps = new DependencyContainer(parent);
            deps.CacheAs(playback);
            deps.CacheAs(jukebox);
            deps.CacheAs(queue);
            deps.CacheAs(cache);
            return deps;
        }

        // NOTE: deliberately NOT deleting `tmp` here — see TestScenePlaybackController for why
        // (TestScene runs queued AddStep bodies from a base-class teardown hook that fires after
        // this derived class's own [TearDown], so a synchronous delete here would race the
        // fixture files out from under still-pending steps).

        private Container uiContainer = null!;

        // playback/jukebox are added exactly once, here, for the TestScene's whole lifetime — NOT
        // inside SetUpSteps. SetUpSteps' `uiContainer.Children = ...` reassignment below disposes
        // whatever it previously held on every re-run (once per [Test]); doing that to playback/
        // jukebox themselves would dispose the very instances CreateChildDependencies cached for
        // [Resolved] to hand out, breaking every [Test] after the first with an
        // ObjectDisposedException. uiContainer exists precisely to give SetUpSteps something
        // disposable to rebuild each test without touching its playback/jukebox siblings.
        protected override void LoadComplete()
        {
            base.LoadComplete();
            Add(playback);
            Add(jukebox);
            Add(uiContainer = new Container { RelativeSizeAxes = Axes.Both });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset queue, build UI", () =>
            {
                queue.Items.Clear();

                uiContainer.Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.Y,
                        Width = column_content_width,
                        Child = nowPlaying = new NowPlayingPanel(),
                    },
                    queuePanel = new QueuePanel(),
                };
            });
        }

        // Volume is a Settings → Audio concern (master/effect/music), never a per-song one, so it
        // has no control in here — unlike the transport, which now sits in this panel.
        [Test]
        public void NoVolumeControlsInNowPlayingPanel()
        {
            AddAssert("no volume slider", () => !nowPlaying.ChildrenOfType<BasicSliderBar<double>>().Any());
            AddAssert("no volume icon", () => !nowPlaying.ChildrenOfType<SpriteIcon>()
                .Any(i => i.Icon.Equals(FontAwesome.Solid.VolumeUp)));
        }

        // Reading order in the column: song → transport → progress → difficulty. The transport is a
        // cluster of buttons, so it is centred rather than left-aligned like the full-width rows.
        [Test]
        public void TransportSitsCentredAboveTheProgressBar()
        {
            AddUntilStep("panel has laid out", () => nowPlaying.DrawHeight > 0);

            AddAssert("transport is above the progress bar",
                () => nowPlaying.Transport.ScreenSpaceDrawQuad.BottomLeft.Y
                      <= nowPlaying.ProgressBar.ScreenSpaceDrawQuad.TopLeft.Y);
            AddAssert("progress bar is above the difficulty dropdown",
                () => nowPlaying.ProgressBar.ScreenSpaceDrawQuad.BottomLeft.Y
                      <= nowPlaying.DifficultySwitcher.ScreenSpaceDrawQuad.TopLeft.Y);

            AddAssert("transport is horizontally centred in the panel", () =>
            {
                var transport = nowPlaying.Transport.ScreenSpaceDrawQuad;
                var panel = nowPlaying.ScreenSpaceDrawQuad;
                float transportCentre = (transport.TopLeft.X + transport.TopRight.X) / 2;
                float panelCentre = (panel.TopLeft.X + panel.TopRight.X) / 2;
                return Math.Abs(transportCentre - panelCentre) < 1f;
            });
            AddAssert("transport hugs its buttons rather than filling the column",
                () => nowPlaying.Transport.DrawWidth < nowPlaying.DrawWidth);
        }

        // The dropdown used to keep the fixed 220px width it had in the old wide bar, stopping
        // short of the column's right edge while every row around it ran full width.
        [Test]
        public void DifficultyDropdownSpansTheFullContentWidth()
        {
            AddUntilStep("panel has laid out", () => nowPlaying.DrawHeight > 0);
            AddAssert("dropdown spans the panel's width",
                () => Math.Abs(nowPlaying.DifficultySwitcher.DrawWidth - nowPlaying.DrawWidth) < 1f);
            AddAssert("so does the dropdown control inside it",
                () => Math.Abs(nowPlaying.DifficultySwitcher.Dropdown.DrawWidth - nowPlaying.DrawWidth) < 1f);
        }

        // Regression coverage for the progress bar overflowing its host: the fill's visual track
        // (ProgressSliderBar.VisualBar) spans the panel's own width exactly — never past it, into
        // the padding the owning column card reserves around this content.
        [Test]
        public void ProgressBarVisualTrackStaysWithinPanelBounds()
        {
            AddAssert("track's left edge is inside the panel",
                () => nowPlaying.ProgressBar.VisualBar.ScreenSpaceDrawQuad.TopLeft.X
                      >= nowPlaying.ScreenSpaceDrawQuad.TopLeft.X - 0.5f);

            AddAssert("track's right edge is inside the panel",
                () => nowPlaying.ProgressBar.VisualBar.ScreenSpaceDrawQuad.TopRight.X
                      <= nowPlaying.ScreenSpaceDrawQuad.TopRight.X + 0.5f);
        }

        // The panel stacks VERTICALLY for a narrow column (it used to be a wide bar with everything
        // side by side): it sizes to its content within the column's width, the progress bar spans
        // that full width, and it sits on its own row below the cover/title/button band rather than
        // beside it.
        [Test]
        public void PanelStacksVerticallyWithinTheColumnWidth()
        {
            AddUntilStep("panel has laid out", () => nowPlaying.DrawHeight > 0);
            AddAssert("panel is no wider than its column", () => nowPlaying.DrawWidth <= column_content_width + 0.5f);
            AddAssert("progress bar spans the panel's full width",
                () => nowPlaying.ProgressBar.DrawWidth >= nowPlaying.DrawWidth - 0.5f);
            // The cover row (64px tall) and the transport both sit above it, so the progress bar
            // can only start well down the panel — never on the same line as the cover.
            AddAssert("progress bar starts below the cover row, not beside it",
                () => nowPlaying.ProgressBar.ScreenSpaceDrawQuad.TopLeft.Y
                      - nowPlaying.ScreenSpaceDrawQuad.TopLeft.Y >= 64);
        }

        // The button moved out of the title row and into the transport strip (to the right of
        // skip-next), which is where a per-song action belongs — the URL seam is unchanged.
        [Test]
        public void BrowserButtonLivesInTheTransportRow()
        {
            AddAssert("browser button is inside the transport strip",
                () => nowPlaying.Transport.ChildrenOfType<IconButton>().Contains(nowPlaying.BrowserButton));
            AddAssert("it trails the skip-next button", () =>
            {
                var buttons = nowPlaying.Transport.ChildrenOfType<IconButton>().ToList();
                return buttons.IndexOf(nowPlaying.BrowserButton) == buttons.Count - 1;
            });
            AddAssert("nothing in the title row opens a browser", () =>
                nowPlaying.ChildrenOfType<IconButton>()
                          .Count(b => b.Icon.Equals(FontAwesome.Solid.ExternalLinkAlt)) == 1);
        }

        [Test]
        public void BrowserButtonOpensNowPlayingSetPage()
        {
            string? openedUrl = null;
            AddStep("wire OpenUrl seam", () => nowPlaying.OpenUrl = url => openedUrl = url);
            AddStep("set NowPlaying", () => jukebox.NowPlaying.Value = fixtureInfo);

            AddStep("click browser button", () => nowPlaying.BrowserButton.TriggerClick());
            AddAssert("opened the set's osu.ppy.sh page",
                () => openedUrl == $"https://osu.ppy.sh/beatmapsets/{fixtureInfo.Id}");
        }

        [Test]
        public void BrowserButtonDoesNothingWithNoNowPlaying()
        {
            bool called = false;
            AddStep("wire OpenUrl seam", () => nowPlaying.OpenUrl = _ => called = true);
            AddStep("clear NowPlaying", () => jukebox.NowPlaying.Value = null);

            AddStep("click browser button", () => nowPlaying.BrowserButton.TriggerClick());
            AddAssert("seam was not invoked", () => !called);
        }

        [Test]
        public void TimeLabelsFormatAndUpdateLive()
        {
            AddStep("play long fixture", () => playback.PlayAsync(fixtureSetLong));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == fixtureSetLong.SetId);

            AddAssert("elapsed starts at 0:00", () => nowPlaying.ElapsedText.Text.ToString() == "0:00");
            AddAssert("total label is mm:ss formatted",
                () => Regex.IsMatch(nowPlaying.TotalText.Text.ToString(), @"^\d+:\d{2}$"));

            AddUntilStep("elapsed label advances as playback progresses",
                () => nowPlaying.ElapsedText.Text.ToString() != "0:00");
        }

        // Regression test for the periodic Update() write fighting a live drag: SliderBar<T>'s
        // TransferValueOnCommit only gates user-drag-input reaching `Current`, not the reverse
        // direction — `current.ValueChanged` unconditionally pushes into the drag-preview value
        // (confirmed by decompiling SliderBar<T>'s constructor; no local framework source is
        // available to read directly). Without also checking progressBar.IsDragged before writing
        // `progress.Value` in Update(), that periodic write would snap the handle back to playback
        // position on every frame while a real drag is in progress.
        [Test]
        public void DraggingProgressBarDoesNotSnapBackAndSeeksOnRelease()
        {
            AddStep("play long fixture", () => playback.PlayAsync(fixtureSetLong));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == fixtureSetLong.SetId);
            AddUntilStep("clock advancing", () => playback.CurrentTimeMs > 0);

            AddStep("press down near the left of the progress bar", () =>
            {
                var bounds = nowPlaying.ProgressBar;
                Vector2 leftLocal = new Vector2(bounds.DrawWidth * 0.05f, bounds.DrawHeight / 2);
                InputManager.MoveMouseTo(bounds.ToScreenSpace(leftLocal));
                InputManager.PressButton(MouseButton.Left);
            });

            AddStep("drag to the centre", () =>
            {
                var bounds = nowPlaying.ProgressBar;
                Vector2 centreLocal = new Vector2(bounds.DrawWidth * 0.5f, bounds.DrawHeight / 2);
                InputManager.MoveMouseTo(bounds.ToScreenSpace(centreLocal));
            });

            AddAssert("progress bar reports it is being dragged", () => nowPlaying.ProgressBar.IsDragged);

            double valueDuringDrag = 0;
            AddStep("capture progress value mid-drag", () => valueDuringDrag = nowPlaying.ProgressBar.Current.Value);

            // Several frames pass while the drag is still held. With the bug present, Update()'s
            // periodic write would overwrite Current.Value with playback's advancing position on
            // every one of these frames; with the fix, it's skipped entirely while IsDragged.
            AddWaitStep("hold the drag while frames pass", 10);
            AddAssert("progress value untouched by playback advancing mid-drag",
                () => nowPlaying.ProgressBar.Current.Value == valueDuringDrag);

            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));
            AddAssert("no longer dragging", () => !nowPlaying.ProgressBar.IsDragged);

            AddUntilStep("seeked to roughly the drag target (~50%)",
                () => Math.Abs(playback.CurrentTimeMs / playback.LengthMs - 0.5) < 0.2);
        }

        // Regression coverage for the difficulty dropdown hiding entirely on single-difficulty
        // sets: it should only disappear when there are zero difficulties (an edge case that
        // shouldn't occur in practice, but the switcher must degrade gracefully) — a lone
        // difficulty still shows its name as a non-interactive, always-selected entry.
        //
        // Each of these builds its own CachedBeatmapSet (distinct SetId, sharing fixtureSet's
        // audio file) rather than mutating the shared fixtureSet — PlaybackController.Current is a
        // Bindable, so reassigning the very same object another test already played would be a
        // same-reference no-op and never fire DifficultySwitcher's rebuild.
        [Test]
        public void DifficultySwitcherShowsNonInteractiveEntryForSingleDifficultySet()
        {
            var soloSet = new CachedBeatmapSet { SetId = 101, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "solo.osu" };
            soloSet.Difficulties.Add(new DifficultyInfo { Path = "solo.osu", Version = "Solo", Mode = 0 });

            AddStep("play set with one difficulty", () => playback.PlayAsync(soloSet));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == soloSet.SetId);

            AddAssert("one item shown", () => nowPlaying.DifficultySwitcher.Dropdown.Items.Count() == 1);
            AddAssert("item is selected and shows the version", () => nowPlaying.DifficultySwitcher.Dropdown.Current.Value?.Version == "Solo");
            AddAssert("dropdown is non-interactive", () => nowPlaying.DifficultySwitcher.Dropdown.Current.Disabled);
        }

        [Test]
        public void DifficultySwitcherHidesForZeroDifficultySet()
        {
            var emptySet = new CachedBeatmapSet { SetId = 102, Directory = tmp, AudioFile = fixtureSet.AudioFile };

            AddStep("play set with no difficulties", () => playback.PlayAsync(emptySet));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == emptySet.SetId);

            AddAssert("dropdown hidden", () => nowPlaying.DifficultySwitcher.Dropdown.Alpha == 0);
        }

        [Test]
        public void DifficultySwitcherShowsInteractiveDropdownForMultiDifficultySet()
        {
            var multiSet = new CachedBeatmapSet { SetId = 103, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "easy.osu" };
            multiSet.Difficulties.Add(new DifficultyInfo { Path = "easy.osu", Version = "Easy", Mode = 0 });
            multiSet.Difficulties.Add(new DifficultyInfo { Path = "hard.osu", Version = "Hard", Mode = 0 });

            AddStep("play set with two difficulties", () => playback.PlayAsync(multiSet));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == multiSet.SetId);

            AddAssert("two items shown", () => nowPlaying.DifficultySwitcher.Dropdown.Items.Count() == 2);
            AddAssert("dropdown interactive", () => !nowPlaying.DifficultySwitcher.Dropdown.Current.Disabled);
            AddAssert("easy selected initially (matches PreferredOsuFile)",
                () => nowPlaying.DifficultySwitcher.Dropdown.Current.Value?.Version == "Easy");
        }

        // Regression coverage for actually switching diffs mid-song via the dropdown (not just
        // that it's interactive) — same selection semantics the old clickable chips provided.
        [Test]
        public void SelectingDropdownItemSwitchesDifficulty()
        {
            var multiSet = new CachedBeatmapSet { SetId = 104, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "easy.osu" };
            multiSet.OsuFiles.AddRange(new[] { "easy.osu", "hard.osu" }); // SwitchDifficultyAsync requires the path to be a known OsuFile
            multiSet.Difficulties.Add(new DifficultyInfo { Path = "easy.osu", Version = "Easy", Mode = 0 });
            multiSet.Difficulties.Add(new DifficultyInfo { Path = "hard.osu", Version = "Hard", Mode = 0 });

            AddStep("play set with two difficulties", () => playback.PlayAsync(multiSet));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == multiSet.SetId);

            AddStep("select Hard via the dropdown", () => nowPlaying.DifficultySwitcher.Dropdown.Current.Value =
                multiSet.Difficulties.Single(d => d.Version == "Hard"));

            AddUntilStep("playback switched to hard.osu", () => playback.SelectedOsuFile.Value == "hard.osu");
        }

        // The mode used to be a text tag baked into the item's label ("[taiko] Oni"). It's now drawn
        // as lazer's real ruleset icon in the item's badge strip instead, so the label carries only
        // the difficulty name — no bracketed prefix left behind.
        [Test]
        public void DifficultyDropdownItemLabelIsJustTheVersion()
        {
            AddStep("play mixed-mode set", () => playback.PlayAsync(mixedModeSet()));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == 105);

            AddAssert("taiko item labelled with only its version", () => dropdown()
                .GenerateItemTextForTest(playback.Current.Value!.Difficulties[1]).ToString() == "Oni");
            AddAssert("no bracketed mode tag anywhere in the labels", () => playback.Current.Value!.Difficulties
                .All(d => !dropdown().GenerateItemTextForTest(d).ToString().Contains('[')));
        }

        // Each item instead opens with lazer's REAL ruleset icon (a SpriteIcon over the
        // texture-backed OsuIcon glyphs — see RulesetIconTest) for that difficulty's own mode.
        [Test]
        public void DifficultyDropdownItemsCarryTheRulesetIcon()
        {
            AddStep("play mixed-mode set", () => playback.PlayAsync(mixedModeSet()));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == 105);

            AddAssert("std item carries osu!'s icon", () => iconOf(playback.Current.Value!.Difficulties[0]).Equals(OsuIcon.RulesetOsu));
            AddAssert("taiko item carries taiko's icon", () => iconOf(playback.Current.Value!.Difficulties[1]).Equals(OsuIcon.RulesetTaiko));
        }

        // …followed by a star-rating pill filled with that rating's difficulty-spectrum colour, the
        // same chip the fullscreen listing's expanded rows draw. Ratings aren't in a .osu file, so
        // they come from the online metadata for the set being played, matched on mode + version.
        [Test]
        public void DifficultyDropdownItemsCarryAStarPillColouredByRating()
        {
            var set = mixedModeSet();

            AddStep("publish online ratings", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 105,
                Title = "Mixed",
                Artist = "Someone",
                Beatmaps =
                {
                    new BeatmapInfo { Mode = "osu", Version = "Normal", DifficultyRating = 2.31 },
                    new BeatmapInfo { Mode = "taiko", Version = "Oni", DifficultyRating = 5.77 },
                },
            });
            AddStep("play mixed-mode set", () => playback.PlayAsync(set));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == 105);

            AddAssert("std pill prints its rating", () => pillTextOf(set.Difficulties[0]) == "2.31");
            AddAssert("taiko pill prints its rating", () => pillTextOf(set.Difficulties[1]) == "5.77");

            AddAssert("std pill is tinted for 2.31 stars",
                () => pillColourOf(set.Difficulties[0]) == Theme.DifficultyColour(2.31));
            AddAssert("taiko pill is tinted for 5.77 stars",
                () => pillColourOf(set.Difficulties[1]) == Theme.DifficultyColour(5.77));
            AddAssert("the two ratings are far enough apart to land in different colour buckets",
                () => Theme.DifficultyColour(2.31) != Theme.DifficultyColour(5.77));
        }

        // A set playing without online metadata (a local folder, or a mirror response with no
        // beatmap list) has no ratings to show — the icon must still be drawn rather than the whole
        // strip going missing with it.
        [Test]
        public void DifficultyDropdownItemsDropOnlyThePillWhenNoRatingIsKnown()
        {
            var set = mixedModeSet();

            AddStep("clear online metadata", () => jukebox.NowPlaying.Value = null);
            AddStep("play mixed-mode set", () => playback.PlayAsync(set));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == 105);

            AddAssert("icon still drawn", () => iconOf(set.Difficulties[0]).Equals(OsuIcon.RulesetOsu));
            AddAssert("no star pill", () => badgesFor(set.Difficulties[0]).ChildrenOfType<CircularContainer>().Any() == false);
        }

        // Collapsing the menu must not change how the current difficulty reads: the closed
        // dropdown's header carries the same badge strip as the row it stands for.
        [Test]
        public void ClosedDropdownHeaderCarriesTheSameBadges()
        {
            var set = mixedModeSet();

            AddStep("publish online ratings", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 105,
                Beatmaps = { new BeatmapInfo { Mode = "osu", Version = "Normal", DifficultyRating = 2.31 } },
            });
            AddStep("play mixed-mode set", () => playback.PlayAsync(set));
            AddUntilStep("track active", () => playback.Current.Value?.SetId == 105);
            AddUntilStep("header has badges", () => headerBadges().Any());

            AddAssert("header shows the selected difficulty's ruleset icon", () => headerBadges()
                .SelectMany(b => b.ChildrenOfType<SpriteIcon>())
                .Any(i => i.Icon.Equals(OsuIcon.RulesetOsu)));
            AddAssert("header shows the selected difficulty's rating", () => headerBadges()
                .SelectMany(b => b.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>())
                .Any(t => t.Text.ToString() == "2.31"));
        }

        private CachedBeatmapSet mixedModeSet()
        {
            var set = new CachedBeatmapSet { SetId = 105, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "std.osu" };
            set.Difficulties.Add(new DifficultyInfo { Path = "std.osu", Version = "Normal", Mode = 0 });
            set.Difficulties.Add(new DifficultyInfo { Path = "taiko.osu", Version = "Oni", Mode = 1 });
            return set;
        }

        private DifficultySwitcher.DifficultyDropdown dropdown()
            => nowPlaying.ChildrenOfType<DifficultySwitcher.DifficultyDropdown>().Single();

        // The badge strip the dropdown would draw for an item, without opening the menu — a
        // DropdownMenu only builds its row drawables once it has been popped open.
        private Drawable badgesFor(DifficultyInfo difficulty) => dropdown().BadgesForTest(difficulty)!;

        private IconUsage iconOf(DifficultyInfo difficulty)
            => badgesFor(difficulty).ChildrenOfType<SpriteIcon>().First().Icon;

        private Drawable starPillOf(DifficultyInfo difficulty)
            => badgesFor(difficulty).ChildrenOfType<CircularContainer>().Single();

        private string pillTextOf(DifficultyInfo difficulty)
            => starPillOf(difficulty).ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>().Single().Text.ToString();

        private Color4 pillColourOf(DifficultyInfo difficulty)
            => starPillOf(difficulty).ChildrenOfType<Box>().Single().Colour;

        private IEnumerable<DifficultySwitcher.DifficultyBadges> headerBadges()
            => nowPlaying.DifficultySwitcher.ChildrenOfType<DifficultySwitcher.DifficultyBadges>();

        // The title used to sit over a full-width accent rule, which read as a divider cutting the
        // song's own metadata in half. The block is now three plain lines: title, artist, and the
        // mapper credit that took the rule's place.
        [Test]
        public void SongBlockShowsTitleArtistAndMapperWithNoAccentRule()
        {
            AddStep("set NowPlaying", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 7,
                Title = "Future Candy",
                Artist = "YUC'e",
                Creator = "Sotarks",
            });

            AddUntilStep("mapper credited", () => nowPlaying.MapperText.Text.ToString() == "mapped by Sotarks");
            AddAssert("title shown", () => nowPlaying.TitleText.Text.ToString() == "Future Candy");
            AddAssert("artist shown", () => nowPlaying.ArtistText.Text.ToString() == "YUC'e");

            AddAssert("mapper reads below the artist",
                () => nowPlaying.MapperText.ScreenSpaceDrawQuad.TopLeft.Y
                      >= nowPlaying.ArtistText.ScreenSpaceDrawQuad.TopLeft.Y);

            // A Box is the only way the rule could be drawn, and the song block holds nothing else
            // that would legitimately need one.
            AddAssert("no accent rule under the title",
                () => !nowPlaying.SongInfo.ChildrenOfType<Box>().Any());
        }

        [Test]
        public void SongBlockLeavesTheMapperLineBlankWhenNoCreatorIsKnown()
        {
            AddStep("set NowPlaying with no creator", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 8,
                Title = "Untitled",
                Artist = "Unknown",
            });

            AddUntilStep("title shown", () => nowPlaying.TitleText.Text.ToString() == "Untitled");
            AddAssert("no credit to nobody", () => nowPlaying.MapperText.Text.ToString() == string.Empty);
        }

        [Test]
        public void PanelShowsNowPlayingTitleAndArtist()
        {
            AddStep("set NowPlaying", () => jukebox.NowPlaying.Value = fixtureInfo);
            AddUntilStep("title shown", () => nowPlaying.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                .Any(t => t.Text.ToString() == fixtureInfo.DisplayTitle));
        }

        [Test]
        public void PanelShowsAndClearsStatusText()
        {
            AddStep("set Status", () => jukebox.Status.Value = "Downloading Something…");
            AddUntilStep("status shown", () => nowPlaying.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                .Any(t => t.Text.ToString() == "Downloading Something…"));

            AddStep("clear Status", () => jukebox.Status.Value = null);
            AddUntilStep("status cleared", () => nowPlaying.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                .All(t => t.Text.ToString() != "Downloading Something…"));
        }

        [Test]
        public void QueuePanelShowsRowsAfterEnqueue()
        {
            AddAssert("starts empty", () => queuePanel.RowCount == 0);
            AddAssert("header shows 0", () => queuePanel.HeaderText == "Queue (0)");

            AddStep("enqueue two sets", () =>
            {
                queue.Enqueue(new BeatmapSetInfo { Id = 1, Title = "One", Artist = "Artist One" });
                queue.Enqueue(new BeatmapSetInfo { Id = 2, Title = "Two", Artist = "Artist Two" });
            });

            AddAssert("2 rows shown", () => queuePanel.RowCount == 2);
            AddAssert("header shows 2", () => queuePanel.HeaderText == "Queue (2)");

            AddStep("remove first row via its ✕ button", () => queuePanel.TriggerRemoveAt(0));
            AddAssert("removed set is gone from the queue itself", () => queue.Items.All(i => i.Id != 1));

            // The removed row fades + collapses out (QueueRow.AnimateOut) rather than vanishing
            // instantly, so RowCount only drops once that animation finishes and the row expires.
            AddUntilStep("1 row left", () => queuePanel.RowCount == 1);
        }

        // Regression test for a production crash: MusicQueue.Items is a BindableList that
        // QueuePanel binds CollectionChanged against to rebuild rowsFlow's InternalChildren.
        // Jukebox's advanceRoundAsync pops the queue from a ConfigureAwait(false) continuation
        // (i.e. off the update thread) on its second-and-later candidate within a round, which
        // used to run that rebuild inline on the popping thread — mutating a Loaded Drawable's
        // InternalChildren off the update thread, which the framework throws
        // Drawable.InvalidThreadForMutationException for (and, in production, left the panel's
        // internal Drawable bookkeeping corrupted enough to crash later with an unrelated
        // KeyNotFoundException). This reproduces that class of bug directly at the queue/panel
        // boundary — mutating Items from a background thread while the panel is loaded — without
        // needing to drive Jukebox's own retry/coalescing paths to land a PopNext call off-thread.
        [Test]
        public void QueuePanelSurvivesQueueMutationFromBackgroundThread()
        {
            AddStep("enqueue two sets", () =>
            {
                queue.Enqueue(new BeatmapSetInfo { Id = 1, Title = "One", Artist = "Artist One" });
                queue.Enqueue(new BeatmapSetInfo { Id = 2, Title = "Two", Artist = "Artist Two" });
            });
            AddAssert("2 rows shown", () => queuePanel.RowCount == 2);

            Exception? caught = null;

            AddStep("pop the queue from a background thread while the panel is loaded", () =>
            {
                caught = null;
                try
                {
                    Task.Run(() => queue.PopNext()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });

            AddAssert("no exception escaped the off-thread mutation", () => caught == null);
            AddUntilStep("panel reflects the removal", () => queuePanel.RowCount == 1);
            AddAssert("header reflects the removal", () => queuePanel.HeaderText == "Queue (1)");
        }

        // The row now speaks the compact card's language — cover, title, artist, mapper — but
        // deliberately drops that card's status pill and difficulty dots: those help you CHOOSE a
        // set in the search results, and say nothing once you have already queued it.
        [Test]
        public void QueueRowShowsCardMetadataWithoutStatusPillOrDifficultyDots()
        {
            AddStep("enqueue a ranked set with difficulties", () => queue.Enqueue(new BeatmapSetInfo
            {
                Id = 997,
                Title = "Row Title",
                Artist = "Row Artist",
                Creator = "Row Mapper",
                Status = "ranked",
                Beatmaps = { new BeatmapInfo { DifficultyRating = 5.2 }, new BeatmapInfo { DifficultyRating = 2.1 } },
            }));

            AddUntilStep("row is laid out", () => queuePanel.RowCount == 1);

            AddAssert("title shown", () => rowTexts().Contains("Row Title"));
            AddAssert("artist shown", () => rowTexts().Contains("Row Artist"));
            AddAssert("mapper shown", () => rowTexts().Contains("mapped by Row Mapper"));
            AddAssert("cover thumbnail present", () => queuePanel.ChildrenOfType<CoverThumbnail>().Any());

            AddAssert("no status pill", () => !rowTexts().Contains("ranked"));
            AddAssert("no difficulty dots", () => !queuePanel.ChildrenOfType<Circle>().Any());
        }

        // A set queued by dropping someone's .osr credits the player. The panel has room for both
        // credits, so the mapper line stays and the player is added beneath it.
        [Test]
        public void SongBlockCreditsTheReplayPlayerUnderTheMapper()
        {
            AddStep("set NowPlaying with a replay attached", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 8,
                Title = "Blue Zenith",
                Artist = "xi",
                Creator = "Asphyxia",
                Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "Cookiezi" },
            });

            AddUntilStep("player credited", () => nowPlaying.ReplayText.Text.ToString() == "Played by Cookiezi");
            AddAssert("mapper credit kept", () => nowPlaying.MapperText.Text.ToString() == "mapped by Asphyxia");
            AddAssert("player reads below the mapper",
                () => nowPlaying.ReplayText.ScreenSpaceDrawQuad.TopLeft.Y
                      >= nowPlaying.MapperText.ScreenSpaceDrawQuad.TopLeft.Y);

            AddStep("set NowPlaying with no replay", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 9,
                Title = "Plain",
                Creator = "Someone",
            });

            AddUntilStep("credit clears", () => nowPlaying.ReplayText.Text.ToString().Length == 0);
        }

        // The queue row is a fixed-height three-line card, so the replay credit takes the mapper
        // line's place there rather than adding a fourth line (see QueuePanel.QueueRow).
        [Test]
        public void QueueRowCreditsTheReplayPlayerInPlaceOfTheMapper()
        {
            AddStep("enqueue a set with a replay attached", () => queue.Enqueue(new BeatmapSetInfo
            {
                Id = 998,
                Title = "Replayed Row",
                Artist = "Row Artist",
                Creator = "Row Mapper",
                Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "Rafis" },
            }));

            AddUntilStep("row is laid out", () => queuePanel.RowCount == 1);

            AddAssert("player credited", () => rowTexts().Contains("Played by Rafis"));
            AddAssert("mapper line given over to it", () => !rowTexts().Contains("mapped by Row Mapper"));
            AddAssert("title still shown", () => rowTexts().Contains("Replayed Row"));
        }

        private List<string> rowTexts() => queuePanel.ChildrenOfType<SpriteText>().Select(t => t.Text.ToString()).ToList();

        // Builds a minimal but real .osz (a zip containing a *.osu file) so BeatmapCache.GetAsync
        // has something genuine to download/extract/scan — mirrors BeatmapCacheTest.makeOsz.
        private string makeOsz()
        {
            string dir = Path.Combine(tmp, "osz-build");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.osu"),
                "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Events]\n");
            File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), new byte[] { 0xFF });
            string osz = Path.Combine(tmp, "fixture.osz");
            ZipFile.CreateFromDirectory(dir, osz);
            return osz;
        }

        // BASS (the audio backend behind osu!framework's Track) plays WAV directly, so a
        // hand-written 44-byte RIFF header followed by silence is enough to drive playback.
        private static void writeSilentWav(string path, double seconds)
        {
            const int sample_rate = 44100;
            const short channels = 1;
            const short bits_per_sample = 16;

            int dataSize = (int)(sample_rate * channels * (bits_per_sample / 8) * seconds);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sample_rate);
            writer.Write(sample_rate * channels * (bits_per_sample / 8));
            writer.Write((short)(channels * (bits_per_sample / 8)));
            writer.Write(bits_per_sample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }

        // Never exercised (Jukebox's advance loop isn't driven in this test — NowPlaying is set
        // directly, and playback is driven directly via PlaybackController.PlayAsync), only
        // present to satisfy Jukebox/RadioService/BeatmapCache's constructors.
        private class EmptyMirror : IBeatmapMirror
        {
            public string Name => "empty";

            public Task<List<BeatmapSetInfo>> SearchAsync(SearchRequest request, CancellationToken ct = default)
                => Task.FromResult(new List<BeatmapSetInfo>());

            public Task DownloadAsync(int setId, bool noVideo, Stream destination, CancellationToken ct = default, DownloadProgressCallback? progress = null)
                => throw new NotSupportedException("not exercised by this test scene");
        }
    }
}
