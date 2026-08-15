#nullable enable

using System;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The "now playing" presentation: cover thumb (fetched async from
/// <see cref="OnlineThumbnailStore"/> whenever <see cref="Playback.Jukebox.NowPlaying"/> changes;
/// a placeholder box remains underneath until it loads or if it never does), the song block —
/// title / artist / "mapped by" credit, all from <see cref="Playback.Jukebox.NowPlaying"/> — a
/// status line
/// (<see cref="Playback.Jukebox.Status"/>, styled in soft red when
/// <see cref="Playback.Jukebox.LastError"/> is set), the <see cref="TransportRow"/> transport strip
/// (which carries the "open in browser" button as its trailing entry), a seekable
/// <see cref="ProgressSliderBar"/> with elapsed/total time labels beneath it, and a difficulty
/// <see cref="DifficultySwitcher"/> dropdown.
///
/// <para>
/// Laid out VERTICALLY for a narrow (~340px) column: this used to be a full-width bar pinned along
/// the bottom of the window, but that strip is gone — everything it carried now lives in the right
/// column's "Playback" tab (see <see cref="PlaybackPanel"/>, which stacks this panel above the
/// playback-speed slider). This panel is chrome-less by design (no card of its own, no fixed
/// height): it sizes to its content and sits directly on the owning column's surface rather than
/// painting a second panel on top of it. Reading order is song → controls → position → difficulty:
/// the transport sits directly under the song it acts on, above the progress bar rather than below
/// the whole stack.
/// </para>
/// </summary>
public partial class NowPlayingPanel : CompositeDrawable
{
    private const float cover_size = 64;

    /// <summary>Room for the progress bar's hit area plus the elapsed/total labels below it.</summary>
    private const float progress_block_height = ProgressSliderBar.HitAreaHeight + Theme.CaptionTextSize + 4;

    /// <summary>Height of the status line — its caption text plus a little breathing room, which is
    /// also what the loading spinner beside it is sized against.</summary>
    private const float status_row_height = Theme.CaptionTextSize + 6;

    private const float status_spinner_size = 14;

    /// <summary>Room reserved at the right of the status line for the "42%" readout.</summary>
    private const float status_percent_width = 40;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private osu.Framework.Platform.GameHost host { get; set; } = null!;

    // [Resolved(canBeNull: true)] rather than a hard [Resolved]: only JukeBoxGame's own
    // [BackgroundDependencyLoader] (not JukeBoxGameBase's, shared with every test scene) caches
    // this — see the field comment on JukeBoxGameBase.dependencies — so every existing test scene
    // must keep constructing/resolving this panel fine with no store present at all.
    [Resolved(canBeNull: true)]
    private OnlineThumbnailStore? thumbnailStore { get; set; }

    // canBeNull for the same reason as thumbnailStore: a test scene that only drives NowPlaying/
    // Status directly needs no cache at all. Without one the status line still shows the jukebox's
    // own "Downloading …" text and spinner, just never a percentage.
    [Resolved(canBeNull: true)]
    private BeatmapCache? cache { get; set; }

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
    private TransportRow transport = null!;
    private SpriteText statusText = null!;
    private Container statusTextContainer = null!;
    private SpriteText statusPercentText = null!;
    private LoadingSpinner statusSpinner = null!;
    private FillFlowContainer songInfo = null!;

    // Last state pushed into the status line's spinner/percentage by updateDownloadIndicator(),
    // which runs every frame — a percent of -1 means "no measurable progress to show". Guards both
    // the SpriteText write (which re-lays out glyphs) and the padding write behind an actual change.
    private bool lastStatusBusy;
    private int lastStatusPercent = -1;
    private SpriteText titleText = null!;
    private SpriteText artistText = null!;
    private SpriteText mapperText = null!;

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
    /// InternalsVisibleTo), to drive it via <see cref="Drawable.TriggerClick"/> — it lives in the
    /// transport strip (see <see cref="TransportRow.BrowserButton"/>), not in the title row.
    /// </summary>
    internal IconButton BrowserButton => transport.BrowserButton!;

    /// <summary>Test-only access to the transport strip (JukeBox.Game.Tests has
    /// InternalsVisibleTo), to assert where it sits in the stack.</summary>
    internal TransportRow Transport => transport;

    /// <summary>Test-only access to the elapsed/total time labels (JukeBox.Game.Tests has
    /// InternalsVisibleTo).</summary>
    internal SpriteText ElapsedText => elapsedText;

    internal SpriteText TotalText => totalText;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the
    /// downloading/buffering indicator beside the status line — see
    /// <see cref="updateDownloadIndicator"/>.</summary>
    internal bool DownloadSpinnerShown => statusSpinner.State.Value == Visibility.Visible;

    internal string StatusText => statusText.Text.ToString();

    internal string DownloadPercentText => statusPercentText.Alpha > 0 ? statusPercentText.Text.ToString() : string.Empty;

    /// <summary>Test-only access to the song block's three lines (JukeBox.Game.Tests has
    /// InternalsVisibleTo).</summary>
    internal SpriteText TitleText => titleText;

    internal SpriteText ArtistText => artistText;

    internal SpriteText MapperText => mapperText;

    /// <summary>Test-only: the song block's own drawables, so a test can assert what the block is
    /// made of (no accent rule, three text lines) without reaching into its layout.</summary>
    internal FillFlowContainer SongInfo => songInfo;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;

        InternalChild = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, Theme.RowSpacing),
            Children = new Drawable[]
            {
                // Cover beside the title/artist block, with the browser button parked at the far
                // right of the same row — the one horizontal band in an otherwise stacked layout.
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = cover_size,
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
                        // Padding (rather than a positioned child) is what reserves the cover's
                        // column: the text inside is relatively sized, so it needs a parent whose
                        // width is already the space actually left for it. The row's whole
                        // remainder is the text's now — the browser button moved to the transport
                        // strip below.
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding { Left = cover_size + Theme.SectionSpacing },
                            Child = songInfo = new FillFlowContainer
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 2),
                                // Title + artist share one wrapper so a song change can crossfade
                                // both together (see onNowPlayingChanged) rather than each swapping
                                // its text independently.
                                //
                                // A song change fades this to 0 then straight back to 1 (see
                                // onNowPlayingChanged) — without AlwaysPresent, osu!framework
                                // throttles Update()/transform ticking for a not-present (Alpha 0)
                                // drawable, stalling that FadeIn indefinitely instead of letting
                                // it progress every frame.
                                AlwaysPresent = true,
                                // Title / artist / mapper, three plain lines with no rule between
                                // them: an accent underline used to sit under the title, but a
                                // full-width coloured bar inside a stack this small reads as a
                                // section divider cutting the song's own metadata in half. The type
                                // scale (primary → secondary → tertiary) already establishes the
                                // hierarchy, and the freed line gives the mapper credit a home.
                                Children = new Drawable[]
                                {
                                    titleText = new SpriteText
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Truncate = true,
                                        Font = FontUsage.Default.With(size: Theme.RowTitleTextSize),
                                        Colour = Theme.TextPrimary,
                                    },
                                    artistText = new SpriteText
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Truncate = true,
                                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                        Colour = Theme.TextSecondary,
                                    },
                                    mapperText = new SpriteText
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Truncate = true,
                                        Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                        Colour = Theme.TextTertiary,
                                    },
                                },
                            },
                        },
                    },
                },
                // The status line is a fixed-height band rather than an auto-sizing one so the
                // spinner (anchored to its vertical centre) and the percentage on the far right
                // have something stable to sit in, and so the stack below doesn't shift by a pixel
                // as the line's own content comes and goes.
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = status_row_height,
                    Children = new Drawable[]
                    {
                        statusSpinner = new LoadingSpinner
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new Vector2(status_spinner_size),
                        },
                        statusTextContainer = new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Child = statusText = new SpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                RelativeSizeAxes = Axes.X,
                                Truncate = true,
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextTertiary,
                                // See songInfo's AlwaysPresent comment above — refreshStatus() fades
                                // this to 0 then immediately back to 1.
                                AlwaysPresent = true,
                            },
                        },
                        // Kept out of statusText rather than appended to it: the percentage ticks
                        // many times a second, and refreshStatus crossfades on every text change —
                        // folding the two together would leave the status line permanently
                        // mid-crossfade for the whole download.
                        statusPercentText = new SpriteText
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                            Colour = Theme.Accent,
                            Alpha = 0,
                        },
                    },
                },
                // The transport is a cluster, not a row of full-width controls, so it's centred in
                // its own full-width wrapper (a FillFlowContainer positions its children along the
                // flow axis, so centring has to come from a wrapper rather than the child's own
                // Anchor).
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Child = transport = new TransportRow(playback, jukebox, openInBrowser)
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                    },
                },
                // The progress bar spans the panel's full width, with its two time labels tucked
                // underneath it (rather than flanking it, as they did while this was a wide bar —
                // there is no horizontal room to spare in a column this narrow).
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = progress_block_height,
                    Children = new Drawable[]
                    {
                        progressBar = new ProgressSliderBar
                        {
                            RelativeSizeAxes = Axes.X,
                            Anchor = Anchor.TopLeft,
                            Origin = Anchor.TopLeft,
                            Current = progress,
                            TransferValueOnCommit = true,
                        },
                        elapsedText = new SpriteText
                        {
                            Anchor = Anchor.BottomLeft,
                            Origin = Anchor.BottomLeft,
                            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                            Colour = Theme.TextTertiary,
                            Text = "0:00",
                        },
                        totalText = new SpriteText
                        {
                            Anchor = Anchor.BottomRight,
                            Origin = Anchor.BottomRight,
                            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                            Colour = Theme.TextTertiary,
                            Text = "0:00",
                        },
                    },
                },
                difficultySwitcher = new DifficultySwitcher(),
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

        updateDownloadIndicator();

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

    /// <summary>
    /// The "this song isn't playing yet, it's still coming down" indicator: a
    /// <see cref="LoadingSpinner"/> runs beside the status line for as long as the jukebox reports
    /// it is busy with the current pick, and a percentage joins it once the mirror has advertised a
    /// total to measure against (<see cref="Playback.Jukebox.DownloadingSetId"/> paired with
    /// <see cref="BeatmapCache.TryGetDownloadProgress"/>). Both clear the moment
    /// <see cref="Playback.Jukebox.Status"/> does, which is when playback actually starts.
    /// </summary>
    private void updateDownloadIndicator()
    {
        bool busy = jukebox.Status.Value != null;
        int? downloadingId = jukebox.DownloadingSetId.Value;

        int percent = -1;

        if (busy
            && downloadingId != null
            && cache != null
            && cache.TryGetDownloadProgress(downloadingId.Value, out var progress)
            && !progress.Indeterminate)
        {
            percent = (int)(progress.Value * 100);
        }

        if (busy == lastStatusBusy && percent == lastStatusPercent)
            return;

        if (busy != lastStatusBusy)
        {
            // LoadingSpinner is a VisibilityContainer — it spins/fades itself in and out through
            // Show()/Hide() rather than a raw Alpha write.
            if (busy)
                statusSpinner.Show();
            else
                statusSpinner.Hide();
        }

        if (percent != lastStatusPercent)
        {
            statusPercentText.Alpha = percent < 0 ? 0 : 1;

            if (percent >= 0)
                statusPercentText.Text = $"{percent}%";
        }

        lastStatusBusy = busy;
        lastStatusPercent = percent;

        statusTextContainer.Padding = new MarginPadding
        {
            // The spinner sits over the text's left edge, and the percentage over its right — both
            // need their room carved out of the truncating text rather than overlapping it.
            Left = busy ? status_spinner_size + 5 : 0,
            Right = percent < 0 ? 0 : status_percent_width,
        };
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
        string creator = change.NewValue?.Creator ?? string.Empty;

        songInfo.FadeOut(Theme.DurationFast, Theme.EaseExit).OnComplete(_ =>
        {
            titleText.Text = title;
            artistText.Text = artist;
            // Sets served without a creator (and the nothing-playing state) leave this blank rather
            // than printing a credit to nobody; SpriteText collapses to zero height, so the flow
            // above simply closes up.
            mapperText.Text = creator.Length > 0 ? $"mapped by {creator}" : string.Empty;
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
