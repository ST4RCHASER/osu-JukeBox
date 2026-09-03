#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Several people's plays shown at once when they are NOT on the same map — a pane each, every one
/// running its own beatmap, its own clock and its own audio.
///
/// <para>
/// The sibling of <see cref="MultiReplayGrid"/> rather than a variant of it, and the split is
/// forced by what a clock can do. The grid renders N replays of ONE difficulty, which is why its
/// cells can share a working beatmap, a background texture and a single driving clock — that
/// sharing is the whole reason it can keep the plays in sync and compare them. Different maps have
/// nothing to share: different lengths, different audio, different everything. So each pane here is
/// self-contained, and "in sync" is not a goal that even means anything.
/// </para>
///
/// <para>
/// Consequently there is no shared-rate compromise here. <see cref="MultiReplayLayout.SharedRate"/>
/// exists because the grid has one audio track between its cells and a DoubleTime play cannot be
/// driven correctly by a no-mod clock; a pane owns its track, so it simply plays at its own
/// replay's rate.
/// </para>
///
/// <para>
/// Storyboards are deliberately not rendered per pane. The shared grid already caps them at
/// <see cref="MultiReplayLayout.STORYBOARD_CELL_LIMIT"/> cells when every cell draws the SAME
/// storyboard; N different ones, each with its own resource store, is a cost with no comparison
/// value to justify it. Each pane gets its map's background instead.
/// </para>
/// </summary>
public partial class IndependentReplayPanes : CompositeDrawable
{
    private readonly IReadOnlyList<SpectateEntry> entries;

    private readonly List<Pane> panes = new List<Pane>();

    /// <summary>Test-only (JukeBox.Game.Tests has InternalsVisibleTo): the built panes, so a test can
    /// assert per-pane clocks and volumes without reaching into the layout.</summary>
    internal IReadOnlyList<Pane> Panes => panes;

    /// <summary>Test-only: the grid the panes were laid out in.</summary>
    internal GridShape Shape { get; private set; }

