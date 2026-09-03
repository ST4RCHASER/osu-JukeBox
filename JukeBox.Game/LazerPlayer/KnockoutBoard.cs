#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.Leaderboards;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
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

    /// <summary>The longest name in the field, in characters. Every row's name column is sized to it
    /// so the columns after the name line up down the board instead of stepping in and out with each
    /// player's name length.</summary>
    private readonly int maxNameChars;

    /// <summary>The rules in force. Assign to change mode or sorting; the board follows.</summary>
    public KnockoutRules Rules { get; set; } = new KnockoutRules();

    /// <summary>
    /// Raised with a player's index while their row is hovered, and with null when it is not. The
    /// combine layer listens so it can bring THAT player's cursor forward and step every other one
    /// back — the rail row is the handle, the effect lands on the playfield. Left unset (as it is in
    /// the grid) the rows simply do not answer hover with anything.
    /// </summary>
    public Action<int?>? PlayerFocused { get; set; }

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

    /// <summary>Re-tints one player's name to a new cursor colour, live — the rail name shares the
    /// player's cursor colour, so a colour override has to reach here too. Rows are held in player
    /// order, so the index is the player's.</summary>
    public void SetPlayerColour(int playerIndex, Color4 colour)
    {
        if (playerIndex >= 0 && playerIndex < rows.Count)
            rows[playerIndex].SetColour(colour);
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
        maxNameChars = entrants.Count == 0 ? 4 : entrants.Max(e => e.Name.Length);

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
            var row = new Row(i, entrants[i], maxNameChars)
            {
                // Hover is reported up through the board so a single listener on the combine layer
                // can act on it, rather than every row holding a reference to the cursor overlay.
                FocusRequested = index => PlayerFocused?.Invoke(index),
            };

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

        // Sort ONLY once every player has been simulated up to the playhead. While the recording is
        // still catching up, players get their numbers one at a time (laggard-first), so a live sort
        // would rank half a field of real scores against half a field of zeros and churn every frame
        // — which is what drew rows on top of each other on a fresh load. Until then hold a stable
        // order (the last good sort, or the drop order before any) and SNAP rather than animate, so
        // nothing is ever mid-flight between two colliding Y targets.
        bool ready = !timelines.Any(t => IsPending(t, time));

        IReadOnlyList<int> order;
        bool snap;

        if (ready)
        {
            order = heldOrder = Rules.Standings(timelines, time);
            snap = false;
        }
        else if (heldOrder != null)
        {
            order = heldOrder;
            snap = false;
        }
        else
        {
            order = Enumerable.Range(0, timelines.Count).ToList();
            snap = true;
        }

        // Computed once for the frame: who is out depends on the whole field, not on one play.
        var eliminated = Rules.Eliminated(timelines, time);

        for (int rank = 0; rank < order.Count; rank++)
        {
            var row = rows[order[rank]];

            // Past what fits, a row is not drawn at all rather than drawn off the bottom. The
            // players it stands for are reported in the overflow line instead of vanishing.
            bool visible = rank < Metrics.VisibleRows;

            row.Alpha = visible ? row.RestingAlpha : 0;

            if (!visible)
                continue;

            float target = rank * Metrics.RowHeight;

            if (snap)
            {
                // Placed directly, no transform, while the order is still being held stable.
                row.TargetY = target;
                row.Y = target;
            }
            else if (Math.Abs(row.TargetY - target) > 0.01f)
            {
                // Only animate an actual change. Re-issuing the same transform every frame restarts it
                // every frame, which leaves rows permanently easing towards a place they already are.
                row.TargetY = target;
                row.MoveToY(target, reorder_ms, Easing.OutQuint);
            }

            row.UpdateFrom(entrants[order[rank]].Timeline, Rules, time, rank + 1, !eliminated.Contains(order[rank]));
        }
    }

    /// <summary>The last order the board settled on while every player's numbers were known, held
    /// through any moment the recording falls behind so the board never churns back to drop order
    /// mid-song.</summary>
    private IReadOnlyList<int>? heldOrder;

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

        /// <summary>Raised with this row's player index on hover and with null when the pointer
        /// leaves, so the combine layer can focus that player's cursor. The board sets it.</summary>
        internal Action<int?>? FocusRequested;

        // The whole row is the hover target, not just the pixels its text happens to cover — a
        // composite otherwise only answers hover where a child does, and the gaps between the name
        // and the numbers would read as "not hovering anyone". The transparent background box gives
        // it a full-width surface; this makes the empty space part of it too.
        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => DrawRectangle.Contains(ToLocalSpace(screenSpacePos));

        protected override bool OnHover(HoverEvent e)
        {
            FocusRequested?.Invoke(PlayerIndex);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e) => FocusRequested?.Invoke(null);

        private readonly int maxNameChars;

        private readonly Box background = null!;

        // Numeric columns, in a FIXED-WIDTH figure font and RIGHT-aligned inside fixed-width cells,
        // so a rolling value changes the digits in place without the text reflowing and without the
        // columns stepping in and out down the board.
        private readonly OsuSpriteText accuracy = null!;
        private readonly OsuSpriteText performance = null!;
        private readonly OsuSpriteText combo = null!;
        private readonly OsuSpriteText score = null!;
        private readonly OsuSpriteText judgement = null!;

        private readonly OsuSpriteText playerName = null!;
        private readonly OsuSpriteText mods = null!;

        // The fixed-width cells the drawables live in. Their widths and X positions are set per
        // density in Apply, which is what makes the row a table rather than a run-on line. The name
        // block flows (name, mods, then the hit badge) so the badge sits right after the mods the way
        // danser draws it, rather than in a fixed column.
        private readonly Container accCell = null!;
        private readonly Container ppCell = null!;
        private readonly Container gradeCell = null!;
        private readonly FillFlowContainer nameCell = null!;
        private readonly Container comboCell = null!;
        private readonly Container scoreCell = null!;

        private Color4 playerColour;

        /// <summary>The grade currently drawn, so the rank graphic is only rebuilt when it changes.</summary>
        private string currentGrade = string.Empty;
        private DrawableRank? rankGraphic;

        /// <summary>Test hook: how many times this row has been flashed for a combo break. The
        /// combine board no longer flashes (it shows a judgement column instead); kept for the grid's
        /// own use and for the tests that still cover the flash directly.</summary>
        internal int ComboBreakFlashes { get; private set; }

        /// <summary>Re-tints this row's name to a new player colour, live. The flash restores to
        /// this same field, so a break mid-change settles to the new colour rather than the old.</summary>
        public void SetColour(Color4 colour)
        {
            playerColour = colour;
            playerName.Colour = colour;
        }

        /// <summary>Flashes the name red and swells it, then settles back to the player's colour.</summary>
        public void FlashComboBreak()
        {
            ComboBreakFlashes++;

            playerName.FadeColour(Color4.Red).Then().FadeColour(playerColour, 900, Easing.In);
            playerName.ScaleTo(1.4f).Then().ScaleTo(1, 900, Easing.OutQuint);

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

        /// <summary>Test hook: the recent-judgement column (X / 50 / 100), empty when there is
        /// nothing recent worth showing.</summary>
        internal string JudgementText => judgement.Text.ToString()!;

        /// <summary>Test hook: the score column's right edge on screen. Fixed as the value rolls
        /// (right-aligned in a fixed-width cell), which is what stops the digits shaking.</summary>
        internal float ScoreRightEdge => score.ScreenSpaceDrawQuad.TopRight.X;

        /// <summary>Test hook: whether the numeric columns use a tabular (fixed-width) figure font,
        /// so a changing digit keeps the same advance and nothing reflows.</summary>
        internal bool NumbersAreFixedWidth
            => accuracy.Font.FixedWidth && performance.Font.FixedWidth && combo.Font.FixedWidth && score.Font.FixedWidth;

        /// <summary>Test hook: whether the grade is drawn as a rank GRAPHIC (danser/leaderboard
        /// badge) rather than a bare letter. Always true once a grade exists — the badge renders for
        /// every skin, which is the fix for the letter showing through Argon.</summary>
        internal bool GradeIsImage => rankGraphic != null;

        internal string ModsText => mods.Text.ToString()!;

        internal Color4 ModsColour => mods.Colour;

        internal string NameText => playerName.Text.ToString()!;

        internal Color4 NameColour => playerName.Colour;

        internal Color4 BackgroundColour => background.Colour;

        internal string PerformanceText => performance.Text.ToString()!;

        /// <summary>The alpha this row rests at — dimmed once its player is out.</summary>
        public float RestingAlpha { get; private set; } = 1;

        /// <summary>The RAW rank this row is showing ("X" for a perfect play).</summary>
        public string CurrentGrade => currentGrade;

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

            if (Math.Abs(delta) < 0.01)
                return target;

            // The CLOCK's frame time, not the drawable's Time.Elapsed — a row is updated by its
            // parent before its own update runs, where Time.Elapsed reads zero.
            double elapsed = Math.Max(Clock.ElapsedFrameTime, 0);

            if (elapsed <= 0)
                return target;

            double rate = 1 - Math.Pow(0.0001, elapsed / 1000);

            return current + delta * Math.Clamp(rate, 0, 1);
        }

        public Row(int playerIndex, Entrant entrant, int maxNameChars)
        {
            PlayerIndex = playerIndex;
            playerColour = entrant.Colour;
            this.maxNameChars = maxNameChars;

            RelativeSizeAxes = Axes.X;

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Transparent,
                },

                // Left group — fixed columns anchored to the left, so accuracy, pp, the grade badge
                // and the name all start at the same x on every row rather than the pp width shoving
                // the ones after it around.
                accCell = numberCell(out accuracy, "100.00%", FontWeight.SemiBold, Anchor.CentreLeft),
                ppCell = numberCell(out performance, "0.00pp", FontWeight.Regular, Anchor.CentreLeft),

                gradeCell = new Container { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, RelativeSizeAxes = Axes.Y },

                // Name, mods and the recent-hit badge FLOW left to right from a fixed start x — the
                // badge lands right after the mods (danser draws "100"/"50" there), and the whole
                // block is free to run toward the numbers because the right group is right-pinned and
                // cannot be pushed out of line by a long name.
                nameCell = new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.X,
                    RelativeSizeAxes = Axes.Y,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(4, 0),
                    Children = new Drawable[]
                    {
                        playerName = new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = entrant.Name,
                            Font = OsuFont.Torus.With(weight: FontWeight.SemiBold),
                            Colour = entrant.Colour,
                            Shadow = true,
                        },
                        mods = new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = entrant.Mods,
                            Font = OsuFont.Torus.With(weight: FontWeight.SemiBold),
                            Colour = Color4.White,
                            Shadow = true,
                        },
                        judgement = new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = string.Empty,
                            Font = OsuFont.Torus.With(weight: FontWeight.Bold, fixedWidth: true),
                            Colour = Color4.White,
                            Shadow = true,
                        },
                    },
                },

                // Right group — pinned to the right edge, reading combo then score.
                comboCell = numberCell(out combo, "0x", FontWeight.Regular, Anchor.CentreRight),
                scoreCell = numberCell(out score, "00000000", FontWeight.Bold, Anchor.CentreRight),
            };
        }

        /// <summary>A fixed-width numeric cell: the text is right-aligned inside it in a tabular
        /// (fixed-width) figure font, so rolling digits change in place.</summary>
        private static Container numberCell(out OsuSpriteText text, string sample, FontWeight weight, Anchor anchor)
        {
            text = new OsuSpriteText
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                Text = sample,
                Font = OsuFont.Torus.With(weight: weight, fixedWidth: true),
                Colour = Color4.White,
                Shadow = true,
            };

            return new Container
            {
                Anchor = anchor,
                Origin = anchor,
                RelativeSizeAxes = Axes.Y,
                Child = text,
            };
        }

        /// <summary>Re-sizes and re-lays-out the row's columns for the current density.</summary>
        public void Apply(RailMetrics metrics)
        {
            Height = metrics.RowHeight;
            metricsHeight = metrics.RowHeight;

            float fs = metrics.FontSize;

            foreach (var sprite in new[] { accuracy, performance, combo, score })
                sprite.Font = sprite.Font.With(size: fs);

            playerName.Font = playerName.Font.With(size: fs);
            mods.Font = mods.Font.With(size: fs * 0.8f);
            judgement.Font = judgement.Font.With(size: fs * 0.85f);
            nameCell.Spacing = new Vector2(fs * 0.28f, 0);

            // Column widths, all derived from the font size so the table scales with the board. The
            // numeric ones are counted in fixed-width figure glyphs (~0.55 em each); the name block
            // auto-sizes and flows toward the numbers, so it needs no fixed width of its own.
            float d = fs * 0.55f;
            float gap = fs * 0.5f;

            float accW = 7f * d;
            float ppW = 8.5f * d;
            float gradeW = fs * 2.1f;
            float comboW = 7f * d;
            float scoreW = 8.5f * d;

            accCell.Width = accW;
            ppCell.Width = ppW;
            gradeCell.Width = gradeW;
            comboCell.Width = comboW;
            scoreCell.Width = scoreW;

            float lm = 5;
            accCell.X = lm;
            ppCell.X = lm + accW + gap;
            gradeCell.X = ppCell.X + ppW + gap;
            nameCell.X = gradeCell.X + gradeW + gap;

            // Right group X is measured back from the right edge (negative, right-anchored).
            float rm = 5;
            scoreCell.X = -rm;
            comboCell.X = -(rm + scoreW + gap);

            if (rankGraphic != null)
                rankGraphic.Size = new Vector2(gradeW, metrics.RowHeight * 0.72f);
        }

        /// <summary>Reads this player's state at <paramref name="time"/> onto the row.</summary>
        /// <param name="alive">Whether they are still in it — decided by the caller, because the
        /// survivor floor depends on the whole field rather than on this one play.</param>
        public void UpdateFrom(ReplayTimeline timeline, KnockoutRules rules, double time, int place, bool alive)
        {
            var point = timeline.At(time);
            bool pending = IsPending(timeline, time);

            // While a player is still being simulated, show the map's neutral START state — zeros —
            // rather than dash sentinels. A dash reads as "broken"; a still-simulating player has a
            // real state (nothing scored yet), and the numbers fill in the moment their recording
            // reaches the playhead. The rolling figures start from zero so they climb rather than jump.
            if (pending)
            {
                rollingScore = 0;
                rollingAccuracy = 0;
                rollingPerformance = 0;
            }
            else
            {
                rollingScore = roll(rollingScore, point.Score);
                rollingAccuracy = roll(rollingAccuracy, point.Accuracy);
                rollingPerformance = roll(rollingPerformance, point.Performance);
            }

            // Score abbreviated the way danser does it: 16.85M, 15.30K, or the raw number below 1000.
            score.Text = formatScore((long)Math.Round(rollingScore));
            accuracy.Text = (rollingAccuracy * 100).ToString("0.00") + "%";
            combo.Text = (pending ? 0 : point.Combo).ToString("N0") + "x";
            performance.Text = rollingPerformance.ToString("0.00") + "pp";

            applyGrade(pending ? string.Empty : point.Grade, metricsHeight);

            // The recent-judgement column: the most recent non-perfect result, but only while it is
            // still RECENT — a miss lingers a moment then clears, rather than sticking for the song.
            var recent = !pending && time - point.Time >= 0 && time - point.Time < recent_judgement_ms
                ? point.Judgement
                : HitResult.None;

            judgement.Text = judgementText(recent);
            judgement.Colour = judgementColour(recent);

            ShownPending = pending;

            if (alive == ShownAlive)
                return;

            ShownAlive = alive;
            RestingAlpha = alive ? 1 : 0.45f;

            this.FadeTo(RestingAlpha, 300, Easing.OutQuint);
            background.FadeColour(alive ? Color4.Black.Opacity(0.45f) : Color4.DarkRed.Opacity(0.5f), 300, Easing.OutQuint);
        }

        /// <summary>Row height as of the last <see cref="Apply"/>, so a grade rebuilt between resizes
        /// is created at the right size.</summary>
        private float metricsHeight;

        /// <summary>How long after a judgement it still shows in the column.</summary>
        private const double recent_judgement_ms = 600;

        /// <summary>
        /// Draws the grade as a rank GRAPHIC — lazer's own leaderboard badge, which renders for every
        /// skin. The old skin-texture lookup returned nothing under Argon (it has no ranking-*-small
        /// textures) and every row fell back to a bare letter, which is what the user saw.
        /// </summary>
        private void applyGrade(string grade, float height)
        {
            if (grade == currentGrade)
                return;

            currentGrade = grade;
            gradeCell.Clear();
            rankGraphic = null;

            if (grade.Length == 0 || !Enum.TryParse<ScoreRank>(grade, out var rank))
                return;

            gradeCell.Add(rankGraphic = new DrawableRank(rank)
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(gradeCell.DrawWidth > 0 ? gradeCell.DrawWidth : height * 1.6f, height * 0.72f),
            });
        }

        /// <summary>Score in danser's abbreviated form: 16.85M at a million, 15.30K at a thousand,
        /// the raw number below that. Fixed shape (two decimals) so the tabular column does not jump.</summary>
        private static string formatScore(long score)
            => score >= 1_000_000 ? (score / 1_000_000.0).ToString("0.00") + "M"
             : score >= 1_000 ? (score / 1_000.0).ToString("0.00") + "K"
             : score.ToString();

        private static string judgementText(HitResult result) => result switch
        {
            HitResult.Miss => "X",
            HitResult.Meh => "50",
            HitResult.Ok => "100",
            _ => string.Empty,
        };

        private static Color4 judgementColour(HitResult result) => result switch
        {
            HitResult.Miss => new Color4(1f, 0.3f, 0.3f, 1f),
            HitResult.Meh => new Color4(1f, 0.7f, 0.2f, 1f),
            HitResult.Ok => new Color4(0.4f, 1f, 0.5f, 1f),
            _ => Color4.White,
        };
    }
}
