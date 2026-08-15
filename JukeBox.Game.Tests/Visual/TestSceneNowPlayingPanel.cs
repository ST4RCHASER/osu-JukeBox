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

        // Regression coverage for the switcher silently undoing someone else's difficulty choice.
        // The switcher repopulates its item list a frame AFTER the set changes; replacing the items
        // moves the control's own Current, which used to reach its selection-committed handler and
        // fire a real SwitchDifficultyAsync back to the set's default — clobbering a difficulty
        // selected in between. That gap is exactly where Jukebox selects the difficulty a dropped
        // replay was recorded on, which was reverted to the default every time.
        [Test]
        public void ADifficultyChosenWhileTheSwitcherRepopulatesIsNotRevertedToTheDefault()
        {
            // Built inside the step rather than in the method body: step bodies are enqueued before
            // the scene has loaded, so the fixture fields CreateChildDependencies sets aren't
            // populated yet at this point (this test sorts first in the fixture, so it can't rely
            // on an earlier one having forced the load).
            AddStep("swap set and pick a non-default difficulty in the same frame", () =>
            {
                var multiSet = new CachedBeatmapSet { SetId = 106, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "easy.osu" };
                multiSet.OsuFiles.AddRange(new[] { "easy.osu", "hard.osu" });
                multiSet.Difficulties.Add(new DifficultyInfo { Path = "easy.osu", Version = "Easy", Mode = 0 });
                multiSet.Difficulties.Add(new DifficultyInfo { Path = "hard.osu", Version = "Hard", Mode = 0 });

                // Both writes in ONE frame, the way PlaybackController.PlayAsync's swap and the
                // difficulty selection that follows it land: the switcher's repopulation is
                // deferred a frame, so it runs after both — and used to overwrite the second.
                playback.Current.Value = multiSet;
                playback.SelectedOsuFile.Value = "hard.osu";
            });

            AddWaitStep("let the switcher repopulate and settle", 10);

            AddAssert("still on Hard", () => playback.SelectedOsuFile.Value == "hard.osu");
            AddAssert("the set itself is unchanged", () => playback.Current.Value?.SetId == 106);
            AddAssert("and the dropdown agrees", () => nowPlaying.DifficultySwitcher.Dropdown.Current.Value?.Version == "Hard");
        }

        // A replay is tied to one exact .osu, so switching difficulty would silently drop it back to
        // autoplay on a difficulty the player never played. The dropdown is locked and dimmed while
        // one is being watched — still showing which difficulty it was — and live again afterwards.
        [Test]
        public void TheDifficultySwitcherIsLockedWhileAReplayIsBeingWatched()
        {
            AddStep("play a multi-difficulty set with a replay attached", () =>
            {
                var replaySet = new CachedBeatmapSet { SetId = 107, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "easy.osu" };
                replaySet.OsuFiles.AddRange(new[] { "easy.osu", "hard.osu" });
                replaySet.Difficulties.Add(new DifficultyInfo { Path = "easy.osu", Version = "Easy", Mode = 0 });
                replaySet.Difficulties.Add(new DifficultyInfo { Path = "hard.osu", Version = "Hard", Mode = 0 });

                playback.Current.Value = replaySet;
                playback.SelectedOsuFile.Value = "hard.osu";

                jukebox.NowPlaying.Value = new BeatmapSetInfo
                {
                    Id = 107,
                    Title = "Replayed",
                    Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "Cookiezi", OsuFile = "hard.osu" },
                };
            });

            AddUntilStep("dropdown is locked", () => nowPlaying.DifficultySwitcher.Dropdown.Current.Disabled);
            AddUntilStep("and dimmed", () => nowPlaying.DifficultySwitcher.Dropdown.Alpha < 1);
            AddAssert("still showing the replay's own difficulty",
                () => nowPlaying.DifficultySwitcher.Dropdown.Current.Value?.Version == "Hard");

            AddStep("move on to an ordinary multi-difficulty set", () =>
            {
                var plainSet = new CachedBeatmapSet { SetId = 108, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "easy.osu" };
                plainSet.OsuFiles.AddRange(new[] { "easy.osu", "hard.osu" });
                plainSet.Difficulties.Add(new DifficultyInfo { Path = "easy.osu", Version = "Easy", Mode = 0 });
                plainSet.Difficulties.Add(new DifficultyInfo { Path = "hard.osu", Version = "Hard", Mode = 0 });

                playback.Current.Value = plainSet;
                playback.SelectedOsuFile.Value = "easy.osu";
                jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 108, Title = "Plain" };
            });

            AddUntilStep("dropdown is interactive again", () => !nowPlaying.DifficultySwitcher.Dropdown.Current.Disabled);
            AddUntilStep("and back to full opacity", () => nowPlaying.DifficultySwitcher.Dropdown.Alpha == 1);
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
                () => pillColourOf(set.Difficulties[0]) == starColour(2.31));
            AddAssert("taiko pill is tinted for 5.77 stars",
                () => pillColourOf(set.Difficulties[1]) == starColour(5.77));
            AddAssert("the two ratings are far enough apart to look different",
                () => starColour(2.31) != starColour(5.77));
        }

        /// <summary>lazer's own colour for a rating — the reference these assertions compare
        /// against, so a change to our side that stopped matching the game would fail here.</summary>
        private static Color4 starColour(double stars) => new OsuColour().ForStarDifficulty(stars);

        // The app used to band ratings into six fixed colours, which merged everything from 5.0 to
        // 6.4 into one red (the user's screenshot had 5.10, 5.23, 5.72 and 6.22 all identical) and
        // never reached the purple end the game shows past 6.5. Colours now come from lazer's
        // continuous spectrum, so neighbouring ratings differ and high ones actually go purple.
        [Test]
        public void StarPillsUseLazersContinuousSpectrumRatherThanBands()
        {
            var set = new CachedBeatmapSet { SetId = 130, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "a.osu" };
            double[] ratings = { 5.10, 5.23, 5.72, 6.22, 6.62 };

            AddStep("play a set of closely-graded difficulties", () =>
            {
                var online = new BeatmapSetInfo { Id = 130, Title = "Close together" };

                for (int i = 0; i < ratings.Length; i++)
                {
                    set.OsuFiles.Add($"{i}.osu");
                    set.Difficulties.Add(new DifficultyInfo { Path = $"{i}.osu", Version = $"D{i}", Mode = 0 });
                    online.Beatmaps.Add(new BeatmapInfo { Mode = "osu", Version = $"D{i}", DifficultyRating = ratings[i] });
                }

                set.PreferredOsuFile = "0.osu";
                playback.Current.Value = set;
                playback.SelectedOsuFile.Value = "0.osu";
                jukebox.NowPlaying.Value = online;
            });

            AddUntilStep("all five rated", () => menuRows()
                .Count(r => difficultyOf(r) != null && r.ChildrenOfType<CircularContainer>().Any()) == ratings.Length);

            AddAssert("every pill matches lazer's colour for its own rating", () =>
                set.Difficulties.All(d =>
                {
                    double rating = ratings[int.Parse(d.Version.Substring(1))];
                    return pillColourOf(d) == starColour(rating);
                }));

            AddAssert("and no two of these ratings share a colour",
                () => set.Difficulties.Select(pillColourOf).Distinct().Count() == ratings.Length);

            // Past lazer's 6.5 cutoff the fill is dark enough that black text is unreadable, so
            // lazer switches the text to a light orange. The pill has to follow, or the number
            // vanishes into its own background exactly where ratings matter most.
            AddAssert("the 6.62 pill's text follows lazer's high-rating rule", () =>
            {
                var hardest = set.Difficulties.Single(d => d.Version == "D4");
                var expected = new OsuColour().ForStarDifficultyText(6.62);
                return starPillOf(hardest).ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>().Single().Colour == expected;
            });
        }

        // The list used to stop at ten, which hid two thirds of a big marathon set — and made the
        // "start on the hardest" default a lie, since the real hardest was usually one of the ones
        // dropped. Every difficulty is listed now; the MENU's height is what's bounded.
        [Test]
        public void EveryDifficultyIsListedAndTheMenuScrolls()
        {
            const int count = 30;
            var set = new CachedBeatmapSet { SetId = 131, Directory = tmp, AudioFile = fixtureSet.AudioFile };

            AddStep("play a 30-difficulty set", () =>
            {
                var online = new BeatmapSetInfo { Id = 131, Title = "Marathon" };

                for (int i = 0; i < count; i++)
                {
                    set.OsuFiles.Add($"{i}.osu");
                    set.Difficulties.Add(new DifficultyInfo { Path = $"{i}.osu", Version = $"D{i:00}", Mode = 0 });
                    // Ascending, so the hardest is the LAST one — which the old cap would have cut.
                    online.Beatmaps.Add(new BeatmapInfo { Mode = "osu", Version = $"D{i:00}", DifficultyRating = 1 + i * 0.25 });
                }

                set.PreferredOsuFile = "0.osu";
                playback.Current.Value = set;
                playback.SelectedOsuFile.Value = "0.osu";
                jukebox.NowPlaying.Value = online;
            });

            AddUntilStep("all 30 listed", () => dropdown().Items.Count() == count);
            AddAssert("and 30 rows really exist", () => menuRows().Count(r => difficultyOf(r) != null) == count);

            AddAssert("the menu is height-bounded rather than item-bounded", () => boundedMenu() != null);

            AddStep("open the menu", () =>
            {
                InputManager.MoveMouseTo(dropdown().ChildrenOfType<DropdownHeader>().Single());
                InputManager.Click(MouseButton.Left);
            });

            // The rows add up to more than the menu is allowed to be, which is exactly the condition
            // that makes it scroll rather than grow — assert the overflow, not just the cap.
            AddUntilStep("the open menu is capped and scrollable", () =>
            {
                var menu = boundedMenu();
                var scroll = menu?.ChildrenOfType<ScrollContainer<Drawable>>().FirstOrDefault();

                return menu != null
                       && scroll != null
                       && menu.DrawHeight <= menu.MaxHeight + 1
                       && scroll.ScrollableExtent > 0;
            });

            AddAssert("the hardest difficulty is the last one, and it's the one playing",
                () => listedVersions().LastOrDefault() == $"D{count - 1:00}"
                      && playback.SelectedOsuFile.Value == $"{count - 1}.osu");
        }

        /// <summary>The dropdown's menu, if it declares a height bound at all — null rather than
        /// throwing, so a regression reads as a failed assertion instead of an exception.</summary>
        private Menu? boundedMenu() => dropdown().ChildrenOfType<Menu>()
            .FirstOrDefault(m => m.MaxHeight > 0 && !float.IsPositiveInfinity(m.MaxHeight));

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

        /// <summary>
        /// The dropdown's REAL menu rows, in list order. Deliberately not the badge factory that
        /// builds a strip on demand: these assertions are about what the open list actually draws,
        /// and a factory-based helper passed happily for a whole round while every real row rendered
        /// a bare name (the framework builds a row's content inside a base constructor, before the
        /// badges reach the subclass — see DifficultyMenuItem). Rows exist as soon as Items is set,
        /// so the menu does not have to be popped open to inspect them.
        /// </summary>
        private System.Collections.Generic.List<Menu.DrawableMenuItem> menuRows()
            => dropdown().ChildrenOfType<Menu.DrawableMenuItem>().ToList();

        private static DifficultyInfo? difficultyOf(Menu.DrawableMenuItem row)
            => (row.Item as DropdownMenuItem<DifficultyInfo?>)?.Value;

        private Drawable badgesFor(DifficultyInfo difficulty)
            => menuRows().Single(r => difficultyOf(r) == difficulty)
                         .ChildrenOfType<DifficultySwitcher.DifficultyBadges>().Single();

        private IconUsage iconOf(DifficultyInfo difficulty)
            => badgesFor(difficulty).ChildrenOfType<SpriteIcon>().First().Icon;

        private Drawable starPillOf(DifficultyInfo difficulty)
            => badgesFor(difficulty).ChildrenOfType<CircularContainer>().Single();

        private string pillTextOf(DifficultyInfo difficulty)
            => starPillOf(difficulty).ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>().Single().Text.ToString();

        private Color4 pillColourOf(DifficultyInfo difficulty)
            => starPillOf(difficulty).ChildrenOfType<Box>().Single().Colour;

        /// <summary>A three-difficulty set whose alphabetical order (Hard, Insane, Normal) and star
        /// order (Normal 2.10, Hard 3.20, Insane 4.90) disagree — so a test can tell which one the
        /// dropdown actually used. Mirrors the set in the user's screenshot.</summary>
        private CachedBeatmapSet gradedSet(int setId = 110)
        {
            var set = new CachedBeatmapSet { SetId = setId, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "hard.osu" };
            set.OsuFiles.AddRange(new[] { "hard.osu", "insane.osu", "normal.osu" });
            set.Difficulties.Add(new DifficultyInfo { Path = "hard.osu", Version = "Hard", Mode = 0 });
            set.Difficulties.Add(new DifficultyInfo { Path = "insane.osu", Version = "Insane", Mode = 0 });
            set.Difficulties.Add(new DifficultyInfo { Path = "normal.osu", Version = "Normal", Mode = 0 });
            return set;
        }

        private static BeatmapSetInfo gradedRatings(int setId = 110) => new BeatmapSetInfo
        {
            Id = setId,
            Title = "Graded",
            Beatmaps =
            {
                new BeatmapInfo { Mode = "osu", Version = "Hard", DifficultyRating = 3.20 },
                new BeatmapInfo { Mode = "osu", Version = "Insane", DifficultyRating = 4.90 },
                new BeatmapInfo { Mode = "osu", Version = "Normal", DifficultyRating = 2.10 },
            },
        };

        private string[] listedVersions() => dropdown().Items.Select(d => d!.Version).ToArray();

        /// <summary>
        /// A mania set as it really arrives: the .osu files on disk carry plain difficulty names,
        /// while osu! (and every mirror, which proxies the same data) serves them decorated with the
        /// key count. Names and ratings are the real ones from beatmapset 653740, "WHITEOUT".
        /// </summary>
        private CachedBeatmapSet maniaSet(int setId = 120)
        {
            var set = new CachedBeatmapSet { SetId = setId, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "novice.osu" };
            set.OsuFiles.AddRange(new[] { "basic.osu", "novice.osu", "advanced.osu", "exhaust.osu", "heavenly.osu" });
            set.Difficulties.Add(new DifficultyInfo { Path = "basic.osu", Version = "Makii's BASIC", Mode = 3 });
            set.Difficulties.Add(new DifficultyInfo { Path = "novice.osu", Version = "NOVICE", Mode = 3 });
            set.Difficulties.Add(new DifficultyInfo { Path = "advanced.osu", Version = "Amii's ADVANCED", Mode = 3 });
            set.Difficulties.Add(new DifficultyInfo { Path = "exhaust.osu", Version = "Virtue's EXHAUST", Mode = 3 });
            set.Difficulties.Add(new DifficultyInfo { Path = "heavenly.osu", Version = "Chicken's HEAVENLY", Mode = 3 });
            return set;
        }

        private static BeatmapSetInfo maniaRatings(int setId = 120) => new BeatmapSetInfo
        {
            Id = setId,
            Title = "WHITEOUT",
            Beatmaps =
            {
                new BeatmapInfo { Mode = "mania", Version = "[4K] Makii's BASIC", DifficultyRating = 1.26 },
                new BeatmapInfo { Mode = "mania", Version = "[4K] NOVICE", DifficultyRating = 2.03 },
                new BeatmapInfo { Mode = "mania", Version = "[4K] Amii's ADVANCED", DifficultyRating = 3.15 },
                new BeatmapInfo { Mode = "mania", Version = "[4K] Virtue's EXHAUST", DifficultyRating = 3.95 },
                new BeatmapInfo { Mode = "mania", Version = "[4K] Chicken's HEAVENLY", DifficultyRating = 5.25 },
            },
        };

        // osu-web prefixes a mania difficulty's name with its key count; the .osu file on disk never
        // carries that, so a plainly-named mania set matched nothing at all — no pills, alphabetical
        // order, and no hardest-difficulty default. Exactly the user's screenshot.
        [Test]
        public void ManiaDifficultiesMatchThroughOsuWebsKeyCountPrefix()
        {
            AddStep("play a mania set", () =>
            {
                playback.Current.Value = maniaSet();
                playback.SelectedOsuFile.Value = "novice.osu";
                jukebox.NowPlaying.Value = maniaRatings();
            });

            AddUntilStep("every row picks up its pill", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r => r.ChildrenOfType<CircularContainer>().Any()));

            AddAssert("sorted easiest-first despite the decorated names",
                () => listedVersions().SequenceEqual(new[]
                {
                    "Makii's BASIC", "NOVICE", "Amii's ADVANCED", "Virtue's EXHAUST", "Chicken's HEAVENLY",
                }));

            AddAssert("BASIC is labelled with its own 1.26",
                () => pillTextOf(playback.Current.Value!.Difficulties.Single(d => d.Version == "Makii's BASIC")) == "1.26");

            AddUntilStep("and playback started on the 5.25 difficulty",
                () => playback.SelectedOsuFile.Value == "heavenly.osu");
        }

        // Mappers who put the key count in the name themselves get it served back unchanged, so the
        // exact match must keep working — the prefix rule must not eat a name's own leading text.
        [Test]
        public void ManiaDifficultiesThatAlreadyNameTheirKeyCountStillMatch()
        {
            AddStep("play a set named like beatmapset 1974347", () =>
            {
                var set = new CachedBeatmapSet { SetId = 121, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "e.osu" };
                set.OsuFiles.AddRange(new[] { "e.osu", "h.osu" });
                set.Difficulties.Add(new DifficultyInfo { Path = "e.osu", Version = "10K Easy", Mode = 3 });
                set.Difficulties.Add(new DifficultyInfo { Path = "h.osu", Version = "14K DP Hard", Mode = 3 });

                playback.Current.Value = set;
                playback.SelectedOsuFile.Value = "e.osu";
                jukebox.NowPlaying.Value = new BeatmapSetInfo
                {
                    Id = 121,
                    Beatmaps =
                    {
                        new BeatmapInfo { Mode = "mania", Version = "10K Easy", DifficultyRating = 2.40 },
                        new BeatmapInfo { Mode = "mania", Version = "14K DP Hard", DifficultyRating = 4.10 },
                    },
                };
            });

            AddUntilStep("both rows rated", () => menuRows()
                .Count(r => difficultyOf(r) != null && r.ChildrenOfType<CircularContainer>().Any()) == 2);
            AddAssert("easy keeps its own rating",
                () => pillTextOf(playback.Current.Value!.Difficulties.Single(d => d.Version == "10K Easy")) == "2.40");
        }

        // A wrong rating is worse than none: it would sort a difficulty into the wrong place and
        // label it with a number that isn't its own. Two key variants sharing one name collapse to
        // the same undecorated string, so that difficulty gets no pill rather than a coin flip.
        [Test]
        public void AmbiguousUndecoratedManiaNamesYieldNoRating()
        {
            AddStep("play a set with 4K and 7K difficulties sharing a name", () =>
            {
                var set = new CachedBeatmapSet { SetId = 122, Directory = tmp, AudioFile = fixtureSet.AudioFile, PreferredOsuFile = "i.osu" };
                set.OsuFiles.AddRange(new[] { "i.osu", "n.osu" });
                set.Difficulties.Add(new DifficultyInfo { Path = "i.osu", Version = "Insane", Mode = 3 });
                set.Difficulties.Add(new DifficultyInfo { Path = "n.osu", Version = "Normal", Mode = 3 });

                playback.Current.Value = set;
                playback.SelectedOsuFile.Value = "i.osu";
                jukebox.NowPlaying.Value = new BeatmapSetInfo
                {
                    Id = 122,
                    Beatmaps =
                    {
                        new BeatmapInfo { Mode = "mania", Version = "[4K] Insane", DifficultyRating = 4.10 },
                        new BeatmapInfo { Mode = "mania", Version = "[7K] Insane", DifficultyRating = 5.60 },
                        new BeatmapInfo { Mode = "mania", Version = "[4K] Normal", DifficultyRating = 2.20 },
                    },
                };
            });

            AddUntilStep("the unambiguous difficulty is rated", () => menuRows()
                .Any(r => difficultyOf(r)?.Version == "Normal" && r.ChildrenOfType<CircularContainer>().Any()));
            AddAssert("the ambiguous one is left unrated", () => menuRows()
                .Single(r => difficultyOf(r)?.Version == "Insane")
                .ChildrenOfType<CircularContainer>().Any() == false);
        }

        // Sorted easiest-first: the list used to come out in scan order, which is alphabetical by
        // filename, so a set read as Hard / Insane / Normal — nonsense as a difficulty ladder.
        [Test]
        public void DifficultyDropdownListsEasiestFirst()
        {
            AddStep("play a graded set with ratings", () =>
            {
                playback.Current.Value = gradedSet();
                playback.SelectedOsuFile.Value = "hard.osu";
                jukebox.NowPlaying.Value = gradedRatings();
            });

            AddUntilStep("list sorted by ascending stars",
                () => listedVersions().SequenceEqual(new[] { "Normal", "Hard", "Insane" }));

            // The framework flows rows in Items order, but assert the drawn rows too — the list the
            // user reads is the one that matters, and it is a different object graph.
            AddAssert("and the drawn rows follow the same order", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .OrderBy(r => r.ScreenSpaceDrawQuad.TopLeft.Y)
                .Select(r => difficultyOf(r)!.Version)
                .SequenceEqual(new[] { "Normal", "Hard", "Insane" }));
        }

        // THE REAL SEQUENCE, and the one the tests above skip by publishing both together: Jukebox
        // starts the set first and publishes its metadata a few frames later, so the list is
        // necessarily built before any rating exists. Everything rating-shaped has to catch up when
        // it lands — including the rows' own pills, which are built with the rows.
        //
        // The alphabetical order here already matches the star order, so nothing about the SEQUENCE
        // of items changes when the ratings arrive. That is the case the first fix got wrong: it
        // rebuilt only on a changed order, leaving a rated header sitting above unrated rows.
        [Test]
        public void RatingsArrivingAfterPlaybackStartsStillReachTheRows()
        {
            AddStep("start the set with no metadata yet", () =>
            {
                playback.Current.Value = gradedSet(118);
                playback.SelectedOsuFile.Value = "hard.osu";
                jukebox.NowPlaying.Value = null;
            });

            AddUntilStep("rows built, unrated", () => menuRows().Count(r => difficultyOf(r) != null) == 3
                                                     && menuRows().All(r => !r.ChildrenOfType<CircularContainer>().Any()));

            AddStep("metadata lands, ordered so the sequence does NOT change", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 118,
                Title = "Already in order",
                Beatmaps =
                {
                    new BeatmapInfo { Mode = "osu", Version = "Hard", DifficultyRating = 3.20 },
                    new BeatmapInfo { Mode = "osu", Version = "Insane", DifficultyRating = 4.90 },
                    new BeatmapInfo { Mode = "osu", Version = "Normal", DifficultyRating = 5.50 },
                },
            });

            AddUntilStep("every row picks up its pill", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r => r.ChildrenOfType<CircularContainer>().Any()));
            AddAssert("order is unchanged, as this fixture intends",
                () => listedVersions().SequenceEqual(new[] { "Hard", "Insane", "Normal" }));
            AddAssert("and the header agrees with the rows", () => headerBadges()
                .SelectMany(b => b.ChildrenOfType<CircularContainer>()).Any());
        }

        // Star ratings are computed, not stored in a .osu file, so a set with no online metadata (a
        // local folder, a dropped .osz) has nothing to rank by. It must keep a stable, sensible
        // order rather than shuffling — which means the scan order it always used.
        [Test]
        public void DifficultyDropdownFallsBackToScanOrderWithoutRatings()
        {
            AddStep("play a graded set with NO ratings", () =>
            {
                playback.Current.Value = gradedSet(111);
                playback.SelectedOsuFile.Value = "hard.osu";
                jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 111, Title = "No ratings" };
            });

            AddUntilStep("list keeps its alphabetical scan order",
                () => listedVersions().SequenceEqual(new[] { "Hard", "Insane", "Normal" }));
        }

        // Ratings for the set being played — NOT whatever NowPlaying happens to hold. Jukebox
        // publishes NowPlaying only after PlayAsync has already moved PlaybackController.Current, so
        // there is a window where NowPlaying still describes the previous song; difficulty names
        // like "Hard"/"Normal" recur across sets, so an unguarded match would sort and label a
        // difficulty by a different song's numbers.
        [Test]
        public void DifficultyDropdownIgnoresRatingsBelongingToAnotherSet()
        {
            AddStep("play a set while NowPlaying still describes a different one", () =>
            {
                jukebox.NowPlaying.Value = gradedRatings(999); // same version names, different set
                playback.Current.Value = gradedSet(112);
                playback.SelectedOsuFile.Value = "hard.osu";
            });

            AddWaitStep("let the switcher settle", 5);

            AddAssert("no pill borrowed from the other set", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r => !r.ChildrenOfType<CircularContainer>().Any()));
            AddAssert("and the order stays the scan order",
                () => listedVersions().SequenceEqual(new[] { "Hard", "Insane", "Normal" }));
        }

        // The regression this round exists for: every REAL row rendered a bare name while the closed
        // header showed its badges, because the framework builds a row's content from inside a base
        // constructor — before the subclass field holding the badges is assigned.
        [Test]
        public void DifficultyDropdownRowsCarryTheirIconAndPill()
        {
            AddStep("play a graded set with ratings", () =>
            {
                playback.Current.Value = gradedSet(113);
                playback.SelectedOsuFile.Value = "hard.osu";
                jukebox.NowPlaying.Value = gradedRatings(113);
            });

            AddUntilStep("rows built", () => menuRows().Count(r => difficultyOf(r) != null) == 3);

            AddAssert("every row draws a badge strip", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r => r.ChildrenOfType<DifficultySwitcher.DifficultyBadges>().Any()));
            AddAssert("every row draws a ruleset icon", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r => r.ChildrenOfType<SpriteIcon>().Any(i => i.Icon.Equals(OsuIcon.RulesetOsu))));
            AddAssert("every row draws its own rating", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .Select(r => r.ChildrenOfType<CircularContainer>().Single()
                              .ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>().Single().Text.ToString())
                .OrderBy(t => t)
                .SequenceEqual(new[] { "2.10", "3.20", "4.90" }));
            // The badge strip is auto-sized, so the label's inset is measured a frame late and only
            // while the row is actually running Update — i.e. once the menu is open. Open it and
            // poll, rather than asserting on a closed menu that never ran that pass.
            AddStep("open the menu", () =>
            {
                InputManager.MoveMouseTo(dropdown().ChildrenOfType<DropdownHeader>().Single());
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("every label is pushed clear of its badges", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r => r.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                           .Any(t => t.Text.ToString() == difficultyOf(r)!.Version
                                     && t.Padding.Left > unbadged_label_inset)));

            // …by at least the width of those badges, so the two can't collide. Compared against the
            // measured strip rather than a constant: the pill's width follows its rendered digits.
            AddAssert("by at least the badge strip's own width", () => menuRows()
                .Where(r => difficultyOf(r) != null)
                .All(r =>
                {
                    var badges = r.ChildrenOfType<DifficultySwitcher.DifficultyBadges>().Single();
                    var label = r.ChildrenOfType<osu.Framework.Graphics.Sprites.SpriteText>()
                                 .First(t => t.Text.ToString() == difficultyOf(r)!.Version);
                    return badges.DrawWidth > 0 && label.Padding.Left >= unbadged_label_inset + badges.DrawWidth;
                }));
        }

        /// <summary>Lazer's own label inset on an unbadged dropdown row — a badged row must clear
        /// it by the width of its badges.</summary>
        private const float unbadged_label_inset = 15;

        // A set's own default difficulty is just the first osu!std file on disk — an arbitrary pick.
        // Starting on the hardest is the useful default for a storyboard viewer.
        [Test]
        public void PlayingASetStartsOnItsHardestDifficulty()
        {
            AddStep("play a graded set with ratings", () =>
            {
                playback.Current.Value = gradedSet(114);
                playback.SelectedOsuFile.Value = "hard.osu"; // the set's own PreferredOsuFile
                jukebox.NowPlaying.Value = gradedRatings(114);
            });

            AddUntilStep("switched to the 4.90 difficulty", () => playback.SelectedOsuFile.Value == "insane.osu");
            AddUntilStep("and the dropdown agrees", () => dropdown().Current.Value?.Version == "Insane");
        }

        // …but only once. Moving off it afterwards must stick, or the picker would drag the user
        // back to the hardest difficulty every time anything republished NowPlaying.
        [Test]
        public void TheHardestDefaultDoesNotFightAManualPick()
        {
            AddStep("play a graded set with ratings", () =>
            {
                playback.Current.Value = gradedSet(115);
                playback.SelectedOsuFile.Value = "hard.osu";
                jukebox.NowPlaying.Value = gradedRatings(115);
            });

            AddUntilStep("started on the hardest", () => playback.SelectedOsuFile.Value == "insane.osu");

            AddStep("user picks the easiest", () => dropdown().Current.Value =
                playback.Current.Value!.Difficulties.Single(d => d.Version == "Normal"));
            AddUntilStep("playback followed", () => playback.SelectedOsuFile.Value == "normal.osu");

            AddStep("something republishes the same set's metadata", () => jukebox.NowPlaying.Value = gradedRatings(115));
            AddWaitStep("let the switcher settle", 5);

            AddAssert("still on the difficulty the user chose", () => playback.SelectedOsuFile.Value == "normal.osu");
        }

        // A replay is tied to one exact .osu (matched by checksum at import), so auto-switching to
        // the hardest difficulty would silently drop it back to autoplay on a difficulty the player
        // never played.
        [Test]
        public void TheHardestDefaultLeavesAReplayBackedSetAlone()
        {
            AddStep("play a graded set carrying a replay on the EASIEST difficulty", () =>
            {
                playback.Current.Value = gradedSet(116);
                playback.SelectedOsuFile.Value = "normal.osu";

                var info = gradedRatings(116);
                info.Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "Cookiezi", OsuFile = "normal.osu" };
                jukebox.NowPlaying.Value = info;
            });

            AddUntilStep("dropdown is locked", () => dropdown().Current.Disabled);
            AddWaitStep("let the switcher settle", 5);

            AddAssert("still on the replay's own difficulty", () => playback.SelectedOsuFile.Value == "normal.osu");
            AddAssert("and the dropdown still shows it", () => dropdown().Current.Value?.Version == "Normal");
        }

        // No ratings, nothing to rank by — the set keeps the difficulty it would have played before.
        [Test]
        public void TheHardestDefaultIsSkippedWithoutRatings()
        {
            AddStep("play a graded set with NO ratings", () =>
            {
                playback.Current.Value = gradedSet(117);
                playback.SelectedOsuFile.Value = "hard.osu";
                jukebox.NowPlaying.Value = new BeatmapSetInfo { Id = 117, Title = "No ratings" };
            });

            AddWaitStep("let the switcher settle", 5);

            AddAssert("still on the set's own default", () => playback.SelectedOsuFile.Value == "hard.osu");
        }

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
                Replay = new JukeBox.Game.Replays.ReplayAttachment
                {
                    PlayerName = "Cookiezi",
                    ModAcronyms = new[] { "HD", "HR", "DT" },
                    RateTempo = 1.5,
                },
            });

            AddUntilStep("player credited", () => nowPlaying.ReplayText.Text.ToString() == "Played by Cookiezi");
            AddAssert("mods listed under the player", () => nowPlaying.ReplayModsText.Text.ToString() == "HD HR DT");
            AddAssert("mods read below the player",
                () => nowPlaying.ReplayModsText.ScreenSpaceDrawQuad.TopLeft.Y
                      >= nowPlaying.ReplayText.ScreenSpaceDrawQuad.TopLeft.Y);
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
            AddAssert("and so does the mods row", () => nowPlaying.ReplayModsText.Text.ToString().Length == 0);
        }

        // A no-mod play must not leave an empty row sitting under the credit.
        [Test]
        public void ANoModReplayShowsTheCreditWithNoModsRow()
        {
            AddStep("set NowPlaying with a no-mod replay", () => jukebox.NowPlaying.Value = new BeatmapSetInfo
            {
                Id = 10,
                Title = "Nomod",
                Creator = "Someone",
                Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "Vaxei" },
            });

            AddUntilStep("player credited", () => nowPlaying.ReplayText.Text.ToString() == "Played by Vaxei");
            AddAssert("no mods row", () => nowPlaying.ReplayModsText.Text.ToString().Length == 0);
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
                Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "Rafis", ModAcronyms = new[] { "HD", "DT" } },
            }));

            AddUntilStep("row is laid out", () => queuePanel.RowCount == 1);

            AddAssert("player and mods share the line", () => rowTexts().Contains("Played by Rafis · HD DT"));
            AddAssert("mapper line given over to it", () => !rowTexts().Contains("mapped by Row Mapper"));
            AddAssert("title still shown", () => rowTexts().Contains("Replayed Row"));
        }

        [Test]
        public void QueueRowOmitsTheModSeparatorForANoModReplay()
        {
            AddStep("enqueue a set with a no-mod replay", () => queue.Enqueue(new BeatmapSetInfo
            {
                Id = 999,
                Title = "Nomod Row",
                Creator = "Row Mapper",
                Replay = new JukeBox.Game.Replays.ReplayAttachment { PlayerName = "WhiteCat" },
            }));

            AddUntilStep("row is laid out", () => queuePanel.RowCount == 1);
            AddAssert("credit carries no trailing separator", () => rowTexts().Contains("Played by WhiteCat"));
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