    public IndependentReplayPanes(IReadOnlyList<SpectateEntry> entries)
    {
        // NOT capped again here: SpectatePanePlan.Shape already refuses to lay out more than
        // MAX_PANES cells, and the build loop is bounded by those cells. A second Rendered() call
        // looked like belt-and-braces but no behaviour depended on it — a mutation test proved it
        // dead. One mechanism, tested where it lives.
        this.entries = entries;

        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load(AudioManager audio, GameHost host)
    {
        Shape = SpectatePanePlan.Shape(entries.Count);

        var volumes = SpectatePanePlan.InitialVolumes(entries.Count);

        var content = new Drawable[Shape.Rows][];

        for (int row = 0; row < Shape.Rows; row++)
        {
            content[row] = new Drawable[Shape.Columns];

            for (int column = 0; column < Shape.Columns; column++)
            {
                int index = row * Shape.Columns + column;

                content[row][column] = index < entries.Count
                    ? buildPane(entries[index], volumes[index], audio, host)
                    : Empty();
            }
        }

        InternalChild = new GridContainer
        {
            RelativeSizeAxes = Axes.Both,
            Content = content,
        };
    }

    private Drawable buildPane(SpectateEntry entry, double volume, AudioManager audio, GameHost host)
    {
        var pane = new Pane(entry, volume, audio, host);
        panes.Add(pane);
        return pane;
    }

    protected override void Update()
    {
        base.Update();

        foreach (var pane in panes)
            pane.Advance();
    }

    /// <summary>
    /// One player's pane: their map, their replay, their clock, their audio.
    /// </summary>
    internal partial class Pane : CompositeDrawable
    {
        private readonly SpectateEntry entry;
        private readonly AudioManager audio;
        private readonly GameHost host;

        /// <summary>
        /// This pane's own clock, and the reason panes exist at all. Decoupled so it keeps running
        /// (and the chart keeps rendering) even before the track has loaded or if it never does —
        /// the same arrangement <c>PlaybackController</c> uses for the app's single track.
        /// </summary>
        private readonly DecouplingFramedClock clock = new DecouplingFramedClock { AllowDecoupling = true };

        private Track? track;
        private ITrackStore? store;

        /// <summary>
        /// How loud this pane is, 0-1. Bound by the UI's per-player control; the FIRST pane starts
        /// at 1 and the rest at 0 (see <see cref="SpectatePanePlan.InitialVolumes"/>), because four
        /// unrelated songs at once is noise rather than a feature.
        /// </summary>
        public readonly BindableDouble Volume = new BindableDouble { MinValue = 0, MaxValue = 1 };

        /// <summary>Whether the player's name is drawn over their pane.</summary>
        public readonly BindableBool ShowName = new BindableBool(true);

        /// <summary>Whether the live score/accuracy/combo readout is drawn.</summary>
        public readonly BindableBool ShowNumbers = new BindableBool(true);

        internal LazerChartLayer Chart { get; private set; } = null!;

        /// <summary>
        /// Test-only: the clock this pane's CONTENT actually runs on — read off the chart rather
        /// than off the field, because the field exists whether or not it was ever attached to the
        /// subtree, and "each pane made a clock object" is not the claim worth defending.
        /// </summary>
        internal IFrameBasedClock EffectiveClock => Chart.Clock;

        /// <summary>Test-only: the loaded track, so the rate a pane really plays at can be asserted
        /// rather than the rate it was handed.</summary>
        internal Track? LoadedTrack => track;

        internal Drawable NameLabel => nameLabel;

        internal Drawable ScoreLabel => scoreLabel;

        /// <summary>Test-only: the rate this pane plays at — its own replay's, never a shared one.</summary>
        internal double Rate => entry.Replay.Rate;

        private OsuSpriteText nameLabel = null!;
        private OsuSpriteText scoreLabel = null!;
        private OsuSpriteText accuracyLabel = null!;
        private OsuSpriteText comboLabel = null!;

        private bool boundLiveScore;

        public Pane(SpectateEntry entry, double volume, AudioManager audio, GameHost host)
        {
            this.entry = entry;
            this.audio = audio;
            this.host = host;

            Volume.Value = volume;

            RelativeSizeAxes = Axes.Both;
            Masking = true;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // The pane's whole subtree runs on the pane's clock — that one assignment is what makes
            // these independent rather than N views of one timeline.
            Clock = clock;

            Chart = new LazerChartLayer(new FlatWorkingBeatmap(entry.OsuFile), entry.OsuFile, entry.Replay.Score)
            {
                AlwaysPresent = true,
                TrackLiveScore = true,
            };

            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black },
                Chart,
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(6),
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Vertical,
                            Children = new Drawable[]
                            {
                                scoreLabel = label(MultiReplayGrid.FormatScore(entry.Replay), 20, FontWeight.SemiBold, Anchor.TopCentre),
                                accuracyLabel = label(MultiReplayGrid.FormatAccuracy(entry.Replay), 14, FontWeight.Regular, Anchor.TopCentre),
                            },
                        },
                        comboLabel = label(MultiReplayGrid.FormatCombo(entry.Replay), 16, FontWeight.SemiBold, Anchor.BottomLeft),
                        nameLabel = label(entry.DisplayName, 14, FontWeight.SemiBold, Anchor.BottomRight),
                    },
                },
            };

            loadTrackAsync();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            ShowName.BindValueChanged(e => nameLabel.Alpha = e.NewValue ? 1 : 0, true);
            ShowNumbers.BindValueChanged(e =>
            {
                float alpha = e.NewValue ? 1 : 0;
                scoreLabel.Alpha = alpha;
                accuracyLabel.Alpha = alpha;
                comboLabel.Alpha = alpha;
            }, true);

            // Applied to the TRACK rather than through an AudioContainer, because the track is not
            // in the drawable tree — it is played by this pane's clock, not by the scene graph. The
            // chart's own hitsounds are drawables and do follow the container, which is why both
            // paths are covered below.
            Volume.BindValueChanged(e =>
            {
                if (track != null)
                    track.Volume.Value = e.NewValue;

                Chart.HitSoundsEnabled.Value = e.NewValue > 0;
            }, true);
        }

        /// <summary>
        /// Loads this pane's audio and starts its clock on it. Failure is survivable on purpose: a
        /// pane whose track will not decode still renders the play silently, which is a better
        /// answer than a blank cell.
        /// </summary>
        private async void loadTrackAsync()
        {
            try
            {
                string? audioFile = await Task.Run(() => findAudioFile()).ConfigureAwait(false);

                if (audioFile == null)
                    return;

                var loadedStore = audio.GetTrackStore(new StorageBackedResourceStore(new NativeStorage(entry.SetDirectory, host)));
                var loaded = loadedStore.Get(audioFile);

                if (loaded == null)
                {
                    loadedStore.Dispose();
                    return;
                }

                Schedule(() =>
                {
                    store = loadedStore;
                    track = loaded;

                    track.Volume.Value = Volume.Value;

                    // Its OWN replay's rate. No reconciliation with the other panes, because there
                    // is no shared track to reconcile against.
                    track.Tempo.Value = entry.Replay.RateTempo;
                    track.Frequency.Value = entry.Replay.RateFrequency;

                    clock.ChangeSource(track);
                    clock.Start();
                });
            }
            catch (Exception e)
            {
                Logger.Log($"Spectate pane for {entry.DisplayName} could not load its audio: {e.Message}",
                    LoggingTarget.Runtime, LogLevel.Debug);
            }
        }

        /// <summary>The set's audio file name, read from the difficulty being played.</summary>
        private string? findAudioFile()
        {
            try
            {
                var info = Beatmaps.OsuFileScanner.Scan(entry.OsuFile);
                return string.IsNullOrEmpty(info.AudioFilename) ? null : info.AudioFilename;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Steps this pane's clock. Called from the owner's Update rather than this drawable's own,
        /// because a clock has to be processed before the subtree that reads it draws — the same
        /// ordering <c>PlaybackController</c> keeps for the app's clock.
        /// </summary>
        public void Advance()
        {
            clock.ProcessFrame();

            if (boundLiveScore || Chart.LiveScore is not { } processor)
                return;

            boundLiveScore = true;

            processor.TotalScore.BindValueChanged(e => scoreLabel.Text = ((long)e.NewValue).ToString("00000000"), true);
            processor.Accuracy.BindValueChanged(e => accuracyLabel.Text = (e.NewValue * 100).ToString("0.00") + "%", true);
            processor.Combo.BindValueChanged(e => comboLabel.Text = e.NewValue.ToString("N0") + "x", true);
        }

        private static OsuSpriteText label(string text, float size, FontWeight weight, Anchor anchor) => new OsuSpriteText
        {
            Anchor = anchor,
            Origin = anchor,
            Text = text,
            Font = OsuFont.Torus.With(size: size, weight: weight),
            Colour = Color4.White,
            Shadow = true,
        };

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            // Every pane holds its OWN track store, and AudioManager retains each one it hands out
            // until disposed — so four panes rotating through a spectate session would leak four
            // stores per rotation without this.
            track?.Dispose();
            store?.Dispose();
        }
    }
}
