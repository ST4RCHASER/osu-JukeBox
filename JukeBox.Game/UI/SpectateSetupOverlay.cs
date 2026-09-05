#nullable enable

using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The "Setup players…" modal opened from the Spectate menu — a centred card (styled like
/// <see cref="MapIdOverlay"/>) that manages the spectate watch list, replacing the old inline
/// username field the sidebar used to carry.
///
/// <para>
/// It is a thin editor over <see cref="SpectateController"/>: a text field plus Add appends a name,
/// each listed name carries its own remove (×), and the cap of
/// <see cref="SpectateWatchList.MAX_WATCHED"/> is the controller's — <see cref="SpectateController.Add"/>
/// simply refuses beyond it, so this overlay never has to count. Starting and stopping spectating is
/// NOT here; that stays a one-press toggle on the menu itself, because this modal is about WHO is
/// watched, not whether watching is running.
/// </para>
/// </summary>
public partial class SpectateSetupOverlay : FocusedOverlayContainer
{
    private const float panel_width = 440;

    [Resolved]
    private SpectateController spectate { get; set; } = null!;

    private Container panelCard = null!;
    private AccentTextBox nameBox = null!;
    private TextButton addButton = null!;
    private TextButton closeButton = null!;
    private FillFlowContainer rows = null!;
    private SpriteText hint = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the pieces, so a test
    /// can drive the overlay the way a person does rather than through layout positions.</summary>
    internal AccentTextBox NameBox => nameBox;

    internal TextButton AddButton => addButton;

    internal TextButton CloseButton => closeButton;

    internal FillFlowContainer Rows => rows;

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
                                Text = "Spectate players",
                            },
                            new GridContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 34,
                                ColumnDimensions = new[]
                                {
                                    new Dimension(),
                                    new Dimension(GridSizeMode.Absolute, Theme.RowSpacing),
                                    new Dimension(GridSizeMode.AutoSize),
                                },
                                Content = new[]
                                {
                                    new Drawable[]
                                    {
                                        nameBox = new AccentTextBox
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Height = 34,
                                            PlaceholderText = "osu! username",
                                        },
                                        Empty(),
                                        addButton = new TextButton("Add")
                                        {
                                            Width = 70,
                                            Height = 34,
                                            IdleColour = Theme.AccentDim,
                                            HoverColour = Theme.Accent,
                                        },
                                    },
                                },
                            },
                            rows = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 2),
                            },
                            hint = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                Colour = Theme.TextTertiary,
                            },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Child = closeButton = new TextButton("Close")
                                {
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                    Size = new Vector2(88, 34),
                                    Action = Hide,
                                },
                            },
                        },
                    },
                },
            },
        };

        nameBox.OnCommit += (_, _) => add();
        addButton.Action = add;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        spectate.Revision.BindValueChanged(_ => refresh(), true);
    }

    private void add()
    {
        // The controller owns the cap: it refuses a name that would exceed MAX_WATCHED (or a
        // duplicate), and only reports true when the list actually grew — so clearing the box on
        // success and leaving what was typed on refusal both fall out of its return value.
        if (spectate.Add(nameBox.Text))
            nameBox.Text = string.Empty;

        refresh();
    }

    private void refresh()
    {
        rows.Clear();

        foreach (var player in spectate.Players)
            rows.Add(new NameRow(player.Username, () => spectate.Remove(player.Username)));

        int remaining = SpectateWatchList.MAX_WATCHED - spectate.Players.Count;

        hint.Text = remaining > 0
            ? $"Add up to {remaining} more (max {SpectateWatchList.MAX_WATCHED})."
            : $"Watch list full — {SpectateWatchList.MAX_WATCHED} is the maximum.";
    }

    protected override void PopIn()
    {
        this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
        panelCard.ScaleTo(Theme.PopScale).Then().ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter);

        nameBox.Text = string.Empty;

        // Scheduled for the same reason as the other modals' focus grabs — see MapIdOverlay.PopIn.
        Schedule(() => GetContainingFocusManager()?.ChangeFocus(nameBox));
    }

    protected override void PopOut()
    {
        this.FadeOut(Theme.DurationFast, Theme.EaseExit);
        panelCard.ScaleTo(Theme.PopScale, Theme.DurationFast, Theme.EaseExit);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!e.Repeat && e.Key == Key.Escape)
        {
            closeButton.TriggerClick();
            return true;
        }

        return base.OnKeyDown(e);
    }

    /// <summary>One watched name with its remove button. Deliberately simpler than
    /// <see cref="SpectatePanel"/>'s presence-aware row: this modal only edits the list, so it shows
    /// nothing about what each player is doing.</summary>
    private partial class NameRow : CompositeDrawable
    {
        private const float row_height = 30;

        private readonly string username;
        private readonly System.Action remove;

        public NameRow(string username, System.Action remove)
        {
            this.username = username;
            this.remove = remove;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = row_height;

            InternalChild = new GridContainer
            {
                RelativeSizeAxes = Axes.Both,
                ColumnDimensions = new[]
                {
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, row_height),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = username,
                            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                            Colour = Theme.TextPrimary,
                        },
                        new IconButton
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Size = new Vector2(22),
                            Icon = FontAwesome.Solid.Times,
                            IconColour = Theme.TextTertiary,
                            Action = remove,
                        },
                    },
                },
            };
        }
    }
}
