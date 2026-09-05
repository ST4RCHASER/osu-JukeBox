#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace JukeBox.Game.UI.Result;

/// <summary>
/// The osu!-RANKING result screen: a full-screen overlay shown when the replay(s) reach the end, with
/// the beatmap header across the top and a grid of one <see cref="ResultPanel"/> per player beneath it.
/// A single play centres its one panel; a multi-replay combine tiles a scrolling grid that stays legible
/// from two players up to the full field (~47).
///
/// <para>
/// It is driven entirely by data handed to <see cref="Show(ResultBeatmapHeader, IReadOnlyList{PlayerResultData})"/>
/// — the lead decides WHEN the replays have ended and what each player scored; this screen only draws
/// it and stays up until the user picks <b>Next</b> or <b>Restart</b>, which fire
/// <see cref="NextRequested"/> / <see cref="RestartRequested"/>. While it is up the combine does not
/// auto-advance, so the two buttons are the only way forward.
/// </para>
/// </summary>
public partial class ResultScreen : CompositeDrawable
{
    /// <summary>Raised when the user clicks <b>Next</b>. The lead wires this to advancing the queue.</summary>
    public Action? NextRequested;

    /// <summary>Raised when the user clicks <b>Restart</b>. The lead wires this to replaying the current
    /// item from the top.</summary>
    public Action? RestartRequested;

    private FillFlowContainer headerFlow = null!;
    private SpriteText headerTitle = null!;
    private FillFlowContainer<ResultPanel> grid = null!;
    private BasicScrollContainer gridScroll = null!;

    /// <summary>Test hook (JukeBox.Game.Tests has InternalsVisibleTo): how many player panels are laid
    /// out — one per player passed to <see cref="Show"/>.</summary>
    internal int PanelCount => grid.Count;

    /// <summary>Test hook: the header's title line, so a test can confirm the beatmap block was populated.</summary>
    internal string HeaderText => headerTitle.Text.ToString();

    /// <summary>Test hook: the laid-out panels, in player order.</summary>
    internal IReadOnlyList<ResultPanel> Panels => grid.ToList();

    public ResultScreen()
    {
        RelativeSizeAxes = Axes.Both;

        // Starts hidden; Show() fades it in. Kept present so its transforms tick even while invisible.
        Alpha = 0;

        InternalChildren = new Drawable[]
        {
            // A dim scrim over the playfield, so the result screen reads as a modal layer rather than
            // floating over live gameplay.
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ModalScrim,
            },
            // A GridContainer rather than a vertical FillFlowContainer: the middle row is the scrolling
            // panel grid, which is relatively sized in the flow's own axis — a fill-flow refuses a child
            // sized relative to the direction it is flowing, whereas a grid gives that row the space left
            // between an auto-sized header and an auto-sized button row cleanly.
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(Theme.PanelPadding),
                Child = new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension(),
                        new Dimension(GridSizeMode.AutoSize),
                    },
                    Content = new[]
                    {
                        // The beatmap header across the top: title / "Beatmap by MAPPER" / "Played by …".
                        new Drawable[]
                        {
                            headerFlow = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Margin = new MarginPadding { Bottom = Theme.SectionSpacing },
                                Spacing = new Vector2(0, 2),
                                Children = new Drawable[]
                                {
                                    headerTitle = new SpriteText
                                    {
                                        Font = FontUsage.Default.With(size: Theme.HeaderTextSize, weight: "Bold"),
                                        Colour = Theme.TextPrimary,
                                    },
                                    headerArtist = new SpriteText
                                    {
                                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                        Colour = Theme.TextSecondary,
                                    },
                                    headerMapper = new SpriteText
                                    {
                                        Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                        Colour = Theme.TextTertiary,
                                    },
                                    headerPlayedBy = new SpriteText
                                    {
                                        Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                                        Colour = Theme.TextTertiary,
                                    },
                                },
                            },
                        },

                        // The panel grid, in a scroll container so a full field overflows downward rather
                        // than off the bottom of the window. Takes the height left between header and buttons.
                        new Drawable[]
                        {
                            gridScroll = new BasicScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                ScrollbarVisible = true,
                                Child = grid = new FillFlowContainer<ResultPanel>
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Full,
                                    Spacing = new Vector2(Theme.RowSpacing, Theme.RowSpacing),
                                },
                            },
                        },

                        // Next / Restart, the only ways off the screen.
                        new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Margin = new MarginPadding { Top = Theme.SectionSpacing },
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(Theme.RowSpacing, 0),
                                Children = new Drawable[]
                                {
                                    restartButton = new TextButton("Restart")
                                    {
                                        Anchor = Anchor.TopCentre,
                                        Origin = Anchor.TopCentre,
                                        Size = new Vector2(120, 40),
                                        Action = () => RestartRequested?.Invoke(),
                                    },
                                    nextButton = new TextButton("Next")
                                    {
                                        Anchor = Anchor.TopCentre,
                                        Origin = Anchor.TopCentre,
                                        Size = new Vector2(120, 40),
                                        IdleColour = Theme.AccentDim,
                                        HoverColour = Theme.Accent,
                                        Action = () => NextRequested?.Invoke(),
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private SpriteText headerArtist = null!;
    private SpriteText headerMapper = null!;
    private SpriteText headerPlayedBy = null!;
    private TextButton nextButton = null!;
    private TextButton restartButton = null!;

    /// <summary>Test hook: the Next button, so a test can drive it and observe <see cref="NextRequested"/>.</summary>
    internal TextButton NextButton => nextButton;

    /// <summary>Test hook: the Restart button.</summary>
    internal TextButton RestartButton => restartButton;

    /// <summary>
    /// Populates the screen from a finished play and shows it: the header across the top and one
    /// <see cref="ResultPanel"/> per player in the grid (a single player is centred; many tile and
    /// scroll). Replaces whatever was shown before, so it can be reused across queue items. Fades in and
    /// stays up until <see cref="NextRequested"/> / <see cref="RestartRequested"/> is fired.
    /// </summary>
    public void Show(ResultBeatmapHeader header, IReadOnlyList<PlayerResultData> players)
    {
        headerTitle.Text = header.Title;
        headerArtist.Text = header.Artist;
        headerMapper.Text = header.Mapper.Length > 0 ? $"Beatmap by {header.Mapper}" : string.Empty;
        headerPlayedBy.Text = header.PlayedByLine;

        grid.Clear();

        // A single player centres its one panel; a field of them tiles left-to-right and wraps. Centring
        // is only right for the lone case — a centred flow of many panels leaves a ragged last row.
        bool single = players.Count == 1;
        grid.Anchor = single ? Anchor.TopCentre : Anchor.TopLeft;
        grid.Origin = single ? Anchor.TopCentre : Anchor.TopLeft;
        // Clear the auto-size axes before touching the relative ones: a drawable may never have the same
        // axis set as both relative and auto-size, and the two assignments would momentarily overlap
        // (X in both) when widening from the single-player state back to the multi-player one.
        grid.AutoSizeAxes = Axes.None;
        grid.RelativeSizeAxes = single ? Axes.None : Axes.X;
        grid.AutoSizeAxes = single ? Axes.Both : Axes.Y;

        foreach (var player in players)
            grid.Add(new ResultPanel(player));

        gridScroll.ScrollToStart(false);

        this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
    }

    /// <summary>Hides the screen (fades out). The lead calls this after acting on Next/Restart; the
    /// buttons themselves only fire the actions, so the screen stays up until the owner decides.</summary>
    public new void Hide() => this.FadeOut(Theme.DurationFast, Theme.EaseExit);
}
