#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The scoreboard as the event rather than as a caption: one row per player, re-ordering itself as
/// the plays diverge, with the eliminated sinking to the bottom rather than disappearing.
///
/// <para>
/// Every number on it is read from that player's <see cref="ReplayTimeline"/> at the current
/// playback time, which is what lets it be correct after a seek. Nothing here accumulates.
/// </para>
///
/// <para>
/// The rows are positioned by hand instead of flowed, because a flow container re-lays-out
/// instantly and the whole point is to SEE someone overtake. An absolute Y per rank with a
/// transition between them is what makes a swap legible.
/// </para>
/// </summary>
public partial class KnockoutBoard : CompositeDrawable
{
    /// <summary>One player as the board knows them.</summary>
    /// <param name="Name">Displayed name, mods included.</param>
    /// <param name="Colour">Their colour, shared with their cursor.</param>
    /// <param name="Timeline">Their recorded play.</param>
    public readonly record struct Entrant(string Name, Color4 Colour, ReplayTimeline Timeline);

    private readonly IReadOnlyList<Entrant> entrants;
    private readonly List<Row> rows = new List<Row>();

    /// <summary>The rules in force. Assign to change mode or sorting; the board follows.</summary>
    public KnockoutRules Rules { get; set; } = new KnockoutRules();

    /// <summary>Test hook: the rows, in creation order — NOT in board order.</summary>
    internal IReadOnlyList<Row> Rows => rows;

    /// <summary>
    /// Flashes that player's NAME red for about a second, the way a tournament overlay marks a
    /// dropped combo.
    ///
    /// <para>
    /// Distinct from being knocked out, and deliberately so: this fires on EVERY break, including
    /// with elimination switched off, and the player carries on afterwards. Elimination is the
    /// permanent state — dimmed and sunk to the bottom; this is the moment it happened.
    /// </para>
    /// </summary>
    public void FlashComboBreak(int playerIndex)
    {
        if (playerIndex >= 0 && playerIndex < rows.Count)
            rows[playerIndex].FlashComboBreak();
    }

    /// <summary>
    /// Test hook: player indices top to bottom as the board has ranked them.
    ///
    /// <para>
    /// Read from where each row is HEADING rather than where it currently is. The two differ for
    /// the length of the re-order animation, and a test asserting on live positions would be
    /// asserting on how far an easing curve had got — which is both racy and not the thing anyone
    /// cares about.
    /// </para>
    /// </summary>
    internal IReadOnlyList<int> DisplayOrder => rows.OrderBy(r => r.TargetY).Select(r => r.PlayerIndex).ToList();

    private const float row_height = 22;
    private const double reorder_ms = 400;

    /// <summary>
    /// Whether this player's numbers at <paramref name="time"/> are still being worked out.
    ///
    /// <para>
    /// The simulation runs tens of times faster than playback and finishes a three-minute map in a
    /// few seconds, so this is only ever true in the opening moments — but in those moments a user
    /// who drags the seek bar to the end is asking about a play that has not been recorded yet, and
    /// the timeline would answer with the last thing it knew. That answer LOOKS like a score. Saying
    /// "not yet" is the difference between a blank and a wrong number.
    /// </para>
    /// </summary>
    internal static bool IsPending(ReplayTimeline timeline, double time)
        => !timeline.Complete && time > timeline.SimulatedTo;

    /// <summary>
    /// The metrics currently in force, recomputed whenever the board's height or the field size
    /// changes. Test hook as well as state: the density rules are what stop 47 players running off
    /// the bottom of the screen.
    /// </summary>
    internal RailMetrics Metrics { get; private set; } = RailDensity.For(0, 0);

    /// <summary>Test hook: rows currently drawn, which is fewer than the field only when even the
    /// smallest row will not fit for everyone.</summary>
    internal int VisibleRowCount => Metrics.VisibleRows;

