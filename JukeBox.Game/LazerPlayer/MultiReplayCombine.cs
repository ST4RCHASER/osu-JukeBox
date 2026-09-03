#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Everyone's replay over ONE chart, in the shape danser-go calls Knockout: the beatmap is
/// rendered once, every player's cursor is drawn on top of it in their own colour, and a live
/// scoreboard down the side re-orders itself as they overtake each other.
///
/// <para>
/// The board is the point rather than a caption on it. Its numbers come from each player's
/// recorded <see cref="Replays.ReplayTimeline"/> read at the current playback time — see
/// <see cref="ReplaySimulator"/> — so a row shows what that player HAD at this moment in the song,
/// not what they finished with. That is also what makes it survive seeking, which a running total
/// cannot.
/// </para>
///
/// <para>
/// Elimination is off by default (<see cref="Replays.KnockoutMode.Showcase"/>): every play runs to
/// the end and the board just sorts. Turning it on is what makes it a contest — players are out on
/// their first combo break, sink to the bottom of the board greyed out, and the survivors' cursors
/// grow as the field thins.
/// </para>
///
/// <para>
/// The cursors are lazer's own <c>ReplayAnalysisOverlay</c>, mounted inside the single hosted
/// ruleset's <c>PlayfieldAdjustmentContainer</c> — see
/// <see cref="LazerChartLayer.AddCursorOverlay"/>. That is what makes this affordable AND correct:
/// affordable because a cursor is a handful of drawables where a whole extra
/// <c>DrawableRuleset</c> is a beatmap conversion and a hit-object pool, and correct because
/// living inside the playfield's own container means the cursors inherit its transform instead of
/// this class re-deriving it and drifting the moment the zoom or aspect changes.
/// </para>
///
/// <para>
/// osu! only, unavoidably — the other three rulesets have no cursor to overlay. A non-osu! beatmap
/// falls back to the plain single chart, which is why <see cref="CursorsAttached"/> exists.
/// </para>
/// </summary>
public partial class MultiReplayCombine : CompositeDrawable
{
    private readonly string osuFile;
    private readonly IReadOnlyList<ReplayAttachment> replays;

    private LazerChartLayer chart = null!;
    private ReplaySimulator simulator = null!;
    private KnockoutBoard board = null!;
    private bool attached;

    /// <summary>
    /// The cursors, indexed by replay — one per player, the chart's driver included. A slot is null
    /// only when that replay carried no frames to draw.
    /// </summary>
    private readonly List<PlayerCursor?> cursors = new List<PlayerCursor?>();

    /// <summary>Test hook: cursors actually mounted, one per replay that had frames.</summary>
    internal int CursorsAttached { get; private set; }

    /// <summary>Test hook: the mounted cursors, for asserting they are distinctly coloured.</summary>
    internal IReadOnlyList<PlayerCursor?> Cursors => cursors;

    /// <summary>Test hook: the one hosted chart every cursor is drawn over.</summary>
    internal LazerChartLayer Chart => chart;

    /// <summary>Test hook: the live scoreboard.</summary>
    internal KnockoutBoard Board => board;

    /// <summary>Test hook: the off-screen playthrough feeding the board.</summary>
    internal ReplaySimulator Simulator => simulator;

    /// <summary>
    /// The knockout rules in force. Assigning reaches the board immediately, so a user changing the
    /// mode mid-song sees it apply rather than having to reload the map — the plays are already
    /// recorded, so who is out is a re-reading of data that is all there.
    /// </summary>
    public KnockoutRules Rules
    {
        get => rules;
        set
        {
            rules = value;

            if (board != null)
                board.Rules = value;
        }
    }

    private KnockoutRules rules = new KnockoutRules();

    /// <summary>Whether the chart's hit sounds play. One chart, so no flam to avoid here.</summary>
    public bool HitSoundsEnabled
    {
        set => chart.HitSoundsEnabled.Value = value;
    }

