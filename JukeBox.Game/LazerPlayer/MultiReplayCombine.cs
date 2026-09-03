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
    /// The cursors, indexed by replay. The FIRST is null — that player drives the chart itself and
    /// already has a cursor from the ruleset, so there is nothing extra of theirs to scale.
    /// </summary>
    private readonly List<osu.Game.Rulesets.Osu.UI.ReplayAnalysisOverlay?> cursors = new List<osu.Game.Rulesets.Osu.UI.ReplayAnalysisOverlay?>();

    /// <summary>Test hook: cursors actually mounted, which is one FEWER than the replay count — the
    /// first replay drives the chart itself and already has a cursor.</summary>
    internal int CursorsAttached { get; private set; }

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
        };

        simulator = new ReplaySimulator(osuFile, replays);

        var children = new List<Drawable> { chart, simulator };

        if (MultiReplayLayout.RateMismatchWarning(replays) is { } warning)
            children.Add(rateWarning(warning));

        InternalChildren = children.ToArray();
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
                           MultiReplayGrid.DisplayName(replay),
                           ColourFor(index, replays.Count),
                           simulator.Timelines[index]))
                       .ToList();

        AddInternal(board = new KnockoutBoard(entrants)
        {
            Anchor = Anchor.TopLeft,
            Origin = Anchor.TopLeft,
            Margin = new MarginPadding(6),
            Rules = rules,
        });
    }

    protected override void Update()
    {
        base.Update();

        buildBoardWhenReady();
        attachCursors();
        updateCursors();
    }

    /// <summary>
    /// Mounts everyone else's cursor onto the one chart. The ruleset only exists once the chart has
    /// finished its own async load, so this cannot happen in load(). Done once, then never again.
    /// </summary>
    private void attachCursors()
    {
        if (attached || chart.DrawableRuleset == null)
            return;

        attached = true;

        // The first player drives the chart, so their cursor comes from the ruleset itself and gets
        // a null slot here — the list stays indexed by replay so a cursor is never mismatched to
        // the wrong player's fate.
        cursors.Add(null);

        for (int i = 1; i < replays.Count; i++)
        {
            var replay = replays[i].Score?.Replay;
            var overlay = replay == null ? null : chart.AddCursorOverlay(replay, ColourFor(i, replays.Count));

            cursors.Add(overlay);

            if (overlay != null)
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

            bool stillIn = rules.AliveAt(timelines[i], time);

            cursor.Scale = new Vector2(stillIn ? scale : 1);
            cursor.Alpha = stillIn ? 1 : 0;
        }
    }

    private static Drawable rateWarning(string warning) => new Container
    {
        Anchor = Anchor.BottomCentre,
        Origin = Anchor.BottomCentre,
        AutoSizeAxes = Axes.Both,
        Margin = new MarginPadding { Bottom = 6 },
        Masking = true,
        CornerRadius = 4,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black.Opacity(0.75f) },
            new OsuSpriteText
            {
                Margin = new MarginPadding { Horizontal = 8, Vertical = 3 },
                Text = warning,
                Font = OsuFont.Torus.With(size: 13, weight: FontWeight.SemiBold),
                Colour = Color4.Orange,
            },
        },
    };

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