    public KnockoutBoard(IReadOnlyList<Entrant> entrants)
    {
        this.entrants = entrants;

        // Sized to its CONTAINER, not to its content. Sizing to content is what produced a board
        // over a thousand pixels tall for 47 players, running off the bottom of the player box and
        // over the app behind it. Masking makes that structural rather than a matter of arithmetic
        // being right: nothing can be drawn outside the board's own bounds even if it tried.
        RelativeSizeAxes = Axes.Y;
        Masking = true;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        for (int i = 0; i < entrants.Count; i++)
        {
            var row = new Row(i, entrants[i]);

            rows.Add(row);
            AddInternal(row);
        }

        AddInternal(overflowNote = new OsuSpriteText
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.BottomLeft,
            Colour = Color4.White.Opacity(0.6f),
            Alpha = 0,
            Margin = new MarginPadding { Left = 6 },
        });
    }

    private OsuSpriteText overflowNote = null!;

    protected override void Update()
    {
        base.Update();

        if (rows.Count == 0)
            return;

        applyDensity();

        double time = Clock.CurrentTime;
        var timelines = entrants.Select(e => e.Timeline).ToList();
        var standings = Rules.Standings(timelines, time);

        // Computed once for the frame: who is out depends on the whole field, not on one play.
        var eliminated = Rules.Eliminated(timelines, time);

        for (int rank = 0; rank < standings.Count; rank++)
        {
            var row = rows[standings[rank]];

            // Past what fits, a row is not drawn at all rather than drawn off the bottom. The
            // players it stands for are reported in the overflow line instead of vanishing.
            bool visible = rank < Metrics.VisibleRows;

            row.Alpha = visible ? row.RestingAlpha : 0;

            if (!visible)
                continue;

            float target = rank * Metrics.RowHeight;

            // Only animate an actual change. Re-issuing the same transform every frame restarts it
            // every frame, which leaves rows permanently easing towards a place they already are.
            if (Math.Abs(row.TargetY - target) > 0.01f)
            {
                row.TargetY = target;
                row.MoveToY(target, reorder_ms, Easing.OutQuint);
            }

            row.UpdateFrom(entrants[standings[rank]].Timeline, Rules, time, rank + 1, !eliminated.Contains(standings[rank]));
        }
    }

    /// <summary>
    /// Re-sizes the board and its rows for the space actually available. Recomputed every frame but
    /// only APPLIED on a change, because setting a font size rebuilds the text.
    /// </summary>
    private void applyDensity()
    {
        var metrics = RailDensity.For(entrants.Count, DrawHeight);

        if (metrics.Equals(Metrics))
            return;

        Metrics = metrics;
        Width = metrics.Width;

        foreach (var row in rows)
            row.Apply(metrics);

        int hidden = entrants.Count - metrics.VisibleRows;

        overflowNote.Alpha = hidden > 0 ? 1 : 0;
        overflowNote.Text = hidden > 0 ? $"+{hidden} more" : string.Empty;
        overflowNote.Font = OsuFont.Torus.With(size: metrics.FontSize, weight: FontWeight.SemiBold);
    }

    /// <summary>One player's line on the board.</summary>
    internal partial class Row : CompositeDrawable
    {
        public readonly int PlayerIndex;

        /// <summary>Where this row is heading, so a transform is only started when it changes.</summary>
        public float TargetY;

        private readonly OsuSpriteText rank = null!;
        private readonly OsuSpriteText score = null!;
        private readonly OsuSpriteText accuracy = null!;
        private readonly OsuSpriteText combo = null!;
        private readonly OsuSpriteText playerName = null!;
        private readonly Box background = null!;
        private readonly Color4 playerColour;

        /// <summary>Test hook: how many times this row has been flashed for a combo break.</summary>
        internal int ComboBreakFlashes { get; private set; }

        /// <summary>Flashes the name red and swells it, then settles back to the player's colour.</summary>
        public void FlashComboBreak()
        {
            ComboBreakFlashes++;

            playerName.FadeColour(Color4.Red).Then().FadeColour(playerColour, 900, Easing.In);
            playerName.ScaleTo(1.4f).Then().ScaleTo(1, 900, Easing.OutQuint);

            // The blink is what catches the eye; a colour fade on its own is easy to miss on a board
            // that is already re-ordering.
            playerName.FadeTo(0.2f, 80).Then().FadeTo(1, 80)
                      .Then().FadeTo(0.2f, 80).Then().FadeTo(1, 80);
        }

        /// <summary>Test hook: whether this row is currently drawn as still in the running.</summary>
        internal bool ShownAlive { get; private set; } = true;

        /// <summary>Test hook: whether this row is showing dashes because the play has not been
        /// simulated this far yet.</summary>
        internal bool ShownPending { get; private set; }

        /// <summary>Test hook: what the row currently reads.</summary>
        internal string ScoreText => score.Text.ToString()!;

        internal string ComboText => combo.Text.ToString()!;

        internal string AccuracyText => accuracy.Text.ToString()!;

        private readonly Circle dot = null!;
        private readonly OsuSpriteText performance = null!;
        private readonly OsuSpriteText grade = null!;
        private readonly FillFlowContainer left = null!;
        private readonly FillFlowContainer right = null!;

        /// <summary>The alpha this row rests at — dimmed once its player is out.</summary>
        public float RestingAlpha { get; private set; } = 1;

        public Row(int playerIndex, Entrant entrant)
        {
            PlayerIndex = playerIndex;
            playerColour = entrant.Colour;

            RelativeSizeAxes = Axes.X;

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Black.Opacity(0.45f),
                },

                // Who and how well, reading left to right the way the reference lays it out.
                left = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(5, 0),
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Margin = new MarginPadding { Left = 5 },
                    Children = new Drawable[]
                    {
                        // The dot is the only thing tying this row to a cursor weaving about the
                        // playfield, which is why it is the same colour and never re-used.
                        dot = new Circle
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Colour = entrant.Colour,
                        },
                        accuracy = text("100.00%", FontWeight.SemiBold),
                        performance = text("0pp", FontWeight.Regular),
                        grade = text("S", FontWeight.Bold),
                        playerName = text(entrant.Name, FontWeight.SemiBold, entrant.Colour),
                    },
                },

                // What they have, pinned to the right edge so the numbers line up down the board
                // however long the names are.
                right = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(6, 0),
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 5 },
                    Children = new Drawable[]
                    {
                        combo = text("0x", FontWeight.Regular),
                        score = text("00000000", FontWeight.Bold),
                    },
                },
            };
        }

        /// <summary>Re-sizes this row for the current density.</summary>
        public void Apply(RailMetrics metrics)
        {
            Height = metrics.RowHeight;
            dot.Size = new Vector2(metrics.DotSize);
            performance.Alpha = metrics.ShowPerformance ? 1 : 0;

            foreach (var sprite in new[] { accuracy, performance, grade, playerName, combo, score })
                sprite.Font = sprite.Font.With(size: metrics.FontSize);

            // Spacing follows the text, or a dense board turns into one run-on line.
            left.Spacing = new Vector2(metrics.FontSize * 0.42f, 0);
            right.Spacing = new Vector2(metrics.FontSize * 0.5f, 0);
        }

        /// <summary>Reads this player's state at <paramref name="time"/> onto the row.</summary>
        /// <param name="alive">Whether they are still in it — decided by the caller, because the
        /// survivor floor depends on the whole field rather than on this one play.</param>
        public void UpdateFrom(ReplayTimeline timeline, KnockoutRules rules, double time, int place, bool alive)
        {
            var point = timeline.At(time);
            bool pending = IsPending(timeline, time);

            // Dashes rather than the last known figures. Beyond what has been simulated the
            // timeline's answer is simply the most recent thing it recorded, which reads as a real
            // score for a moment the player has not reached — a wrong number, not a missing one.
            score.Text = pending ? "--------" : point.Score.ToString("00000000");
            accuracy.Text = pending ? "--.--%" : (point.Accuracy * 100).ToString("0.00") + "%";
            combo.Text = pending ? "--x" : point.Combo.ToString("N0") + "x";
            performance.Text = pending ? "--pp" : point.Performance.ToString("0") + "pp";
            grade.Text = pending ? "-" : point.Grade;

            ShownPending = pending;

            if (alive == ShownAlive)
                return;

            ShownAlive = alive;
            RestingAlpha = alive ? 1 : 0.45f;

            // Knocked out reads as dimmed and desaturated rather than removed: a player who
            // vanishes has not been seen to lose.
            this.FadeTo(RestingAlpha, 300, Easing.OutQuint);
            background.FadeColour(alive ? Color4.Black.Opacity(0.45f) : Color4.DarkRed.Opacity(0.5f), 300, Easing.OutQuint);
        }

        private static OsuSpriteText text(string content, FontWeight weight, Color4? colour = null) => new OsuSpriteText
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Text = content,
            Font = OsuFont.Torus.With(size: RailDensity.MAX_ROW_HEIGHT * 0.55f, weight: weight),
            Colour = colour ?? Color4.White,
            Shadow = true,
        };
    }
}
