#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Input;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osuTK.Input;
using VolumeOverlay = osu.Game.Overlays.VolumeOverlay;

namespace JukeBox.Game.Tests.Visual
{
    // InputManager.ChangeFocus is obsolete in favour of GetContainingFocusManager().ChangeFocus(),
    // but that accessor is protected and resolves THIS scene's focus manager — which is the one
    // OUTSIDE the nested ManualInputManager the test drives. Focusing through it leaves
    // InputManager.FocusedDrawable unchanged, so the suppression case never actually focused its
    // text box. The obsolete overload is the one that targets the manual manager, which is the
    // whole point of the fixture.
#pragma warning disable CS0618
    /// <summary>
    /// The global playback shortcuts, driven through the REAL input manager rather than by calling
    /// the handler's methods.
    ///
    /// <para>
    /// That distinction is the whole point of this fixture: the first working version of this
    /// feature resolved its two overlays out of DI and compiled, ran, and did nothing at all,
    /// because both are SIBLINGS and a <c>[Cached]</c> drawable only serves its own subtree. A test
    /// that constructed the pieces and called into them directly would have passed throughout. So
    /// every case here presses a key and asserts on the state a user would see.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestScenePlaybackShortcuts : JukeBoxManualInputTestScene
    {
        private JukeBoxConfigManager config = null!;
        private VolumeOverlay volume = null!;
        private TransientValueOverlay readout = null!;
        private AccentTextBox textBox = null!;

        [Resolved]
        private osu.Framework.Audio.AudioManager audio { get; set; } = null!;

        /// <summary>
        /// RESOLVED, not constructed. PlaybackShortcuts resolves its controller out of DI, so a
        /// locally-built one is a different object entirely — the shortcuts would drive the game's
        /// instance while the assertions read a bystander, and every transport case failed while the
        /// feature worked perfectly in the app. Same trap the overlays hit from the other side.
        /// </summary>
        [Resolved]
        private PlaybackController playback { get; set; } = null!;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            config = new JukeBoxConfigManager(new TemporaryNativeStorage(Path.Combine("jukebox-shortcuts-test", Path.GetRandomFileName())));

            var deps = new DependencyContainer(parent);
            deps.Cache(config);
            return deps;
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("build the shortcut layer over its overlays", () =>
            {
                config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0);

                volume = new VolumeOverlay();
                readout = new TransientValueOverlay();
                textBox = new AccentTextBox { Size = new osuTK.Vector2(200, 30) };

                // Same shape the main screen uses: the overlays are siblings, handed to the
                // shortcut layer rather than resolved out of the container.
                Children = new Drawable[]
                {
                    volume,
                    readout,
                    textBox,
                    new PlaybackShortcuts(volume, readout),
                };
            });

