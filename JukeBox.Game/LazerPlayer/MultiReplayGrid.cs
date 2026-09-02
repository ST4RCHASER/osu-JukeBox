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
/// Several people's replays of one beatmap, side by side — the tournament-style grid: each cell is
/// its own full gameplay render of the same map, playing that person's replay, with their score,
/// accuracy, combo and name over it.
///
/// <para>
/// Every cell shares ONE clock, which is what keeps them honest: the grid is a comparison, and a
/// comparison where the cells run on separate clocks is worthless. Seeking, pausing and rate all
/// reach every cell at once because none of them owns a clock — they inherit this drawable's, which
/// is the app's playback clock.
/// </para>
///
/// <para>
/// Visual mods differ per cell (each replay renders under the mods it was played with); SPEED
/// cannot, because there is one audio track. See
/// <see cref="MultiReplayLayout.RatesAgree"/> for what happens when the replays disagree.
/// </para>
/// </summary>
public partial class MultiReplayGrid : CompositeDrawable
{
    private readonly string osuFile;
    private readonly IReadOnlyList<ReplayAttachment> replays;

    private readonly List<LazerChartLayer> cells = new List<LazerChartLayer>();

    /// <summary>Test hook: the gameplay layers actually built, one per rendered replay.</summary>
    internal IReadOnlyList<LazerChartLayer> Cells => cells;

    /// <summary>Test hook: the grid shape chosen for the replay count.</summary>
    internal GridShape Shape { get; private set; }

    /// <summary>
    /// Whether the cells make any sound. Only ONE cell may: N gameplay layers hitting the same
    /// samples a few milliseconds apart is not N times louder, it is a flam. The first cell keeps
    /// the audio and the rest are silent.
    /// </summary>
    public bool HitSoundsEnabled
    {
        set
        {
            for (int i = 0; i < cells.Count; i++)
                cells[i].HitSoundsEnabled.Value = value && i == 0;
        }
    }

    /// <param name="osuFile">The one difficulty every replay was played on.</param>
    /// <param name="replays">The replays, in drop order. Beyond
    /// <see cref="MultiReplayLayout.MAX_GRID_CELLS"/> the extras are left unrendered.</param>
    public MultiReplayGrid(string osuFile, IReadOnlyList<ReplayAttachment> replays)
    {
        this.osuFile = osuFile;
        this.replays = replays;

        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        int rendered = MultiReplayLayout.RenderedCount(replays.Count);
        Shape = MultiReplayLayout.For(rendered);

        var content = new Drawable[Shape.Rows][];

        for (int row = 0; row < Shape.Rows; row++)
        {
            content[row] = new Drawable[Shape.Columns];

            for (int column = 0; column < Shape.Columns; column++)
            {
                int index = row * Shape.Columns + column;

                content[row][column] = index < rendered
                    ? buildCell(replays[index], index)
                    : Empty();
            }
        }

        var grid = new GridContainer
        {
            RelativeSizeAxes = Axes.Both,
            Content = content,
        };

        var children = new List<Drawable> { grid };

        if (MultiReplayLayout.RateMismatchWarning(replays) is { } warning)
            children.Add(buildRateWarning(warning));

        InternalChildren = children.ToArray();
    }

    /// <summary>
    /// One cell: a whole gameplay render with the player's numbers over it, laid out the way the
    /// reference does — score and accuracy top-centre, combo bottom-left, name bottom-right.
    /// </summary>
    private Drawable buildCell(ReplayAttachment replay, int index)
    {
        // A FRESH working beatmap per cell rather than one shared instance: each cell converts the
        // beatmap under its own replay's mods, and lazer's WorkingBeatmap caches that conversion —
        // sharing one would have the cells racing to cache different conversions of the same map
        // during async load.
        var working = new FlatWorkingBeatmap(osuFile);

        var layer = new LazerChartLayer(working, osuFile, replay.Score) { AlwaysPresent = true };
        cells.Add(layer);

        return new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black },
                layer,
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(6),
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 1),
                            Children = new Drawable[]
                            {
                                label(FormatScore(replay), 20, FontWeight.Bold, Anchor.TopCentre),
                                label(FormatAccuracy(replay), 13, FontWeight.Regular, Anchor.TopCentre),
                            },
                        },
                        label(FormatCombo(replay), 18, FontWeight.Bold, Anchor.BottomLeft),
                        label(DisplayName(replay), 15, FontWeight.SemiBold, Anchor.BottomRight),
                    },
                },
            },
        };
    }

    private static OsuSpriteText label(string text, float size, FontWeight weight, Anchor anchor) => new OsuSpriteText
    {
        Anchor = anchor,
        Origin = anchor,
        Text = text,
        Font = OsuFont.Torus.With(size: size, weight: weight),
        Colour = Color4.White,
        // The cells are real gameplay, so the text sits over moving colour — a shadow is what keeps
        // it readable rather than a scrim that would hide the very thing being compared.
        Shadow = true,
    };

    private Drawable buildRateWarning(string warning) => new Container
    {
        Anchor = Anchor.TopCentre,
        Origin = Anchor.TopCentre,
        AutoSizeAxes = Axes.Both,
        Margin = new MarginPadding { Top = 4 },
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

    /// <summary>The replay's final score, zero-padded the way osu!'s own scoreboards show it.</summary>
    internal static string FormatScore(ReplayAttachment replay)
        => (replay.Score?.ScoreInfo.TotalScore ?? 0).ToString("00000000");

    /// <summary>Final accuracy as a percentage. Replays that never decoded show nothing rather than a lie.</summary>
    internal static string FormatAccuracy(ReplayAttachment replay)
        => replay.Score == null ? string.Empty : (replay.Score.ScoreInfo.Accuracy * 100).ToString("0.00") + "%";

    /// <summary>The replay's max combo, in osu!'s "1234x" form.</summary>
    internal static string FormatCombo(ReplayAttachment replay)
        => replay.Score == null ? string.Empty : replay.Score.ScoreInfo.MaxCombo.ToString("N0") + "x";

    /// <summary>The player, with their mods when they used any — "WhiteCat +HDDT".</summary>
    internal static string DisplayName(ReplayAttachment replay)
    {
        string name = replay.PlayerName.Length > 0 ? replay.PlayerName : "unknown";

        return replay.ModAcronyms.Count > 0 ? $"{name} +{string.Join(string.Empty, replay.ModAcronyms)}" : name;
    }
}