    /// <param name="osuFile">The one difficulty every replay was played on.</param>
    /// <param name="replays">The replays, in drop order.</param>
    public MultiReplayCombine(string osuFile, IReadOnlyList<ReplayAttachment> replays)
    {
        this.osuFile = osuFile;
        this.replays = replays;

        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        // The FIRST replay drives the rendered chart — its hits are the ones the map reacts to —
        // and everyone else rides along as a cursor. Someone has to be the one the chart follows.
        chart = new LazerChartLayer(new FlatWorkingBeatmap(osuFile), osuFile, replays.FirstOrDefault()?.Score)
        {
            AlwaysPresent = true,

            // Under the driving player's OWN recorded mods, to match how their score is computed.
            // Letting the shared Chart-tab selection edit this chart would put the rendered play
            // and the numbers beside it under different mods.
            UseRecordedReplayModsOnly = true,
        };

        simulator = new ReplaySimulator(osuFile, replays);

        InternalChildren = new Drawable[] { chart, simulator };
    }

    /// <summary>
    /// Builds the board once the simulator has its timelines, which it creates when IT loads.
    /// Deliberately not done in this class's LoadComplete: a parent's LoadComplete is not
    /// guaranteed to run after its children's, and reading the timelines a frame too early is an
    /// index out of range rather than something that degrades quietly.
    /// </summary>
    private void buildBoardWhenReady()
    {
        if (board != null || simulator.Timelines.Count < replays.Count)
            return;

        var entrants = replays
                       .Select((replay, index) => new KnockoutBoard.Entrant(
                           // The bare name, with the mods handed over SEPARATELY: the board draws
                           // the name in the player's colour and the mods in white, so the two
                           // cannot be one string.
                           replay.PlayerName.Length > 0 ? replay.PlayerName : "unknown",
                           ColourFor(index, replays.Count),
                           simulator.Timelines[index],
                           replay.ModAcronyms.Count > 0 ? "+" + string.Join(string.Empty, replay.ModAcronyms) : string.Empty))
                       .ToList();

        AddInternal(board = new KnockoutBoard(entrants)
        {
            Anchor = Anchor.TopLeft,
            Origin = Anchor.TopLeft,
            Margin = new MarginPadding(6),
            Rules = rules,

            // Hovering a rail row focuses that player's cursor. The value is only stashed here; the
            // fade is issued from updateCursors so it runs once per change instead of every frame,
            // and so a hover that arrives before the cursors are mounted still takes effect once
            // they are.
            PlayerFocused = index => focusedPlayer = index,
        });
    }

    /// <summary>The player whose rail row is currently hovered, or null when none is. A focused
    /// player's cursor stays at full strength while every other fades to a whisper.</summary>
    private int? focusedPlayer;

    /// <summary>The focus last pushed to the cursors, so the fade is issued only when it changes.
    /// Never written while the cursors are unattached, so a hover during load is applied once they
    /// exist rather than being swallowed.</summary>
    private int? appliedFocus;

    protected override void Update()
    {
        base.Update();

        buildBoardWhenReady();
        attachCursors();
        updateCursors();
    }

    /// <summary>
    /// Mounts a cursor for EVERY player onto the one chart. The ruleset only exists once the chart
    /// has finished its own async load, so this cannot happen in load(). Done once, then never again.
    ///
    /// <para>
    /// Every player including the one driving the chart, which is the change from what shipped. The
    /// driver used to rely on the playfield's own cursor, which is white and cannot be tinted, so
    /// even once the others worked one player would still have been the odd one out. The playfield
    /// cursor is switched off and all N are drawn the same way.
    /// </para>
    /// </summary>
    private void attachCursors()
    {
        if (attached || chart.DrawableRuleset == null)
            return;

        attached = true;
        chart.HidePlayfieldCursor();

        for (int i = 0; i < replays.Count; i++)
        {
            var replay = replays[i].Score?.Replay;

            if (replay == null)
            {
                cursors.Add(null);
                continue;
            }

            var cursor = new PlayerCursor(MultiReplayGrid.DisplayName(replays[i]), replay.Frames, ColourFor(i, replays.Count));

            cursors.Add(chart.AddPlayerCursor(cursor) ? cursor : null);

            if (cursors[i] != null)
                CursorsAttached++;
        }
    }

