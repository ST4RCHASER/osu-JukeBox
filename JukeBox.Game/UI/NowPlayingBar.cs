#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// Bottom-anchored, full-width playback bar: cover thumb (fetched async from
/// <see cref="OnlineThumbnailStore"/> whenever <see cref="Playback.Jukebox.NowPlaying"/> changes;
/// a placeholder box remains underneath until it loads or if it never does), title (with a small
/// accent underline) / artist (from <see cref="Playback.Jukebox.NowPlaying"/>), a status line
/// (<see cref="Playback.Jukebox.Status"/>, styled in soft red when
/// <see cref="Playback.Jukebox.LastError"/> is set), a seekable <see cref="ProgressSliderBar"/>
/// spanning the bar's top edge (flanked by live elapsed/total time labels), a difficulty
/// <see cref="DifficultySwitcher"/> dropdown and an "open in browser" button. Playback transport
/// (play/pause/skip) and the volume slider live in Settings → Playback/Audio instead — see
/// <see cref="SettingsOverlay"/>'s <c>TransportRow</c> and its master/effect/music volume rows,
/// which this bar deliberately no longer duplicates.
/// </summary>
public partial class NowPlayingBar : CompositeDrawable
{
    private const float bar_height = 88;
    private const float cover_size = 64;
    private const float browser_button_size = 36;

    // Small downward nudge off the card's true top edge, and horizontal room reserved on each
    // side of the progress bar for its flanking mm:ss label — together these are what keep the
    // fill (and its hover glow) inset within the card's rounded surface rather than poking past
    // the corner curve (Theme.CornerRadius) the way it used to when the bar sat flush edge-to-edge.
    private const float progress_bar_top_offset = 3;
    private const float progress_bar_side_reserve = Theme.PanelPadding + 40;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private osu.Framework.Platform.GameHost host { get; set; } = null!;

    // [Resolved(canBeNull: true)] rather than a hard [Resolved]: only JukeBoxGame's own
    // [BackgroundDependencyLoader] (not JukeBoxGameBase's, shared with every test scene) caches
    // this — see the field comment on JukeBoxGameBase.dependencies — so every existing test scene
    // must keep constructing/resolving this bar fine with no store present at all.
    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    /// <summary>Test seam (JukeBox.Game.Tests has InternalsVisibleTo): replaces
    /// <see cref="osu.Framework.Platform.GameHost.OpenUrlExternally"/>, mirroring
    /// <see cref="FullscreenListingOverlay"/>'s own seam, so tests can assert the browsed URL
    /// without actually opening a browser.</summary>
    internal Action<string>? OpenUrl;

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
    private SpriteText elapsedText = null!;
    private SpriteText totalText = null!;
    private DifficultySwitcher difficultySwitcher = null!;
    private IconButton browserButton = null!;
    private SpriteText statusText = null!;
    private FillFlowContainer songInfo = null!;
    private SpriteText titleText = null!;
    private Box titleUnderline = null!;
    private SpriteText artistText = null!;

    // Last text actually written to elapsedText/totalText, as whole seconds — SpriteText.Text
    // re-lays-out glyphs on every write, so Update() (which runs every frame) only touches these
    // when the displayed second actually changes rather than on every sub-second tick.
    private int lastElapsedSeconds = -1;
    private int lastTotalSeconds = -1;

    /// <summary>
    /// Test-only access to the progress bar (JukeBox.Game.Tests has InternalsVisibleTo), to drive
    /// a real mouse drag over it and observe its <c>Current</c>/<see cref="Drawable.IsDragged"/>.
    /// </summary>
    internal ProgressSliderBar ProgressBar => progressBar;

    /// <summary>
    /// Test-only access to the difficulty switcher (JukeBox.Game.Tests has InternalsVisibleTo).
    /// </summary>
    internal DifficultySwitcher DifficultySwitcher => difficultySwitcher;

    /// <summary>
    /// Test-only access to the "open in browser" button (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to drive it via <see cref="Drawable.TriggerClick"/>.
    /// </summary>
    internal IconButton BrowserButton => browserButton;

    /// <summary>Test-only access to the elapsed/total time labels (JukeBox.Game.Tests has
    /// InternalsVisibleTo).</summary>
    internal SpriteText ElapsedText => elapsedText;

