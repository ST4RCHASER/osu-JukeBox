#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The "add a beatmap by ID or link" dialog — a centred modal styled like
/// <see cref="SettingsOverlay"/> (dim scrim + rounded panel-surface card), opened from the menu bar
/// (Queue → Lookup by id…).
///
/// One input accepts EITHER a bare beatmapset ID or a pasted osu.ppy.sh link; see
/// <see cref="BeatmapLink"/> for the exact set of supported shapes and why a link to a single
/// difficulty (<c>/b/…</c>) is reported back to the user rather than looked up — no mirror in
/// <see cref="IBeatmapMirror"/> can turn a beatmap id into its set, since NeriNyan's only
/// field-restricted search option is <c>setId</c>.
///
/// Two buttons: <b>Cancel</b> closes without queueing anything, <b>Lookup</b> performs the fetch.
/// Enter is Lookup, Escape is Cancel. A lookup resolves through
/// <see cref="IBeatmapMirror.SearchAsync"/> with the <c>setId</c> option; if that doesn't return
/// the id as its first result (e.g. a fallback mirror that ignores <see cref="SearchRequest.Option"/>),
/// it retries with a plain query and filters client-side. While it's in flight the input and
/// Lookup are disabled and a real <see cref="LoadingSpinner"/> spins beside them (never a
/// "loading…" line); a match fires <see cref="SetResolved"/> and closes, and a miss or a parse
/// failure leaves the dialog open with inline text in soft red so the user can correct it.
/// </summary>
public partial class MapIdOverlay : FocusedOverlayContainer
{
    private const float panel_width = 440;

    public event Action<BeatmapSetInfo>? SetResolved;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    private Container panelCard = null!;
    private AccentTextBox idBox = null!;
    private TextButton lookupButton = null!;
    private TextButton cancelButton = null!;
    private SpriteText statusText = null!;
    private LoadingSpinner lookupSpinner = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the input box.
    /// </summary>
    internal AccentTextBox IdBox => idBox;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the status/error text.
    /// </summary>
    internal SpriteText ErrorText => statusText;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the two buttons.</summary>
    internal TextButton LookupButton => lookupButton;

    internal TextButton CancelButton => cancelButton;

    // Guards against a stale lookup response (from a superseded id) overwriting a newer one.
    private int lookupSequence;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ModalScrim,
            },
            panelCard = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = panel_width,
                AutoSizeAxes = Axes.Y,
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                EdgeEffect = Theme.PanelShadow,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.PanelSurface,
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding(Theme.PanelPadding),
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, Theme.SectionSpacing),
                        Children = new Drawable[]
                        {
                            new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                                Colour = Theme.TextPrimary,
                                Text = "Add a beatmap",
                            },
                            new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                Colour = Theme.TextSecondary,
                                Text = "Paste a beatmap link from osu.ppy.sh, or type a beatmapset ID.",
                            },
                            idBox = new AccentTextBox
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 40,
                                PlaceholderText = "beatmapset ID or osu.ppy.sh link",
                            },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Children = new Drawable[]
                                {
                                    // Progress and outcome share this row: the spinner sits at the
                                    // left where the message will land, so a lookup never shifts
                                    // the dialog's height between the two states.
                                    lookupSpinner = new LoadingSpinner
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Size = new Vector2(18),
                                    },
                                    statusText = new SpriteText
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Margin = new MarginPadding { Left = 26 },
                                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                        Colour = Theme.Error,
                                    },
                                    new FillFlowContainer
                                    {
                                        Anchor = Anchor.CentreRight,
                                        Origin = Anchor.CentreRight,
                                        AutoSizeAxes = Axes.Y,
                                        Width = 200,
                                        Direction = FillDirection.Horizontal,
                                        Spacing = new Vector2(Theme.RowSpacing, 0),
                                        Children = new Drawable[]
                                        {
                                            cancelButton = new TextButton("Cancel")
                                            {
                                                Size = new Vector2(88, 34),
                                                Action = Hide,
                                            },
                                            lookupButton = new TextButton("Lookup")
                                            {
                                                Size = new Vector2(88, 34),
                                                IdleColour = Theme.AccentDim,
                                                HoverColour = Theme.Accent,
                                                Action = () => _ = lookUpAsync(),
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        idBox.OnCommit += (_, _) => _ = lookUpAsync();
    }

    protected override void PopIn()
    {
        this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
        panelCard.ScaleTo(Theme.PopScale).Then().ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter);

        statusText.Text = string.Empty;
        idBox.Text = string.Empty;

        // Scheduled for the same reason as the listing overlays' focus grabs: FocusedOverlayContainer
        // runs its own focus-contention pass when State flips to Visible, which would otherwise wipe
        // a synchronous focus grab made right here.
        Schedule(() => GetContainingFocusManager()?.ChangeFocus(idBox));
    }

    protected override void PopOut()
    {
        this.FadeOut(Theme.DurationFast, Theme.EaseExit);
        panelCard.ScaleTo(Theme.PopScale, Theme.DurationFast, Theme.EaseExit);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Repeat)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                // Escape is Cancel: same path as the button, so neither can drift from the other.
                cancelButton.TriggerClick();
                return true;

            case Key.Enter:
            case Key.KeypadEnter:
                // Also handled via idBox.OnCommit when the text box itself has focus; kept here
                // too as a fallback for whenever the overlay itself ends up focused instead.
                _ = lookUpAsync();
                return true;
        }

        return base.OnKeyDown(e);
    }

    private async Task lookUpAsync()
    {
        var link = BeatmapLink.Parse(idBox.Text);

        switch (link.Kind)
        {
            case BeatmapLinkKind.Invalid:
                statusText.Text = "enter a beatmapset ID, or paste an osu.ppy.sh beatmap link";
                return;

            case BeatmapLinkKind.Beatmap:
                // A difficulty link. Resolving its set needs a beatmap-id endpoint no mirror here
                // offers (see BeatmapLink) — say so plainly rather than firing a request that
                // can only ever come back empty.
                statusText.Text = "that link points to one difficulty — open it on the site and paste the beatmapset link instead";
                return;
        }

        int id = link.Id;

        int mySequence = ++lookupSequence;

        statusText.Text = string.Empty;
        lookupSpinner.Show();
        idBox.Current.Disabled = true;
        lookupButton.Enabled.Value = false;

        // Shared with the command-line path so both resolve a set id identically — see
        // BeatmapSetLookup for why the restricted-then-plain dance is needed at all.
        var found = await BeatmapSetLookup.ResolveAsync(mirror, id).ConfigureAwait(false);

        Schedule(() =>
        {
            // A newer lookup superseded this one while it was in flight — drop this response.
            if (mySequence != lookupSequence)
                return;

            lookupSpinner.Hide();
            idBox.Current.Disabled = false;
            lookupButton.Enabled.Value = true;

            if (found == null)
            {
                statusText.Text = $"no beatmapset {id} on this mirror";
                return;
            }

            statusText.Text = string.Empty;
            SetResolved?.Invoke(found);
            Hide();
        });
    }
}
