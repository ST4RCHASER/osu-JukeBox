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
/// Everyone's replay over ONE chart: the beatmap is rendered once and every player's cursor is
/// drawn on top of it in their own colour, with a rail of names and accuracies down the left and
/// their combo and score down the right.
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
    private bool attached;

    /// <summary>Test hook: cursors actually mounted, which is one FEWER than the replay count — the
    /// first replay drives the chart itself and already has a cursor.</summary>
    internal int CursorsAttached { get; private set; }

    /// <summary>Test hook: the one hosted chart every cursor is drawn over.</summary>
    internal LazerChartLayer Chart => chart;

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

        var children = new List<Drawable> { chart, buildRail(Anchor.TopLeft), buildRail(Anchor.TopRight) };

        if (MultiReplayLayout.RateMismatchWarning(replays) is { } warning)
            children.Add(rateWarning(warning));

        InternalChildren = children.ToArray();
    }

    protected override void Update()
    {
        base.Update();

        // The ruleset only exists once the chart has finished its own async load, so the cursors
        // cannot be attached in load(). Done once, then never again.
        if (attached || chart.DrawableRuleset == null)
            return;

        attached = true;

        for (int i = 1; i < replays.Count; i++)
        {
            var replay = replays[i].Score?.Replay;

            if (replay != null && chart.AddCursorOverlay(replay, ColourFor(i, replays.Count)))
                CursorsAttached++;
        }
    }

    /// <summary>
    /// One player per row, sorted by score like the reference. The left rail carries who they are,
    /// the right what they did; both are the same rows in the same order, so a name lines up with
    /// its own numbers.
    /// </summary>
    private Drawable buildRail(Anchor side)
    {
        bool left = side == Anchor.TopLeft;

        var ordered = replays
                      .Select((replay, index) => (replay, colour: ColourFor(index, replays.Count)))
                      .OrderByDescending(r => r.replay.Score?.ScoreInfo.TotalScore ?? 0)
                      .ToList();

        var rows = new FillFlowContainer
        {
            Anchor = side,
            Origin = side,
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 1),
            Margin = new MarginPadding(6),
        };

        foreach (var (replay, colour) in ordered)
            rows.Add(left ? leftRow(replay, colour) : rightRow(replay));

        return rows;
    }

    private static Drawable leftRow(ReplayAttachment replay, Color4 colour) => new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both,
        Direction = FillDirection.Horizontal,
        Spacing = new Vector2(5, 0),
        Children = new Drawable[]
        {
            // The dot is the whole key to the picture: it is the ONLY thing tying a row to the
            // cursor weaving about on the playfield.
            new Circle
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Size = new Vector2(8),
                Colour = colour,
            },
            text(MultiReplayGrid.FormatAccuracy(replay), 12, FontWeight.SemiBold),
            text(Rank(replay), 12, FontWeight.Bold),
            text(MultiReplayGrid.DisplayName(replay), 12, FontWeight.Regular, colour),
        },
    };

    private static Drawable rightRow(ReplayAttachment replay) => new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both,
        Direction = FillDirection.Horizontal,
        Spacing = new Vector2(6, 0),
        Children = new Drawable[]
        {
            text(MultiReplayGrid.FormatCombo(replay), 12, FontWeight.SemiBold),
            text(MultiReplayGrid.FormatScore(replay), 12, FontWeight.Bold),
        },
    };

    private static OsuSpriteText text(string content, float size, FontWeight weight, Color4? colour = null) => new OsuSpriteText
    {
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        Text = content,
        Font = OsuFont.Torus.With(size: size, weight: weight),
        Colour = colour ?? Color4.White,
        Shadow = true,
    };

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