    internal SpriteText TotalText => totalText;

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
            // The signature element: spans the bar's top edge (above everything else below), inset
            // from the rounded card's edges by progress_bar_side_reserve — both to keep the fill
            // itself (and its hover glow) within the card's visible rounded surface, and to leave
            // room for the flanking elapsed/total labels below.
            progressBar = new ProgressSliderBar
            {
                RelativeSizeAxes = Axes.X,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Y = progress_bar_top_offset,
                Padding = new MarginPadding { Horizontal = progress_bar_side_reserve },
                Current = progress,
                TransferValueOnCommit = true,
            },
            elapsedText = new SpriteText
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.CentreLeft,
                Position = new Vector2(Theme.PanelPadding, progress_bar_top_offset + ProgressSliderBar.HitAreaHeight / 2f),
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextTertiary,
                Text = "0:00",
            },
            totalText = new SpriteText
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.CentreRight,
                Position = new Vector2(-Theme.PanelPadding, progress_bar_top_offset + ProgressSliderBar.HitAreaHeight / 2f),
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextTertiary,
                Text = "0:00",
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
                            // Title + artist share one wrapper so a song change can crossfade
                            // both together (see onNowPlayingChanged) rather than each swapping
                            // its text independently.
                            songInfo = new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 2),
                                // A song change fades this to 0 then straight back to 1 (see
                                // onNowPlayingChanged) — without AlwaysPresent, osu!framework
                                // throttles Update()/transform ticking for a not-present (Alpha 0)
                                // drawable, stalling that FadeIn indefinitely instead of letting
                                // it progress every frame.
                                AlwaysPresent = true,
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
                                },
                            },
                            statusText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextTertiary,
                                // See songInfo's AlwaysPresent comment above — refreshStatus()
                                // fades this to 0 then immediately back to 1.
                                AlwaysPresent = true,
                            },
                            difficultySwitcher = new DifficultySwitcher(),
                        }
                    },
                    // Opens the playing set's osu.ppy.sh page — the space transport/volume used to
                    // occupy here now sits free (both moved to Settings → Playback/Audio).
                    browserButton = new IconButton
                    {
                        Anchor = Anchor.CentreRight,
                        Origin = Anchor.CentreRight,
                        Size = new Vector2(browser_button_size),
                        Icon = FontAwesome.Solid.ExternalLinkAlt,
                        Action = openInBrowser,
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

        int elapsedSeconds = (int)(playback.CurrentTimeMs / 1000);
        if (elapsedSeconds != lastElapsedSeconds)
        {
            lastElapsedSeconds = elapsedSeconds;
            elapsedText.Text = formatTime(playback.CurrentTimeMs);
        }

        int totalSeconds = (int)(playback.LengthMs / 1000);
        if (totalSeconds != lastTotalSeconds)
        {
            lastTotalSeconds = totalSeconds;
            totalText.Text = formatTime(playback.LengthMs);
        }
    }

    private void openInBrowser()
    {
        var set = jukebox.NowPlaying.Value;
        if (set == null)
            return;

        string url = $"https://osu.ppy.sh/beatmapsets/{set.Id}";

        if (OpenUrl != null)
            OpenUrl(url);
        else
            host.OpenUrlExternally(url);
    }

    /// <summary>Formats a millisecond duration as "m:ss" (no leading zero on minutes, matching the
    /// standard music-player convention — e.g. "3:07", not "03:07").</summary>
    private static string formatTime(double ms)
    {
        if (double.IsNaN(ms) || ms < 0)
            ms = 0;

        int totalSeconds = (int)(ms / 1000);
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    private void refreshStatus()
    {
        string newText = jukebox.LastError.Value ?? jukebox.Status.Value ?? string.Empty;
        Color4 newColour = jukebox.LastError.Value != null ? Theme.Error : Theme.TextTertiary;

        if (newText == statusText.Text.ToString())
        {
            statusText.Colour = newColour;
            return;
        }

        // A SpriteText can't crossfade its own glyphs, so the fade+swap+fade is done in two
        // steps: fade out, swap the string once fully invisible, fade the new text back in.
        statusText.FadeOut(Theme.DurationFast, Theme.EaseExit).OnComplete(_ =>
        {
            statusText.Text = newText;
            statusText.Colour = newColour;
            statusText.FadeIn(Theme.DurationFast, Theme.EaseEnter);
        });
    }

    private void onNowPlayingChanged(ValueChangedEvent<BeatmapSetInfo?> change)
    {
        string title = change.NewValue?.DisplayTitle ?? string.Empty;
        string artist = change.NewValue?.DisplayArtist ?? string.Empty;

        songInfo.FadeOut(Theme.DurationFast, Theme.EaseExit).OnComplete(_ =>
        {
            titleText.Text = title;
            artistText.Text = artist;
            songInfo.FadeIn(Theme.DurationFast, Theme.EaseEnter);
        });

        int myGeneration = ++thumbnailGeneration;

        // The previous set's cover no longer matches what's playing (or nothing is playing) —
        // crossfade it out (rather than cutting it instantly) while the new one loads/fades in
        // over it below; the placeholder box underneath stays put throughout either way.
        var oldCover = coverSprite;
        coverSprite = null;
        oldCover?.FadeOut(Theme.DurationNormal, Theme.EaseExit).Expire();

        if (change.NewValue == null || thumbnailStore == null)
            return;

        _ = loadThumbnailAsync(change.NewValue.Id, myGeneration);
    }

    private async Task loadThumbnailAsync(int setId, int generation)
    {
        Texture? texture;

        try
        {
            texture = await thumbnailStore!.GetAsync($"https://b.ppy.sh/thumb/{setId}l.jpg").ConfigureAwait(false);
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

            // Drawn on top of (added after) the placeholder box from load(), so it simply covers
            // it; fading in rather than snapping is the other half of the crossfade started in
            // onNowPlayingChanged (which faded the previous cover out).
            coverContainer.Add(coverSprite = new Sprite
            {
                RelativeSizeAxes = Axes.Both,
                FillMode = FillMode.Fill,
                Texture = texture,
                Alpha = 0,
                // See songInfo's AlwaysPresent comment in load() — this starts at Alpha 0 and
                // fades in immediately below.
                AlwaysPresent = true,
            });
            coverSprite.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
        });
    }
}
