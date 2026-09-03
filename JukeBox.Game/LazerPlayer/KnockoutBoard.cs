#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Skinning;
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
    /// <param name="Mods">Their mods, already formatted ("+HDDT"), drawn in white beside the
    /// coloured name rather than folded into it — the reference colours the two separately.</param>
    public readonly record struct Entrant(string Name, Color4 Colour, ReplayTimeline Timeline, string Mods = "");

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

    [Resolved(canBeNull: true)]
    private ISkinSource? skin { get; set; }

    /// <summary>The grade each row is currently showing a texture for, so the skin is only asked
    /// again when the answer would change.</summary>
    private readonly Dictionary<int, string> gradeTextures = new Dictionary<int, string>();

    /// <summary>
    /// Points each row's grade at the skin's own "ranking-&lt;grade&gt;-small" graphic.
    ///
    /// <para>
    /// Re-checked whenever a row's grade changes rather than every frame: a texture lookup walks
    /// the whole skin chain, and a board of forty-seven rows doing that per frame is a great deal
    /// of work to arrive at the same answer.
    /// </para>
    /// </summary>
    private void applyGradeTextures()
    {
        foreach (var row in rows)
        {
            string wanted = row.CurrentGrade;

            if (gradeTextures.TryGetValue(row.PlayerIndex, out string? shown) && shown == wanted)
                continue;

            gradeTextures[row.PlayerIndex] = wanted;

            var texture = wanted.Length == 0 ? null : skin?.GetTexture($"ranking-{wanted}-small");

            row.ApplyGradeTexture(texture, Metrics.RowHeight * 0.8f);
        }
    }

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

        // After the rows have read their grades, so a grade that improves mid-song swaps its
        // graphic. Cheap: it only asks the skin when a row's grade has actually changed.
        applyGradeTextures();
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

        // A resize changes the size every grade graphic should be drawn at, so they are all
        // re-fetched rather than left at the previous scale.
        gradeTextures.Clear();

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
        private readonly OsuSpriteText mods = null!;
        private readonly Sprite gradeImage = null!;
        private readonly FillFlowContainer left = null!;
        private readonly FillFlowContainer right = null!;

        /// <summary>Test hook: whether the grade is showing the skin's graphic rather than a letter.</summary>
        internal bool GradeIsImage => gradeImage.Alpha > 0;

        /// <summary>Test hooks for the row's drawn appearance, which is what the reference pins.</summary>
        internal string ModsText => mods.Text.ToString()!;

        internal Color4 ModsColour => mods.Colour;

        internal string NameText => playerName.Text.ToString()!;

        internal Color4 NameColour => playerName.Colour;

        internal Color4 BackgroundColour => background.Colour;

        internal string PerformanceText => performance.Text.ToString()!;

        /// <summary>
        /// Swaps the letter for the skin's own ranking graphic, when the skin has one.
        ///
        /// <para>
        /// The reference asks its skin for "ranking-&lt;grade&gt;-small" and draws that, so a skin
        /// that restyles its grades restyles the board too. The letter stays as the fallback: a skin
        /// without those textures should still say what the grade is rather than show a gap.
        /// </para>
        /// </summary>
        public void ApplyGradeTexture(Texture? texture, float height)
        {
            if (texture == null)
            {
                gradeImage.Alpha = 0;
                grade.Alpha = 1;
                return;
            }

            gradeImage.Texture = texture;
            gradeImage.Size = new Vector2(texture.DisplayWidth / texture.DisplayHeight * height, height);
            gradeImage.Alpha = 1;
            grade.Alpha = 0;
        }

        /// <summary>The alpha this row rests at — dimmed once its player is out.</summary>
        public float RestingAlpha { get; private set; } = 1;

        /// <summary>The RAW rank this row is showing, which is what the skin names its graphic
        /// after — "X" for a perfect play, not "SS".</summary>
        public string CurrentGrade { get; private set; } = string.Empty;

        /// <summary>The rank as a player names it, for the text fallback.</summary>
        private static string displayGrade(string rank) => rank switch
        {
            "X" or "XH" => "SS",
            "SH" => "S",
            _ => rank,
        };

        private double rollingScore;
        private double rollingAccuracy = 1;
        private double rollingPerformance;

        /// <summary>
        /// Eases a displayed number toward its target. Framerate-independent, so the roll takes the
        /// same wall time on a fast machine as a slow one rather than being however many frames
        /// happened to elapse.
        /// </summary>
        private double roll(double current, double target)
        {
            double delta = target - current;

            // Close enough to have arrived: without this the value creeps forever and the text
            // re-renders every frame for a difference nobody can see.
            if (Math.Abs(delta) < 0.01)
                return target;

            // The CLOCK's frame time, not the drawable's Time.Elapsed. A row is updated by its
            // parent's Update, which runs before the row's own update for the frame, so Time.Elapsed
            // reads zero there — and a rate of zero means the number never moves at all. That is not
            // a subtle wrongness: every rolled figure sat at its starting value forever while the
            // un-rolled ones beside it were correct.
            double elapsed = Math.Max(Clock.ElapsedFrameTime, 0);

            if (elapsed <= 0)
                return target;

            double rate = 1 - Math.Pow(0.0001, elapsed / 1000);

            return current + delta * Math.Clamp(rate, 0, 1);
        }

        public Row(int playerIndex, Entrant entrant)
        {
            PlayerIndex = playerIndex;
            playerColour = entrant.Colour;

            RelativeSizeAxes = Axes.X;

            InternalChildren = new Drawable[]
            {
                // NO row background. The reference draws rows straight over the playfield — its
                // DrawBackground paints only the playfield boundary, never a strip per row — and a
                // dark bar behind each line is the difference between an overlay and a panel.
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Transparent,
                },

                // Who and how well, reading left to right the way the reference lays it out:
                // accuracy, pp, the grade IMAGE, then the name with its mods.
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
                        // playfield, and the reference render carries one at the head of each row.
                        dot = new Circle
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Colour = entrant.Colour,
                        },
                        accuracy = text("100.00%", FontWeight.SemiBold),
                        performance = text("0.00pp", FontWeight.Regular),

                        // The grade as the SKIN's own ranking graphic, with the letter behind it for
                        // when the skin has no such texture — see gradeSprite.
                        gradeImage = new Sprite
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Alpha = 0,
                        },
                        grade = text("S", FontWeight.Bold),

                        // Name in the PLAYER's colour, mods in white beside it. Two sprites rather
                        // than one string because they are coloured differently, which is exactly
                        // what the reference does: it sets the player colour, draws the name, resets
                        // to white and draws the mods at 0.8 scale.
                        playerName = text(entrant.Name, FontWeight.SemiBold, entrant.Colour),
                        mods = text(entrant.Mods, FontWeight.SemiBold),
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

            // Mods a little smaller than the name, as the reference draws them (0.8 of the row's
            // text scale) — they qualify the name rather than competing with it.
            mods.Font = mods.Font.With(size: metrics.FontSize * 0.8f);

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
            // The numbers ROLL toward their new value rather than snapping to it, which is what the
            // reference does — its score, pp and accuracy are all target-gliders. Snapping reads as
            // a table refreshing; rolling reads as a score climbing.
            rollingScore = pending ? point.Score : roll(rollingScore, point.Score);
            rollingAccuracy = pending ? point.Accuracy : roll(rollingAccuracy, point.Accuracy);
            rollingPerformance = pending ? point.Performance : roll(rollingPerformance, point.Performance);

            score.Text = pending ? "--------" : ((long)Math.Round(rollingScore)).ToString("00000000");
            accuracy.Text = pending ? "--.--%" : (rollingAccuracy * 100).ToString("0.00") + "%";
            combo.Text = pending ? "--x" : point.Combo.ToString("N0") + "x";

            // Two decimals, as the reference shows it — "310.00pp", not "310pp".
            performance.Text = pending ? "--.--pp" : rollingPerformance.ToString("0.00") + "pp";

            // The letter a PLAYER would recognise, while CurrentGrade keeps the raw rank the skin
            // names its graphics after. lazer calls a perfect play X, which on screen reads as a
            // cross rather than as the best grade there is.
            grade.Text = pending ? "-" : displayGrade(point.Grade);
            CurrentGrade = pending ? string.Empty : point.Grade;

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
