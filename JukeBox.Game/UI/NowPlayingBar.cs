#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Logging;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// Bottom-anchored, full-width playback bar: cover thumb (fetched async from
/// <see cref="OnlineThumbnailStore"/> whenever <see cref="Playback.Jukebox.NowPlaying"/> changes;
/// a placeholder box remains underneath until it loads or if it never does), title (with a small
/// accent underline) / artist (from <see cref="Playback.Jukebox.NowPlaying"/>), a status line
/// (<see cref="Playback.Jukebox.Status"/>, styled in soft red when
/// <see cref="Playback.Jukebox.LastError"/> is set), a seekable <see cref="ProgressSliderBar"/>
/// spanning the bar's top edge, play/pause + skip buttons and a volume slider bound directly to
/// <see cref="PlaybackController.Volume"/>.
/// </summary>
public partial class NowPlayingBar : CompositeDrawable
{
    private const float bar_height = 88;
    private const float cover_size = 64;
    private const float play_pause_size = 44;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    // [Resolved(canBeNull: true)] rather than a hard [Resolved]: only JukeBoxGame's own
    // [BackgroundDependencyLoader] (not JukeBoxGameBase's, shared with every test scene) caches
    // this — see the field comment on JukeBoxGameBase.dependencies — so every existing test scene
    // must keep constructing/resolving this bar fine with no store present at all.
    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    // Bumped every time NowPlaying changes; an in-flight thumbnail load whose generation has
    // fallen behind by the time it completes is stale (NowPlaying has since changed again, or
    // gone back to null) and must not draw its now-outdated cover over whatever's current.
    private int thumbnailGeneration;
    private Sprite? coverSprite;
    private Container coverContainer = null!;

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

    private ProgressSliderBar progressBar = null!;
    private DifficultySwitcher difficultySwitcher = null!;
    private IconButton playPauseButton = null!;
    private SpriteText statusText = null!;
    private SpriteText titleText = null!;
    private Box titleUnderline = null!;
    private SpriteText artistText = null!;

    /// <summary>
    /// Test-only access to the play/pause button (JukeBox.Game.Tests has InternalsVisibleTo), to
    /// drive it via <see cref="Drawable.TriggerClick"/> without depending on its exact position.
    /// </summary>
    internal IconButton PlayPauseButton => playPauseButton;

    /// <summary>
    /// Test-only access to the progress bar (JukeBox.Game.Tests has InternalsVisibleTo), to drive
    /// a real mouse drag over it and observe its <c>Current</c>/<see cref="Drawable.IsDragged"/>.
    /// </summary>
    internal ProgressSliderBar ProgressBar => progressBar;

    /// <summary>
    /// Test-only access to the difficulty switcher (JukeBox.Game.Tests has InternalsVisibleTo).
    /// </summary>
    internal DifficultySwitcher DifficultySwitcher => difficultySwitcher;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.X;
        Height = bar_height;
        Anchor = Anchor.BottomLeft;
        Origin = Anchor.BottomLeft;