    /// <summary>
    /// Grows the surviving cursors as the field thins and fades out the eliminated. In showcase
    /// mode nobody is ever eliminated, so this settles at the smallest size and stops mattering —
    /// which is the intent, since a showcase is about seeing all of the plays at once.
    /// </summary>
    private void updateCursors()
    {
        if (!attached || board == null)
            return;

        double time = Clock.CurrentTime;
        var timelines = simulator.Timelines;

        if (timelines.Count == 0)
            return;

        int alive = rules.AliveCount(timelines, time);
        float scale = KnockoutRules.CursorScale(alive, timelines.Count);

        for (int i = 0; i < cursors.Count && i < timelines.Count; i++)
        {
            if (cursors[i] is not { } cursor)
                continue;

            // The field-aware check, so the survivor floor is honoured here as well as on the board.
            bool stillIn = rules.AliveAt(timelines, i, time);

            cursor.Scale = new Vector2(stillIn ? scale : 1);
            cursor.Alpha = stillIn ? 1 : 0;

            flashNewBreaks(i, cursor, timelines[i], time);
        }

        lastFlashCheck = time;

        applyFocus();
    }

    /// <summary>
    /// Fades the focused player's cursor to full and every other to a whisper, or all of them back
    /// to full when nothing is hovered. Issued only when the focus has actually changed, so the
    /// fade plays once rather than being restarted every frame — and driven off the cursors' own
    /// focus channel, which multiplies over the alive/eliminated alpha rather than overwriting it.
    /// </summary>
    private void applyFocus()
    {
        if (focusedPlayer.Equals(appliedFocus))
            return;

        for (int i = 0; i < cursors.Count; i++)
        {
            if (cursors[i] is not { } cursor)
                continue;

            float target = focusedPlayer is not { } focused ? 1f : (i == focused ? 1f : 0.1f);
            cursor.SetFocusAlpha(target);
        }

        appliedFocus = focusedPlayer;
    }

    /// <summary>
    /// Fires the combo-break cue when the playhead crosses a break this player has not been flashed
    /// for yet.
    ///
    /// <para>
    /// Driven off the recorded timeline rather than off a live judgement, which is what makes it
    /// work at all here: the breaks are all known before the song starts, so a break is a comparison
    /// against the clock instead of an event that has to be caught as it happens.
    /// </para>
    ///
    /// <para>
    /// Only breaks the playhead moves FORWARD across count. Seeking backwards past one and playing
    /// it again does flash again, which is right — the viewer is watching that moment again. Jumping
    /// backwards does not fire anything, or scrubbing would set the whole board flashing.
    /// </para>
    /// </summary>
    private void flashNewBreaks(int player, PlayerCursor cursor, ReplayTimeline timeline, double time)
    {
        if (lastFlashCheck is not { } previous || time <= previous)
            return;

        foreach (var point in timeline.Points)
        {
            if (!point.BrokeCombo)
                continue;

            if (point.Time > previous && point.Time <= time)
            {
                // Only breaks big enough to matter get announced on the playfield. At high player
                // counts, flashing every dropped combo is a continuous flicker of red names and the
                // cue stops meaning anything — the board still shows the break either way.
                if (rules.WorthAnnouncing(point.ComboLost))
                    cursor.FlashComboBreak();

                board?.FlashComboBreak(player);
                break;
            }
        }
    }

    /// <summary>Where the playhead was last frame, so a break can be spotted being crossed.</summary>
    private double? lastFlashCheck;

    /// <summary>
    /// This player's colour, spread evenly around the hue circle so neighbouring cursors are as
    /// distinguishable as the count allows. Full saturation on purpose — these are read against
    /// moving gameplay, not on a page.
    /// </summary>
    internal static Color4 ColourFor(int index, int count)
        => Color4.FromHsv(new Vector4((float)index / Math.Max(count, 1), 0.85f, 1, 1));

    /// <summary>The play's grade letter, or nothing when the replay never decoded.</summary>
    internal static string Rank(ReplayAttachment replay)
        => replay.Score == null ? string.Empty : replay.Score.ScoreInfo.Rank.ToString();
}
