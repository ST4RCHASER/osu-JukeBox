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

    public KnockoutBoard(IReadOnlyList<Entrant> entrants)
    {
        this.entrants = entrants;

        AutoSizeAxes = Axes.X;
        Height = row_height * entrants.Count;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        for (int i = 0; i < entrants.Count; i++)
        {
            var row = new Row(i, entrants[i]) { Y = i * row_height };

            rows.Add(row);
            AddInternal(row);
        }
    }

    protected override void Update()
    {
        base.Update();

        if (rows.Count == 0)
            return;

        double time = Clock.CurrentTime;
        var timelines = entrants.Select(e => e.Timeline).ToList();
        var standings = Rules.Standings(timelines, time);

        for (int rank = 0; rank < standings.Count; rank++)
        {
            var row = rows[standings[rank]];
            float target = rank * row_height;

            // Only animate an actual change. Re-issuing the same transform every frame restarts it
            // every frame, which leaves rows permanently easing towards a place they already are.
            if (Math.Abs(row.TargetY - target) > 0.01f)
            {
                row.TargetY = target;
                row.MoveToY(target, reorder_ms, Easing.OutQuint);
            }

            row.UpdateFrom(entrants[standings[rank]].Timeline, Rules, time, rank + 1);
        }
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
        private readonly Box background = null!;

        /// <summary>Test hook: whether this row is currently drawn as still in the running.</summary>
        internal bool ShownAlive { get; private set; } = true;

        /// <summary>Test hook: whether this row is showing dashes because the play has not been
        /// simulated this far yet.</summary>
        internal bool ShownPending { get; private set; }

        /// <summary>Test hook: what the row currently reads.</summary>
        internal string ScoreText => score.Text.ToString()!;

        internal string ComboText => combo.Text.ToString()!;

        internal string AccuracyText => accuracy.Text.ToString()!;

        public Row(int playerIndex, Entrant entrant)
        {
            PlayerIndex = playerIndex;
            TargetY = playerIndex * row_height;

            AutoSizeAxes = Axes.X;
            Height = row_height;

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Width = 1,
                    Colour = Color4.Black.Opacity(0.45f),
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(6, 0),
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Margin = new MarginPadding { Horizontal = 6 },
                    Children = new Drawable[]
                    {
                        rank = text("1.", 12, FontWeight.Bold),

                        // The dot is the only thing tying this row to a cursor weaving about the
                        // playfield, which is why it is the same colour and never re-used.
                        new Circle
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Size = new Vector2(8),
                            Colour = entrant.Colour,
                        },
                        text(entrant.Name, 12, FontWeight.SemiBold, entrant.Colour),
                        score = text("00000000", 12, FontWeight.Bold),
                        accuracy = text("100.00%", 12, FontWeight.Regular),
                        combo = text("0x", 12, FontWeight.Regular),
                    },
                },
            };
        }

        /// <summary>Reads this player's state at <paramref name="time"/> onto the row.</summary>
        public void UpdateFrom(ReplayTimeline timeline, KnockoutRules rules, double time, int place)
        {
            var point = timeline.At(time);
            bool pending = IsPending(timeline, time);

            rank.Text = $"{place}.";

            // Dashes rather than the last known figures. Beyond what has been simulated the
            // timeline's answer is simply the most recent thing it recorded, which reads as a real
            // score for a moment the player has not reached — a wrong number, not a missing one.
            score.Text = pending ? "--------" : point.Score.ToString("00000000");
            accuracy.Text = pending ? "--.--%" : (point.Accuracy * 100).ToString("0.00") + "%";
            combo.Text = pending ? "--x" : point.Combo.ToString("N0") + "x";

            ShownPending = pending;

            bool alive = rules.AliveAt(timeline, time);

            if (alive == ShownAlive)
                return;

            ShownAlive = alive;

            // Knocked out reads as dimmed and desaturated rather than removed: a player who
            // vanishes has not been seen to lose.
            this.FadeTo(alive ? 1 : 0.45f, 300, Easing.OutQuint);
            background.FadeColour(alive ? Color4.Black.Opacity(0.45f) : Color4.DarkRed.Opacity(0.5f), 300, Easing.OutQuint);
        }

        private static OsuSpriteText text(string content, float size, FontWeight weight, Color4? colour = null) => new OsuSpriteText
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Text = content,
            Font = OsuFont.Torus.With(size: size, weight: weight),
            Colour = colour ?? Color4.White,
            Shadow = true,
        };
    }
}
