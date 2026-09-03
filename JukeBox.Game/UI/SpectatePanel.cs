#nullable enable

using System.Collections.Generic;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The spectate controls: who is being watched, what each of them appears to be doing, and the one
/// button that starts and stops it.
///
/// <para>
/// Every row shows TWO facts side by side and never merges them, because they are of different
/// quality and a person reading the wall has to be able to tell which is which. The dot is REAL —
/// osu! says whether the account is online. The words after it are INFERRED from how recently a
/// score of theirs landed, since the public API exposes no live activity at all (the live route is
/// the spectator hub, which is first-party-only). "online · idle" is therefore not a contradiction:
/// it is someone at their computer we simply cannot see into, and saying so is more useful than
/// picking one of the two and pretending it is the whole answer.
/// </para>
/// </summary>
public partial class SpectatePanel : CompositeDrawable
{
    /// <summary>The presence dot's diameter. Small enough to read as punctuation beside the name.</summary>
    private const float dot_size = 8;

    private const float row_height = 30;

    [Resolved]
    private SpectateController spectate { get; set; } = null!;

    private AccentTextBox nameBox = null!;
    private TextButton addButton = null!;
    private TextButton startButton = null!;
    private FillFlowContainer rows = null!;
    private TextFlowContainer hint = null!;

    /// <summary>What the hint currently says. Held because a <see cref="TextFlowContainer"/> keeps
    /// its text as laid-out parts rather than as a string, and the test reads it back.</summary>
    private string hintText = string.Empty;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the pieces, so a
    /// test can drive the panel the way a person does rather than through layout positions.</summary>
    internal AccentTextBox NameBox => nameBox;

    internal TextButton AddButton => addButton;

    internal TextButton StartButton => startButton;

    internal FillFlowContainer Rows => rows;

    internal string Hint => hintText;

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
                new SpriteText
                {
                    Text = "Spectate",
                    Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                    Colour = Theme.TextPrimary,
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
                            addButton = new TextButton("Watch")
                            {
                                Width = 70,
                                Height = 34,
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
                startButton = new TextButton("Start spectating", FontAwesome.Solid.Eye)
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    IdleColour = Theme.AccentDim.Opacity(0.75f),
                    HoverColour = Theme.Accent,
                },
                // A flow rather than a SpriteText: the honest description of what this feature is
                // does not fit the column on one line, and a single line simply runs off the panel
                // edge — which loses precisely the half that says the plays are not live.
                hint = new TextFlowContainer(t =>
                {
                    t.Font = FontUsage.Default.With(size: Theme.CaptionTextSize);
                    t.Colour = Theme.TextTertiary;
                })
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                },
            },
        };

        nameBox.OnCommit += (_, _) => add();
        addButton.Action = add;
        startButton.Action = () => spectate.Active.Value = !spectate.Active.Value;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        spectate.Revision.BindValueChanged(_ => refresh());
        spectate.Active.BindValueChanged(_ => refresh(), true);
    }

    private void add()
    {
        string name = nameBox.Text;

        if (spectate.Add(name))
            nameBox.Text = string.Empty;

        refresh();
    }

    private void refresh()
    {
        var players = spectate.Players;

        rows.Clear();

        foreach (var player in players)
            rows.Add(new PlayerRow(player, () => spectate.Remove(player.Username)));

        bool active = spectate.Active.Value;

        startButton.Text = active ? "Stop spectating" : "Start spectating";
        startButton.IdleColour = active ? Theme.Error.Opacity(0.6f) : Theme.AccentDim.Opacity(0.75f);
        startButton.HoverColour = active ? Theme.Error : Theme.Accent;

        hint.Text = hintText = hintFor(players, active);
    }

    /// <summary>
    /// The caption under the button. It answers the question the panel's state raises, in order of
    /// what is actually blocking the user — an empty list before anything else, then the honest
    /// description of what "spectating" here means, and while it runs, how many of the watched can
    /// be on screen at once.
    /// </summary>
    private static string hintFor(IReadOnlyList<WatchedPlayer> players, bool active)
    {
        if (players.Count == 0)
            return "Add up to " + SpectateWatchList.MAX_WATCHED + " osu! players to watch.";

        if (!active)
            return "Shows each player's most recent completed play, replayed here — osu! exposes no live feed.";

        int rendered = 0;

        foreach (var player in players)
        {
            if (player.Entry != null && SpectateStateRules.ShouldRender(player.Activity))
                rendered++;
        }

        if (rendered > Replays.SpectatePanePlan.MAX_PANES)
            rendered = Replays.SpectatePanePlan.MAX_PANES;

        return rendered == 0
            ? "Watching for new plays…"
            : $"Showing {rendered} of {players.Count} — newest plays first.";
    }

    /// <summary>One watched player: the real presence dot, the name, the inferred status, and the
    /// button that stops watching them.</summary>
    private partial class PlayerRow : CompositeDrawable
    {
        private readonly WatchedPlayer player;
        private readonly System.Action remove;

        /// <summary>Test-only access: what this row says about the player.</summary>
        internal string StatusText { get; private set; } = string.Empty;

        public PlayerRow(WatchedPlayer player, System.Action remove)
        {
            this.player = player;
            this.remove = remove;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = row_height;

            StatusText = player.Status;

            InternalChild = new GridContainer
            {
                RelativeSizeAxes = Axes.Both,
                ColumnDimensions = new[]
                {
                    new Dimension(GridSizeMode.Absolute, 16),
                    new Dimension(GridSizeMode.AutoSize),
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, row_height),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        new Circle
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new Vector2(dot_size),
                            // Green means osu! itself reports the account online. Grey is both
                            // "offline" and "not looked up yet", which are the same thing to a
                            // reader: we have no evidence they are there.
                            Colour = player.Presence.IsOnline ? Theme.StatusRanked : Theme.TextTertiary,
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = player.Username,
                            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                            Colour = Theme.TextPrimary,
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Padding = new MarginPadding { Right = Theme.RowSpacing },
                            Text = player.Status,
                            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                            Colour = colourFor(player),
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

        /// <summary>
        /// The status line's colour: red only for the two states that mean something went wrong for
        /// US (an unresolvable name, or a note explaining why there is nothing to show). A FAILED
        /// play is not an error — it is the player's own result, and colouring it like a fault would
        /// misreport what happened.
        /// </summary>
        private static Color4 colourFor(WatchedPlayer player)
        {
            if (player.Activity == SpectateState.Unknown_User)
                return Theme.Error;

            if (player.Note != null)
                return Theme.TextTertiary;

            return SpectateStateRules.ShouldRender(player.Activity) ? Theme.TextSecondary : Theme.TextTertiary;
        }
    }
}
