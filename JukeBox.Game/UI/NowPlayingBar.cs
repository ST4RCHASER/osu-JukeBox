#nullable enable

using System;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// Bottom-anchored, full-width playback bar: cover thumb (placeholder — Task 13 wires the real
/// artwork), title/artist (from <see cref="Playback.Jukebox.NowPlaying"/>), a seekable progress
/// bar, play/pause + skip buttons and a volume slider bound directly to
/// <see cref="PlaybackController.Volume"/>.
/// </summary>
public partial class NowPlayingBar : CompositeDrawable
{
    private const float bar_height = 80;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    // Local, not bound straight to CurrentTimeMs/LengthMs. TransferValueOnCommit only gates one
    // direction — user drag input reaching this bindable (`Current`) is deferred until commit —
    // but the OTHER direction is unconditional: SliderBar<T>'s constructor wires
    // `current.ValueChanged` straight into its internal drag-preview value with no such gate
    // (confirmed by decompiling SliderBar<T>, since no local framework source is available). So
    // without also checking progressBar.IsDragged, Update()'s periodic write below would still
    // stomp the live drag preview every frame while a drag is in progress, snapping the handle
    // back to playback position before the user's drag is committed. settingProgress guards the
    // separate, narrower problem of that same write re-triggering a Seek via the ValueChanged
    // handler in LoadComplete.
    private readonly BindableDouble progress = new BindableDouble { MinValue = 0, MaxValue = 1 };
    private bool settingProgress;

    private BasicSliderBar<double> progressBar = null!;
    private BasicButton playPauseButton = null!;
    private SpriteText titleText = null!;
    private SpriteText artistText = null!;

    /// <summary>
    /// Test-only access to the play/pause button (JukeBox.Game.Tests has InternalsVisibleTo), to
    /// drive it via <see cref="Drawable.TriggerClick"/> without depending on its exact position.
    /// </summary>
    internal BasicButton PlayPauseButton => playPauseButton;

    /// <summary>
    /// Test-only access to the progress bar (JukeBox.Game.Tests has InternalsVisibleTo), to drive
    /// a real mouse drag over it and observe its <c>Current</c>/<see cref="Drawable.IsDragged"/>.
    /// </summary>
    internal BasicSliderBar<double> ProgressBar => progressBar;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.X;
        Height = bar_height;
        Anchor = Anchor.BottomLeft;
        Origin = Anchor.BottomLeft;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = new Color4(20, 20, 20, 255),
            },
            new Box // cover thumb placeholder; Task 13 replaces with the real artwork texture.
            {
                Position = new Vector2(8, 8),
                Size = new Vector2(64, 64),
                Colour = Color4.DarkSlateGray,
            },
            new FillFlowContainer
            {
                Position = new Vector2(80, 8),
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Children = new Drawable[]
                {
                    titleText = new SpriteText { Font = FontUsage.Default.With(size: 18) },
                    artistText = new SpriteText { Font = FontUsage.Default.With(size: 14), Colour = Color4.Gray },
                }
            },
            new Container
            {
                Position = new Vector2(80, 56),
                RelativeSizeAxes = Axes.X,
                Padding = new MarginPadding { Right = 260 },
                Height = 8,
                Child = progressBar = new BasicSliderBar<double>
                {
                    RelativeSizeAxes = Axes.Both,
                    Current = progress,
                    TransferValueOnCommit = true,
                    BackgroundColour = Color4.Gray,
                    SelectionColour = Color4.White,
                }
            },
            new FillFlowContainer
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new Vector2(-8, 8),
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(8, 0),
                Children = new Drawable[]
                {
                    playPauseButton = new BasicButton
                    {
                        Size = new Vector2(48, 32),
                        Text = "⏯",
                        Action = () => playback.TogglePause(),
                    },
                    new BasicButton
                    {
                        Size = new Vector2(48, 32),
                        Text = "⏭",
                        Action = () => jukebox.SkipCurrent(),
                    },
                    new BasicSliderBar<double>
                    {
                        Size = new Vector2(80, 32),
                        Current = playback.Volume,
                        BackgroundColour = Color4.Gray,
                        SelectionColour = Color4.White,
                    },
                }
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        jukebox.NowPlaying.BindValueChanged(onNowPlayingChanged, true);

        progress.BindValueChanged(e =>
        {
            // A commit fired from our own periodic Update() write, not a user drag — ignore it,
            // otherwise every frame would re-seek to (approximately) where playback already is.
            if (settingProgress)
                return;

            playback.Seek(e.NewValue * playback.LengthMs);
        });
    }

    protected override void Update()
    {
        base.Update();

        // Skip the write entirely while the user is actively dragging: see the comment on
        // `progress` above for why writing to it here would otherwise fight the live drag.
        if (!progressBar.IsDragged)
        {
            settingProgress = true;
            progress.Value = Math.Clamp(playback.CurrentTimeMs / Math.Max(1, playback.LengthMs), 0, 1);
            settingProgress = false;
        }

        playPauseButton.Text = playback.IsPlaying ? "⏸" : "▶";
    }

    private void onNowPlayingChanged(ValueChangedEvent<BeatmapSetInfo?> change)
    {
        titleText.Text = change.NewValue?.DisplayTitle ?? string.Empty;
        artistText.Text = change.NewValue?.DisplayArtist ?? string.Empty;
    }
}
