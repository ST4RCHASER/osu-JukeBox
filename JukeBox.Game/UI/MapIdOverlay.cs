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
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// Small centred modal for queueing a beatmap set directly by its numeric beatmapset ID, rather
/// than searching by title — opened via the corner "#" button in <see cref="Screens.MainScreen"/>,
/// styled like <see cref="SettingsOverlay"/> (same dim scrim + rounded panel-surface card). Enter
/// (or clicking Add) parses the entered text as an int and looks it up through
/// <see cref="IBeatmapMirror.SearchAsync"/> using NeriNyan's field-restricted "setId" option; if
/// that doesn't return the id as its first result (e.g. a fallback mirror that ignores
/// <see cref="SearchRequest.Option"/>), retries with a plain query and filters client-side. A
/// match fires <see cref="SetResolved"/> and closes the overlay; invalid input or a failed lookup
/// shows inline error text (in soft red) and leaves the overlay open to retry.
/// </summary>
public partial class MapIdOverlay : FocusedOverlayContainer
{
    private const float panel_width = 360;

    public event Action<BeatmapSetInfo>? SetResolved;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    private Container panelCard = null!;
    private AccentTextBox idBox = null!;
    private IconButton addButton = null!;
    private SpriteText statusText = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the ID text box.
    /// </summary>
    internal AccentTextBox IdBox => idBox;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the status/error text.
    /// </summary>
    internal SpriteText ErrorText => statusText;

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
                                Text = "Queue by beatmapset ID",
                            },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 40,
                                Children = new Drawable[]
                                {
                                    idBox = new AccentTextBox
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Padding = new MarginPadding { Right = 48 },
                                        PlaceholderText = "beatmapset ID",
                                    },
                                    addButton = new IconButton
                                    {
                                        Anchor = Anchor.CentreRight,
                                        Origin = Anchor.CentreRight,
                                        Size = new Vector2(40),
                                        Icon = FontAwesome.Solid.Plus,
                                        IdleColour = Theme.Accent,
                                        HoverColour = Theme.Accent.Lighten(0.15f),
                                        Action = () => _ = lookUpAsync(),
                                    },
                                }
                            },
                            statusText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                Colour = Theme.Error,
                            },
                        }
                    }
                }
            }
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

        // Scheduled for the same reason as BeatmapListingOverlay.ShowWithInitialChar: FocusedOverlayContainer
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
                Hide();
                return true;

            case Key.Enter:
            case Key.KeypadEnter:
                // Also handled via idBox.OnCommit when the text box itself has focus; kept here
                // too as a fallback for whenever the overlay itself ends up focused instead (see
                // BeatmapListingOverlay.OnKeyDown for the same pattern).
                _ = lookUpAsync();
                return true;
        }

        return base.OnKeyDown(e);
    }

    private async Task lookUpAsync()
    {
        string text = idBox.Text;

        if (!int.TryParse(text, out int id))
        {
            statusText.Text = "enter a numeric beatmapset ID";
            return;
        }

        int mySequence = ++lookupSequence;

        statusText.Text = "looking up…";
        idBox.Current.Disabled = true;
        addButton.Enabled.Value = false;

        BeatmapSetInfo? found = null;

        try
        {
            var restricted = new SearchRequest { Query = text, Option = "setId" };
            var restrictedResults = await mirror.SearchAsync(restricted).ConfigureAwait(false);
            found = restrictedResults.Count > 0 && restrictedResults[0].Id == id ? restrictedResults[0] : null;

            if (found == null)
            {
                var fallback = new SearchRequest { Query = text };
                var fallbackResults = await mirror.SearchAsync(fallback).ConfigureAwait(false);
                found = fallbackResults.FirstOrDefault(s => s.Id == id);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"MapIdOverlay: lookup of set {id} failed");
        }

        Schedule(() =>
        {
            // A newer lookup superseded this one while it was in flight — drop this response.
            if (mySequence != lookupSequence)
                return;

            idBox.Current.Disabled = false;
            addButton.Enabled.Value = true;

            if (found == null)
            {
                statusText.Text = "beatmapset not found";
                return;
            }

            statusText.Text = string.Empty;
            SetResolved?.Invoke(found);
            Hide();
        });
    }
}