        Masking = true;
        CornerRadius = Theme.CornerRadius;
        EdgeEffect = Theme.PanelShadow;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.PanelSurface,
            },
            // The signature element: spans the bar's full top edge, above everything else below.
            progressBar = new ProgressSliderBar
            {
                RelativeSizeAxes = Axes.X,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Current = progress,
                TransferValueOnCommit = true,
            },
            // Everything else sits below the progress bar's hit area so it never overlaps it.
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 12, Horizontal = Theme.PanelPadding },
                Children = new Drawable[]
                {
                    coverContainer = new Container
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Size = new Vector2(cover_size),
                        Masking = true,
                        CornerRadius = Theme.CornerRadius,
                        Child = new Box // placeholder; stays visible underneath until/unless the real cover loads.
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Theme.ElevatedSurface,
                        },
                    },
                    new FillFlowContainer
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Position = new Vector2(cover_size + 16, 0),
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 2),
                        Children = new Drawable[]
                        {
                            new Container
                            {
                                AutoSizeAxes = Axes.Both,
                                Children = new Drawable[]
                                {
                                    titleText = new SpriteText
                                    {
                                        Font = FontUsage.Default.With(size: Theme.RowTitleTextSize),
                                        Colour = Theme.TextPrimary,
                                    },
                                    titleUnderline = new Box
                                    {
                                        Anchor = Anchor.BottomLeft,
                                        Origin = Anchor.BottomLeft,
                                        RelativeSizeAxes = Axes.X,
                                        Height = 2,
                                        Y = 2,
                                        Colour = Theme.Accent,
                                    },
                                }
                            },
                            artistText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                Colour = Theme.TextSecondary,
                            },
                            statusText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextTertiary,
                            },
                            difficultySwitcher = new DifficultySwitcher(),
                        }
                    },
                    new FillFlowContainer
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(12, 0),
                        Children = new Drawable[]
                        {
                            playPauseButton = new IconButton
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Size = new Vector2(play_pause_size),
                                CornerRadius = play_pause_size / 2,
                                Icon = FontAwesome.Solid.Play,
                                IconColour = Theme.Background,
                                IdleColour = Theme.Accent,
                                HoverColour = Theme.Accent.Lighten(0.15f),
                                Action = () => playback.TogglePause(),
                            },
                            new IconButton
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Size = new Vector2(36),
                                Icon = FontAwesome.Solid.StepForward,
                                Action = () => jukebox.SkipCurrent(),
                            },
                        }
                    },
                    new FillFlowContainer
                    {
                        Anchor = Anchor.CentreRight,
                        Origin = Anchor.CentreRight,
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(8, 0),
                        Children = new Drawable[]
                        {
                            new SpriteIcon
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Icon = FontAwesome.Solid.VolumeUp,
                                Size = new Vector2(14),
                                Colour = Theme.TextTertiary,
                            },
                            new BasicSliderBar<double>
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Size = new Vector2(90, 6),
                                CornerRadius = 3,
                                Current = playback.Volume,
                                BackgroundColour = Theme.ElevatedSurface,
                                SelectionColour = Theme.Accent,
                                FocusColour = Theme.Accent,
                            },
                        }
                    },
                }
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        jukebox.NowPlaying.BindValueChanged(onNowPlayingChanged, true);
        jukebox.Status.BindValueChanged(_ => refreshStatus(), true);
        jukebox.LastError.BindValueChanged(_ => refreshStatus(), true);

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

        playPauseButton.Icon = playback.IsPlaying ? FontAwesome.Solid.Pause : FontAwesome.Solid.Play;
    }

    private void refreshStatus()
    {
        if (jukebox.LastError.Value != null)
        {
            statusText.Text = jukebox.LastError.Value;
            statusText.Colour = Theme.Error;
        }
        else
        {
            statusText.Text = jukebox.Status.Value ?? string.Empty;
            statusText.Colour = Theme.TextTertiary;
        }
    }

    private void onNowPlayingChanged(ValueChangedEvent<BeatmapSetInfo?> change)
    {
        titleText.Text = change.NewValue?.DisplayTitle ?? string.Empty;
        artistText.Text = change.NewValue?.DisplayArtist ?? string.Empty;

        int myGeneration = ++thumbnailGeneration;

        // The previous set's cover no longer matches what's playing (or nothing is playing) —
        // drop it immediately rather than leave a stale thumbnail up while (or if) a new one loads.
        coverSprite?.Expire();
        coverSprite = null;

        if (change.NewValue == null || thumbnailStore == null)
            return;

        _ = loadThumbnailAsync(change.NewValue.Id, myGeneration);
    }

    private async Task loadThumbnailAsync(int setId, int generation)
    {
        Texture? texture;

        try
        {
            texture = await thumbnailStore!.Store.GetAsync($"https://b.ppy.sh/thumb/{setId}l.jpg", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing/unreachable thumbnail — not fatal, the placeholder box stays up.
            Logger.Error(ex, $"Failed to load cover thumbnail for set {setId}");
            return;
        }

        if (texture == null)
            return;

        Schedule(() =>
        {
            // NowPlaying moved on again while this load was in flight — this cover is stale.
            if (generation != thumbnailGeneration)
                return;

            // Drawn on top of (added after) the placeholder box from load(), so it simply covers it.
            coverContainer.Add(coverSprite = new Sprite
            {
                RelativeSizeAxes = Axes.Both,
                FillMode = FillMode.Fill,
                Texture = texture,
            });
        });
    }
}
