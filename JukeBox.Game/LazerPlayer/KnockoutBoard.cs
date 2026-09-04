#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Online.Leaderboards;
using osu.Game.Skinning;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Replays;
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
    /// <param name="Frames">Their replay's input frames, for the key-press indicator at the row's
    /// left. Null (as in the grid, or a test) simply draws no keys.</param>
    /// <param name="Version">The scoring-version tag ("V1", "Classic", "Lazer", "V2"), drawn in a
    /// muted accent AFTER the mods — a distinct marker, not folded into the white mod run. Empty
    /// draws nothing.</param>
    public readonly record struct Entrant(string Name, Color4 Colour, ReplayTimeline Timeline, string Mods = "", IReadOnlyList<ReplayFrame>? Frames = null, string Version = "");

    private readonly IReadOnlyList<Entrant> entrants;
    private readonly List<Row> rows = new List<Row>();

    /// <summary>The longest name+mods in the field, in characters. Every row's name column is
    /// RESERVED at this width so the combo — and every column after it — sits AFTER the name rather
    /// than being drawn on top of it, and so those columns line up down the board instead of stepping
    /// in and out with each player's name length. Mods are counted because they are drawn beside the
    /// name and a "+HDDT" is as much a part of the block to clear as the name itself.</summary>
    private readonly int maxNameChars;

    /// <summary>The rules in force. Assign to change mode or sorting; the board follows.</summary>
    public KnockoutRules Rules { get; set; } = new KnockoutRules();

    /// <summary>When set, a knocked-out player's WHOLE row is removed (faded to nothing), not just
    /// dimmed. Off by default — an eliminated row otherwise dims to a whisper and sinks to the bottom.
    /// Fed from config by the combine.</summary>
    public bool RemoveRowOnKnockout { get; set; }

    /// <summary>
    /// The skin the rail looks its grade textures up in — the SAME chain the chart renders under
    /// (the user's selected skin with the classic legacy skin behind it), set by the combine once the
    /// chart's skin is built. So a grade that has a "ranking-X-small" texture in the active skin is
    /// drawn as THAT texture; only a skin with none falls back to the DrawableRank badge. Null (no
    /// combine, or before the chart loads) means every grade uses the badge fallback.
    /// </summary>
    public ISkinSource? GradeSkin
    {
        get => gradeSkin;
        set
        {
            if (ReferenceEquals(gradeSkin, value))
                return;

            gradeSkin = value;

            // A new skin means every grade must be re-fetched, so drop the cache.
            gradeTextures.Clear();
        }
    }

    private ISkinSource? gradeSkin;

    /// <summary>The grade each row currently has a graphic for, so the skin is only queried again
    /// when a row's grade actually changes — a texture lookup walks the chain, and doing it for 47
    /// rows every frame is a lot of work for the same answer.</summary>
    private readonly Dictionary<int, string> gradeTextures = new Dictionary<int, string>();

    /// <summary>Points each row's grade at the active skin's own graphic, or the badge fallback.</summary>
    private void applyGradeTextures()
    {
        foreach (var row in rows)
        {
            string wanted = row.WantedGrade;

            if (gradeTextures.TryGetValue(row.PlayerIndex, out string? shown) && shown == wanted)
                continue;

            gradeTextures[row.PlayerIndex] = wanted;

            var texture = wanted.Length == 0 ? null : gradeSkin?.GetTexture($"ranking-{wanted}-small");
            row.ApplyGradeGraphic(texture, wanted);
        }
    }

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
        maxNameChars = entrants.Count == 0 ? 4 : entrants.Max(e =>
            e.Name.Length
            + (e.Mods.Length == 0 ? 0 : e.Mods.Length + 1)
            + (e.Version.Length == 0 ? 0 : e.Version.Length + 1));

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

        // The "simulating N%" indicator no longer lives on the rail — it moved to an unobtrusive
        // bottom-right caption on the combine layer, with the full buffered picture in the Playback
        // tab's buffer bar. The rail simply shows neutral zeros for a still-pending player (see
        // UpdateFrom) rather than captioning its own load.

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

            row.RemoveRowOnKnockout = RemoveRowOnKnockout;
            row.UpdateFrom(entrants[order[rank]].Timeline, Rules, time, rank + 1, !eliminated.Contains(order[rank]));
        }

        // After the rows have read their grades, so a grade that improves mid-song swaps its graphic.
        // Cheap: it only asks the skin when a row's grade has actually changed.
        applyGradeTextures();
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

        foreach (var row in rows)
            row.Apply(metrics);

        // The board is as wide as a row needs to be once the name column is reserved at the field's
        // longest name+mods — so the right group (combo, score, hit badge) lands AFTER the name
        // rather than on top of it. Every row reserves the same name width, so they all report the
        // same natural width; take the first.
        Width = rows.Count > 0 ? rows[0].NaturalWidth : metrics.Width;

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

        /// <summary>Board-fed: whether a knocked-out player's whole row is removed (see the board's
        /// <see cref="KnockoutBoard.RemoveRowOnKnockout"/>).</summary>
        internal bool RemoveRowOnKnockout;

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
        private readonly OsuSpriteText version = null!;

        // The key-press indicator at the row's far left: two bars (osu!'s left and right buttons —
        // lazer collapses M1/M2 into these), lit as the player holds them, read from the replay
        // frames at the playhead. Null frames (grid, tests) simply draw nothing.
        private readonly IReadOnlyList<ReplayFrame>? frames;
        private readonly FillFlowContainer keysCell = null!;
        private readonly Container key1 = null!;
        private readonly Container key2 = null!;
        private int frameHint;

        private static readonly Color4 key_dim = new Color4(0.22f, 0.22f, 0.26f, 1f);

        // The fixed-width cells the drawables live in. Their widths and X positions are set per
        // density in Apply, which is what makes the row a table rather than a run-on line. The hit
        // badge sits in its own fixed column AFTER the score (rightmost) so it is always in the same
        // findable place, not chased around by the length of the name.
        private readonly Container accCell = null!;
        private readonly Container ppCell = null!;
        private readonly Container gradeCell = null!;

        // The name column is a FIXED-WIDTH masking cell (reserved at the field's longest name+mods),
        // not an auto-sizing one. Auto-sizing let a long name run rightward under the right-pinned
        // combo and score — the combo drawn on top of the name. Reserving the width, and sizing the
        // board so the right group begins after it, keeps them apart. The inner flow holds the name
        // and mods and is clipped to the reservation if it somehow exceeds it.
        private readonly Container nameCell = null!;
        private readonly FillFlowContainer nameFlow = null!;
        private readonly Container comboCell = null!;
        private readonly Container scoreCell = null!;
        private readonly Container judgeCell = null!;

        /// <summary>The row's natural width once the name column is reserved: the right edge of the
        /// last (hit-badge) column plus its margin. The board sizes itself to this so the right group
        /// sits after the name rather than over it.</summary>
        public float NaturalWidth { get; private set; }

        private Color4 playerColour;

        /// <summary>The grade currently drawn, so the graphic is only rebuilt when it changes.</summary>
        private string currentGrade = string.Empty;

        // The grade lives in a masking cell so it can NEVER draw outside the grade column, whichever
        // of the two ways it is drawn: the active skin's own "ranking-X-small" texture (fit-scaled
        // into the cell), or — only when the skin has no such texture — lazer's DrawableRank badge,
        // constrained to fill the cell rather than blowing up to fill the rail (which is what it did).
        private readonly Sprite gradeImage = null!;
        private readonly Container rankFallback = null!;
        private bool gradeIsImage;

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

        /// <summary>Test hooks: the hit badge's current scale and opacity, which the pop-in animation
        /// drives — big and opaque on a fresh judgement, settling to normal size and fading to nothing
        /// across its life.</summary>
        internal float JudgementScale => judgement.Scale.X;

        internal float JudgementAlpha => judgement.Alpha;

        /// <summary>Test hooks: whether each key bar is lit for a held button at the current time.</summary>
        internal bool LeftKeyHeld => key1Held;

        internal bool RightKeyHeld => key2Held;

        private bool key1Held;
        private bool key2Held;

        /// <summary>Test hook: the score column's right edge on screen. Fixed as the value rolls
        /// (right-aligned in a fixed-width cell), which is what stops the digits shaking.</summary>
        internal float ScoreRightEdge => score.ScreenSpaceDrawQuad.TopRight.X;

        /// <summary>Test hooks: the reserved name column's right edge and the combo column's left edge
        /// on screen. The name must end at or before the combo begins — the two overlapping is the
        /// combo drawn on top of the name.</summary>
        internal float NameRightEdge => nameCell.ScreenSpaceDrawQuad.TopRight.X;

        internal float ComboLeftEdge => comboCell.ScreenSpaceDrawQuad.TopLeft.X;

        /// <summary>Test hook: whether the numeric columns use a tabular (fixed-width) figure font,
        /// so a changing digit keeps the same advance and nothing reflows.</summary>
        internal bool NumbersAreFixedWidth
            => accuracy.Font.FixedWidth && performance.Font.FixedWidth && combo.Font.FixedWidth && score.Font.FixedWidth;

        /// <summary>Test hook: whether the grade is drawn as a rank GRAPHIC (danser/leaderboard
        /// badge) rather than a bare letter. Always true once a grade exists — the badge renders for
        /// every skin, which is the fix for the letter showing through Argon.</summary>
        internal bool GradeIsImage => gradeIsImage;

        internal string ModsText => mods.Text.ToString()!;

        internal Color4 ModsColour => mods.Colour;

        /// <summary>Test hook: the scoring-version tag this row shows ("V1", "Classic", …), empty when
        /// the entrant carried none.</summary>
        internal string VersionText => version.Text.ToString()!;

        internal string NameText => playerName.Text.ToString()!;

        /// <summary>Test hook: whether this row is currently drawn at all (its resting alpha) — a
        /// knocked-out player's row drops to nothing when remove-row-after-knockout is on.</summary>
        internal bool RowShown => RestingAlpha > 0.01f;

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
            frames = entrant.Frames;

            RelativeSizeAxes = Axes.X;

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Transparent,
                },

                // The key-press bars, at the far left the way danser draws them.
                keysCell = new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.X,
                    RelativeSizeAxes = Axes.Y,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(1, 0),
                    Alpha = frames == null ? 0 : 1,
                    Children = new Drawable[]
                    {
                        key1 = keyBar(),
                        key2 = keyBar(),
                    },
                },

                // Left group — fixed columns anchored to the left, so accuracy, pp, the grade badge
                // and the name all start at the same x on every row rather than the pp width shoving
                // the ones after it around.
                accCell = numberCell(out accuracy, "100.00%", FontWeight.SemiBold, Anchor.CentreLeft),
                ppCell = numberCell(out performance, "0.00pp", FontWeight.Regular, Anchor.CentreLeft),

                gradeCell = new Container
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.Y,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        gradeImage = new Sprite
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            RelativeSizeAxes = Axes.Both,
                            FillMode = FillMode.Fit,
                            Alpha = 0,
                        },
                        rankFallback = new Container
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            RelativeSizeAxes = Axes.Both,
                            Masking = true,
                            Alpha = 0,
                        },
                    },
                },

                // Name and mods in a reserved, masking column: the flow inside runs left to right,
                // but the cell is a fixed width (set per density in Apply) so it cannot push into the
                // combo column beside it. The board is sized to leave room for this reservation.
                nameCell = new Container
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.Y,
                    Masking = true,
                    Child = nameFlow = new FillFlowContainer
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
                            // The scoring-version tag, in a muted accent so it reads as a marker
                            // distinct from the white mods rather than more of them.
                            version = new OsuSpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Text = entrant.Version,
                                Font = OsuFont.Torus.With(weight: FontWeight.SemiBold, size: 13),
                                Colour = Color4.White.Opacity(0.6f),
                                Shadow = true,
                            },
                        },
                    },
                },

                // Right group — pinned to the right edge, reading combo, score, then the recent-hit
                // badge (100/50/X) in its own fixed column AFTER the score.
                comboCell = numberCell(out combo, "0x", FontWeight.Regular, Anchor.CentreRight),
                scoreCell = numberCell(out score, "00000000", FontWeight.Bold, Anchor.CentreRight),
                judgeCell = numberCell(out judgement, string.Empty, FontWeight.Bold, Anchor.CentreRight),
            };
        }

        /// <summary>One key-press bar: a small rounded box, dim until the player is holding that key.</summary>
        private static Container keyBar() => new Container
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Masking = true,
            CornerRadius = 1.5f,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = key_dim },
        };

        /// <summary>Lights the two key bars for whatever the player is holding at <paramref name="time"/>,
        /// read from the replay frames. Nothing to do when the row carries no frames.</summary>
        private void updateKeys(double time)
        {
            if (frames == null || frames.Count == 0)
                return;

            // Walk from the last frame we used rather than binary-searching every frame: playback is
            // overwhelmingly forward, so the right frame is usually one or two along.
            int i = Math.Clamp(frameHint, 0, frames.Count - 1);

            while (i + 1 < frames.Count && frames[i + 1].Time <= time)
                i++;
            while (i > 0 && frames[i].Time > time)
                i--;

            frameHint = i;

            key1Held = false;
            key2Held = false;

            if (frames[i] is OsuReplayFrame osu && frames[i].Time <= time)
            {
                key1Held = osu.Actions.Contains(OsuAction.LeftButton);
                key2Held = osu.Actions.Contains(OsuAction.RightButton);
            }

            // Lit bars carry the player's OWN colour (the same colour as their name and cursor), so
            // the key display reads as "this player" at a glance. The two bars are one shade apart so
            // they stay distinguishable.
            ((Box)key1.Child).Colour = key1Held ? playerColour : key_dim;
            ((Box)key2.Child).Colour = key2Held ? key2Colour() : key_dim;
        }

        /// <summary>The second key bar's lit colour: the player's colour a shade darker, so the two
        /// bars read as a pair without a hard-coded palette.</summary>
        private Color4 key2Colour()
        {
            var c = playerColour;
            return new Color4(c.R * 0.7f, c.G * 0.7f, c.B * 0.7f, 1f);
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

            float fs = metrics.FontSize;

            foreach (var sprite in new[] { accuracy, performance, combo, score })
                sprite.Font = sprite.Font.With(size: fs);

            playerName.Font = playerName.Font.With(size: fs);
            mods.Font = mods.Font.With(size: fs * 0.8f);
            judgement.Font = judgement.Font.With(size: fs * 0.85f);
            nameFlow.Spacing = new Vector2(fs * 0.28f, 0);

            // Column widths, all derived from the font size so the table scales with the board. The
            // numeric ones are counted in fixed-width figure glyphs (~0.55 em each).
            float d = fs * 0.55f;
            float gap = fs * 0.5f;

            float accW = 7f * d;
            float ppW = 8.5f * d;
            float gradeW = fs * 2.1f;
            float comboW = 7f * d;
            float scoreW = 8.5f * d;
            float judgeW = 3.5f * d;

            // The reserved name column: the field's longest name+mods, in em. The name font is
            // proportional so this is an estimate rather than a fixed-glyph count — 0.6em runs a
            // little wide of the average, which is the safe direction (extra space, not a clipped
            // name), and the cell masks anything past it. Clamped so one very long tag cannot make
            // the whole rail a banner across the playfield.
            float nameW = Math.Min(maxNameChars, 22) * fs * 0.6f;

            accCell.Width = accW;
            ppCell.Width = ppW;
            gradeCell.Width = gradeW;
            nameCell.Width = nameW;
            comboCell.Width = comboW;
            scoreCell.Width = scoreW;
            judgeCell.Width = judgeW;

            // Key bars: two narrow pills at the far left, each a little over half the row tall.
            float keyW = fs * 0.34f;
            float keyH = metrics.RowHeight * 0.55f;
            key1.Size = new Vector2(keyW, keyH);
            key2.Size = new Vector2(keyW, keyH);
            float keysW = frames == null ? 0 : 2 * keyW + 1 + gap;

            float lm = 5;
            keysCell.X = lm;
            accCell.X = lm + keysW;
            ppCell.X = accCell.X + accW + gap;
            gradeCell.X = ppCell.X + ppW + gap;
            nameCell.X = gradeCell.X + gradeW + gap;

            // Right group X is measured back from the right edge (negative, right-anchored): the hit
            // badge is rightmost (AFTER the score), then the score, then the combo.
            float rm = 5;
            judgeCell.X = -rm;
            scoreCell.X = -(rm + judgeW + gap);
            comboCell.X = -(rm + judgeW + gap + scoreW + gap);

            // The board is sized to exactly this, so the combo's left edge (measured back from the
            // right) falls one gap past the reserved name's right edge — the two never overlap.
            NaturalWidth = nameCell.X + nameW + gap + comboW + gap + scoreW + gap + judgeW + rm;

            // The grade graphic fills the (small, masking) grade cell via RelativeSizeAxes, so it
            // scales with the density automatically — no per-graphic resize here.
        }

        /// <summary>Reads this player's state at <paramref name="time"/> onto the row.</summary>
        /// <param name="alive">Whether they are still in it — decided by the caller, because the
        /// survivor floor depends on the whole field rather than on this one play.</param>
        public void UpdateFrom(ReplayTimeline timeline, KnockoutRules rules, double time, int place, bool alive)
        {
            updateKeys(time);

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

            WantedGrade = pending ? string.Empty : point.Grade;

            // The recent-judgement badge (X / 50 / 100): the most recent non-perfect result, read from
            // the timeline so it survives a seek and is not wiped by the next perfect hit. It POPS in
            // large on each new drop, then settles to its normal size and fades away over ~1.5s — the
            // old hard on/off flashed by too fast to read.
            var recent = pending ? null : timeline.RecentImperfect(time, badge_duration_ms);

            if (recent is { } drop)
            {
                judgement.Text = judgementText(drop.Result);
                judgement.Colour = judgementColour(drop.Result);

                float u = (float)Math.Clamp((time - drop.Time) / badge_duration_ms, 0, 1);

                // Pop: from 1.7x down to normal over the first third, eased out so it snaps in and
                // eases to rest. Fade: held for the first part, then to nothing by the end.
                float scale = u < badge_pop_fraction
                    ? 1f + 0.7f * (1f - easeOutQuad(u / badge_pop_fraction))
                    : 1f;

                float alpha = u < badge_hold_fraction
                    ? 1f
                    : 1f - (u - badge_hold_fraction) / (1f - badge_hold_fraction);

                judgement.Scale = new Vector2(scale);
                judgement.Alpha = Math.Clamp(alpha, 0, 1);
            }
            else
            {
                judgement.Text = string.Empty;
                judgement.Alpha = 0;
                judgement.Scale = Vector2.One;
            }

            ShownPending = pending;
            ShownAlive = alive;

            // The row's resting alpha: full while alive; once eliminated, either REMOVED entirely
            // (faded to nothing) when remove-row-after-knockout is on, or dimmed to a whisper otherwise.
            // Recomputed every frame — not just on the alive transition — so flipping the option
            // mid-song reaches a player who is already out. NO background strip behind a row either way
            // (the user wants the rows drawn straight over the playfield); the alive/out difference is
            // carried entirely by this alpha. The fade is issued only when the target actually changes.
            float wantResting = alive ? 1f : (RemoveRowOnKnockout ? 0f : 0.3f);

            if (Math.Abs(wantResting - RestingAlpha) > 0.001f)
            {
                RestingAlpha = wantResting;
                this.FadeTo(RestingAlpha, 300, Easing.OutQuint);
            }
        }

        /// <summary>How long the hit badge lingers after the judgement that triggered it — the whole
        /// pop-in, settle and fade happens across this window.</summary>
        private const double badge_duration_ms = 1500;

        /// <summary>Fraction of the badge's life spent popping from large down to its normal size.</summary>
        private const float badge_pop_fraction = 0.33f;

        /// <summary>Fraction of the badge's life it stays fully opaque before it starts fading out.</summary>
        private const float badge_hold_fraction = 0.4f;

        /// <summary>Quadratic ease-out (fast then slow), for the pop settling to rest.</summary>
        private static float easeOutQuad(float t) => 1f - (1f - t) * (1f - t);

        /// <summary>
        /// Draws the grade as a rank GRAPHIC — lazer's own leaderboard badge, which renders for every
        /// skin. The old skin-texture lookup returned nothing under Argon (it has no ranking-*-small
        /// textures) and every row fell back to a bare letter, which is what the user saw.
        /// </summary>
        /// <summary>The grade this row wants drawn, set each update; the board reads it, resolves the
        /// skin texture and calls <see cref="ApplyGradeGraphic"/>.</summary>
        internal string WantedGrade { get; private set; } = string.Empty;

        /// <summary>
        /// Draws the grade: the active skin's <paramref name="texture"/> when it has one (fit into the
        /// masking cell), otherwise lazer's DrawableRank badge constrained to the same cell. Either
        /// way it is a small graphic confined to the grade column — never a bare letter, never the
        /// column-filling giant the unconstrained DrawableRank drew.
        /// </summary>
        public void ApplyGradeGraphic(Texture? texture, string grade)
        {
            currentGrade = grade;

            if (grade.Length == 0)
            {
                gradeImage.Alpha = 0;
                rankFallback.Alpha = 0;
                rankFallback.Clear();
                gradeIsImage = false;
                return;
            }

            if (texture != null)
            {
                gradeImage.Texture = texture;
                gradeImage.Alpha = 1;
                rankFallback.Alpha = 0;
                rankFallback.Clear();
                gradeIsImage = true;
                return;
            }

            if (Enum.TryParse<ScoreRank>(grade, out var rank))
            {
                rankFallback.Clear();
                rankFallback.Add(new DrawableRank(rank) { RelativeSizeAxes = Axes.Both });
                rankFallback.Alpha = 1;
                gradeImage.Alpha = 0;
                gradeIsImage = true;
                return;
            }

            gradeImage.Alpha = 0;
            rankFallback.Alpha = 0;
            gradeIsImage = false;
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