            AddUntilStep("everything loaded", () => volume.IsLoaded && readout.IsLoaded);
            // The controller is the GAME's, shared by every test in this fixture, so each one has
            // to start from a known transport state rather than inheriting the last one's.
            AddStep("clear focus and reset the transport", () =>
            {
                InputManager.ChangeFocus(null);
                playback.Stop();
                playback.PlaybackRate.Value = 1;
            });
        }

        private void press(Key key, params Key[] modifiers)
        {
            AddStep($"press {string.Join("+", modifiers.Append(key))}", () =>
            {
                foreach (var m in modifiers)
                    InputManager.PressKey(m);

                InputManager.PressKey(key);
                InputManager.ReleaseKey(key);

                foreach (var m in modifiers)
                    InputManager.ReleaseKey(m);
            });
        }

        // ---- Transport --------------------------------------------------------------------------

        [Test]
        public void SpaceTogglesPlayback()
        {
            AddAssert("not playing to begin with", () => !playback.IsPlaying);

            press(Key.Space);
            AddAssert("playing", () => playback.IsPlaying);

            press(Key.Space);
            AddAssert("paused again", () => !playback.IsPlaying);
        }

        /// <summary>
        /// A held key must not toggle pause at the repeat rate. Asserted against the rule rather
        /// than by trying to synthesise an auto-repeat, which the manual input manager does not
        /// generate — the rule is what the handler actually consults.
        /// </summary>
        [Test]
        public void OnlyContinuousAdjustmentsActOnKeyRepeat()
        {
            AddAssert("space does not repeat", () => !PlaybackShortcuts.RepeatableKey(Key.Space));
            AddAssert("home does not repeat", () => !PlaybackShortcuts.RepeatableKey(Key.Home));
            AddAssert("media keys do not repeat", () => !PlaybackShortcuts.RepeatableKey(Key.TrackNext));

            AddAssert("seek repeats", () => PlaybackShortcuts.RepeatableKey(Key.Left)
                                            && PlaybackShortcuts.RepeatableKey(Key.Right));
            AddAssert("volume repeats", () => PlaybackShortcuts.RepeatableKey(Key.Up)
                                              && PlaybackShortcuts.RepeatableKey(Key.Down));
            AddAssert("speed repeats", () => PlaybackShortcuts.RepeatableKey(Key.PageUp)
                                             && PlaybackShortcuts.RepeatableKey(Key.PageDown));
            AddAssert("zoom repeats", () => PlaybackShortcuts.RepeatableKey(Key.Plus)
                                            && PlaybackShortcuts.RepeatableKey(Key.Minus));
        }

        [Test]
        public void ArrowsSeekSmallAndCtrlArrowsSeekBig()
        {
            AddStep("start a minute in", () => playback.Seek(60000));

            press(Key.Right);
            AddAssert($"forward {PlaybackShortcuts.SmallSeekMs}ms",
                () => Math.Abs(playback.CurrentTimeMs - (60000 + PlaybackShortcuts.SmallSeekMs)) < 1);

            press(Key.Left);
            AddAssert("back where it started", () => Math.Abs(playback.CurrentTimeMs - 60000) < 1);

            press(Key.Right, Key.ControlLeft);
            AddAssert($"forward {PlaybackShortcuts.BigSeekMs}ms — a bigger step than the plain arrow",
                () => Math.Abs(playback.CurrentTimeMs - (60000 + PlaybackShortcuts.BigSeekMs)) < 1
                      && PlaybackShortcuts.BigSeekMs > PlaybackShortcuts.SmallSeekMs);

            press(Key.Left, Key.ControlLeft);
            AddAssert("back where it started", () => Math.Abs(playback.CurrentTimeMs - 60000) < 1);
        }

        [Test]
        public void SeekingBackwardsStopsAtTheStart()
        {
            AddStep("start one second in", () => playback.Seek(1000));

            press(Key.Left);
            AddAssert("clamped to zero rather than negative", () => playback.CurrentTimeMs >= 0 && playback.CurrentTimeMs < 1);
        }

        [Test]
        public void HomeRestartsTheSong()
        {
            AddStep("start a minute in", () => playback.Seek(60000));

            press(Key.Home);
            AddAssert("back at the start", () => playback.CurrentTimeMs < 1);
        }

        // ---- Speed and zoom, with their readout -------------------------------------------------

        [Test]
        public void PageKeysChangeSpeedAndShowIt()
        {
            AddAssert("starts at 1x", () => Math.Abs(playback.PlaybackRate.Value - 1) < 0.001);

            press(Key.PageUp);
            AddAssert("one step faster", () => Math.Abs(playback.PlaybackRate.Value - (1 + PlaybackShortcuts.SpeedStep)) < 0.001);
            AddAssert("and the readout says so", () => readout.LabelText == "Speed" && readout.ValueText.StartsWith("1.05"));
            AddAssert("readout is visible", () => readout.Alpha > 0);

            press(Key.PageDown);
            press(Key.PageDown);
            AddAssert("one step slower than 1x", () => Math.Abs(playback.PlaybackRate.Value - (1 - PlaybackShortcuts.SpeedStep)) < 0.001);
            AddAssert("readout followed", () => readout.ValueText.StartsWith("0.95"));
        }

        [Test]
        public void ZoomKeysChangeThePlayfieldZoomAndShowThePercentage()
        {
            press(Key.Plus, Key.AltLeft);
            AddAssert("zoomed in one step",
                () => Math.Abs(config.Get<double>(JukeBoxSetting.PlayfieldZoom) - (1 + PlaybackShortcuts.ZoomStep)) < 0.001);
            AddAssert("readout shows the percentage",
                () => readout.LabelText == "Playfield zoom" && readout.ValueText == "105%");

            press(Key.Minus, Key.AltLeft);
            press(Key.Minus, Key.AltLeft);
            AddAssert("zoomed out past 100%",
                () => Math.Abs(config.Get<double>(JukeBoxSetting.PlayfieldZoom) - (1 - PlaybackShortcuts.ZoomStep)) < 0.001);
            AddAssert("readout shows 95%", () => readout.ValueText == "95%");

            AddStep("reset with the zero key", () => { });
            press(Key.Number0, Key.AltLeft);
            AddAssert("back to 100%", () => Math.Abs(config.Get<double>(JukeBoxSetting.PlayfieldZoom) - 1) < 0.001);
            AddAssert("readout shows 100%", () => readout.ValueText == "100%");
        }

        /// <summary>Cmd on macOS and Alt elsewhere both drive zoom — one muscle memory, either
        /// platform. Asserted because binding only one of them is a silent no-op on the other OS.</summary>
        [Test]
        public void EitherZoomModifierWorks()
        {
            press(Key.Plus, Key.AltLeft);
            AddAssert("alt zoomed", () => config.Get<double>(JukeBoxSetting.PlayfieldZoom) > 1);

            AddStep("back to 100%", () => config.SetValue(JukeBoxSetting.PlayfieldZoom, 1.0));

            press(Key.Plus, Key.WinLeft);
            AddAssert("cmd zoomed too", () => config.Get<double>(JukeBoxSetting.PlayfieldZoom) > 1);
        }

        // ---- Volume, through lazer's own overlay ------------------------------------------------

        /// <summary>
        /// Lazer's own semantics, kept deliberately: the first press with the meters hidden reveals
        /// them WITHOUT changing anything, and presses after that adjust.
        /// </summary>
        [Test]
        public void VolumeKeysRevealLazersMetersThenAdjustThem()
        {
            // Setting the volume is itself enough to pop the meters up (lazer binds Show to every
            // meter's value), so they have to be put away again before the "first press only
            // reveals" behaviour can be observed at all.
            AddStep("start from a known volume, meters away", () =>
            {
                audio.Volume.Value = 0.5;
                volume.Hide();
            });
            AddUntilStep("meters hidden", () => volume.State.Value == Visibility.Hidden);

            press(Key.Down);
            AddAssert("the meters came up", () => volume.State.Value == Visibility.Visible);
            AddAssert("but nothing moved yet", () => Math.Abs(audio.Volume.Value - 0.5) < 0.001);

            press(Key.Down);
            AddAssert("now it goes down", () => audio.Volume.Value < 0.5);

            double lowered = 0;
            AddStep("remember it", () => lowered = audio.Volume.Value);

            press(Key.Up);
            AddAssert("and back up", () => audio.Volume.Value > lowered);
        }

        [Test]
        public void LazersOwnAltCombosDriveTheSameOverlay()
        {
            AddStep("start from a known volume", () => audio.Volume.Value = 0.5);

            press(Key.Up, Key.AltLeft);
            AddAssert("the meters came up", () => volume.State.Value == Visibility.Visible);

            press(Key.Up, Key.AltLeft);
            AddAssert("alt+up raises master", () => audio.Volume.Value > 0.5);

            // Alt+Left/Right cycle which meter the volume keys act on — lazer's real binding.
            press(Key.Right, Key.AltLeft);

            double master = 0, track = 0;
            AddStep("remember both", () =>
            {
                master = audio.Volume.Value;
                track = audio.VolumeTrack.Value;
            });

            press(Key.Down);
            AddAssert("a different meter moved, not master",
                () => Math.Abs(audio.Volume.Value - master) < 0.001 && Math.Abs(audio.VolumeTrack.Value - track) > 0.001);
        }

        [Test]
        public void CtrlF4MutesOnLazersOwnBinding()
        {
            AddAssert("not muted", () => !volume.IsMuted.Value);

            press(Key.F4, Key.ControlLeft);
            AddAssert("muted", () => volume.IsMuted.Value);

            press(Key.F4, Key.ControlLeft);
            AddAssert("unmuted", () => !volume.IsMuted.Value);
        }

        /// <summary>
        /// The docked beatmap listing holds focus for the whole session, and the focused drawable
        /// gets first refusal on every key — so its Up/Down result-navigation was quietly taking
        /// those keys away from the rest of the app permanently, even with nothing to navigate.
        /// With an empty result list it now declines them, which is what lets the volume keys work
        /// at all while you are just listening to music.
        /// </summary>
        [Test]
        public void TheEmptyListingLetsTheArrowKeysThrough()
        {
            BeatmapListingOverlay listing = null!;

            AddStep("put a focused, empty docked listing in front", () =>
            {
                listing = new BeatmapListingOverlay(docked: true) { RelativeSizeAxes = Axes.Both };
                Add(listing);
            });

            AddUntilStep("listing loaded", () => listing.IsLoaded);
            AddStep("give it focus, as the real column has", () => InputManager.ChangeFocus(listing));
            AddUntilStep("focused", () => InputManager.FocusedDrawable == listing);
            AddAssert("and it really has no results to navigate", () => !listing.Engine.LoadedSets.Any());

            AddStep("known volume, meters away", () =>
            {
                audio.Volume.Value = 0.5;
                volume.Hide();
            });
            AddUntilStep("meters hidden", () => volume.State.Value == Visibility.Hidden);

            press(Key.Down);
            AddAssert("the volume meters came up despite the listing holding focus",
                () => volume.State.Value == Visibility.Visible);

            press(Key.Down);
            AddAssert("and the volume actually moved", () => audio.Volume.Value < 0.5);

            // BOTH arrows, deliberately: the listing handles Up and Down in separate cases, so a
            // test that only ever pressed one would keep passing while the other went back to
            // swallowing its key.
            double lowered = 0;
            AddStep("remember it", () => lowered = audio.Volume.Value);

            press(Key.Up);
            AddAssert("up passes through too", () => audio.Volume.Value > lowered);
        }

        /// <summary>
        /// The shortcuts are only useful if they can be found. This pins the one place that says so
        /// — a caption under the very transport controls the keys drive — against a layout edit
        /// quietly dropping it.
        /// </summary>
        [Test]
        public void ThePlaybackTabSaysWhatTheKeysAre()
        {
            PlaybackPanel panel = null!;

            AddStep("host a playback tab", () => Add(panel = new PlaybackPanel { RelativeSizeAxes = Axes.Both }));
            AddUntilStep("loaded", () => panel.IsLoaded);

            AddAssert("the hint names the everyday keys", () =>
            {
                string hint = panel.ShortcutHint.Text.ToString();

                return hint.Contains("Space") && hint.Contains("seek")
                       && hint.Contains("volume") && hint.Contains("speed");
            });
        }

        // ---- Suppression while typing -----------------------------------------------------------

        /// <summary>
        /// The one rule that makes every other binding safe. Tested against the framework's TextBox
        /// base rather than one specific box, because that is what the guard tests — a text field
        /// added later is covered without anyone remembering to come back here.
        /// </summary>
        [Test]
        public void NothingFiresWhileATextBoxHasFocus()
        {
            AddStep("focus the text box", () => InputManager.ChangeFocus(textBox));
            AddUntilStep("focused", () => InputManager.FocusedDrawable == textBox);

            AddStep("note the starting state", () => playback.Seek(30000));

            double time = 0, rate = 0, zoom = 0;
            AddStep("remember everything the shortcuts could touch", () =>
            {
                time = playback.CurrentTimeMs;
                rate = playback.PlaybackRate.Value;
                zoom = config.Get<double>(JukeBoxSetting.PlayfieldZoom);
            });

            press(Key.Space);
            press(Key.Home);
            press(Key.PageUp);
            press(Key.Plus, Key.AltLeft);

            AddAssert("playback untouched", () => !playback.IsPlaying && Math.Abs(playback.CurrentTimeMs - time) < 1);
            AddAssert("speed untouched", () => Math.Abs(playback.PlaybackRate.Value - rate) < 0.001);
            AddAssert("zoom untouched", () => Math.Abs(config.Get<double>(JukeBoxSetting.PlayfieldZoom) - zoom) < 0.001);

            AddStep("release focus", () => InputManager.ChangeFocus(null));

            press(Key.Space);
            AddAssert("and it works again once the box is done with it", () => playback.IsPlaying);
        }
    }
}
#pragma warning restore CS0618
