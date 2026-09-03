#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
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

    /// <param name="set">The cached set, for the background image and storyboard each cell draws.</param>
    /// <param name="osuFile">The one difficulty every replay was played on.</param>
    /// <param name="replays">The replays, in drop order. Beyond
    /// <see cref="MultiReplayLayout.MAX_GRID_CELLS"/> the extras are left unrendered.</param>
    public MultiReplayGrid(CachedBeatmapSet set, string osuFile, IReadOnlyList<ReplayAttachment> replays)
    {
        this.set = set;
        this.osuFile = osuFile;
        this.replays = replays;

        RelativeSizeAxes = Axes.Both;
    }

    [Resolved]
    private GameHost host { get; set; } = null!;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    private readonly CachedBeatmapSet set;

    private readonly BindableDouble backgroundDim = new BindableDouble();
    private readonly Bindable<bool> showStoryboard = new Bindable<bool>(true);
    private readonly Bindable<bool> showVideo = new Bindable<bool>(true);

    /// <summary>
    /// The set's background, loaded ONCE and drawn by every cell. One texture and N quads is what
    /// makes the background unconditional — loading it per cell would be N decodes of the same
    /// megabyte-ish JPEG for no visual difference.
    /// </summary>
    private Texture? backgroundTexture;

    private TextureStore? backgroundTextures;

    private readonly List<LazerStoryboardLayer> storyboards = new List<LazerStoryboardLayer>();

    /// <summary>Test hook: how many cells actually carry a storyboard/video layer.</summary>
    internal int StoryboardCells => storyboards.Count;

    /// <summary>
    /// Test hook: the one background texture every cell draws. Identity matters — a test counting
    /// merely "sprites with a texture" would be satisfied by the skin sprites inside the gameplay
    /// layers and pass with no background at all.
    /// </summary>
    internal Texture? SharedBackgroundTexture => backgroundTexture;

    [BackgroundDependencyLoader]
    private void load()
    {
        config?.BindWith(JukeBoxSetting.BackgroundDim, backgroundDim);
        config?.BindWith(JukeBoxSetting.ShowStoryboard, showStoryboard);
        config?.BindWith(JukeBoxSetting.ShowVideo, showVideo);

        loadBackgroundTexture();

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

        var layer = new LazerChartLayer(working, osuFile, replay.Score) { AlwaysPresent = true, TrackLiveScore = true };
        cells.Add(layer);

        // A cell is the whole visual stack, not just gameplay: the map's own background, the dim
        // over it, its storyboard/video, then the play. It used to be a black box with gameplay on
        // top, which is what "all black" was — nothing was ever drawn behind the notes.
        var content = new List<Drawable>();

        if (backgroundTexture != null)
        {
            content.Add(new Sprite
            {
                RelativeSizeAxes = Axes.Both,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                FillMode = FillMode.Fill,
                Texture = backgroundTexture,
                Colour = new Colour4(0.7f, 0.7f, 0.7f, 1f),
            });
        }

        // Dim belongs UNDER the storyboard and gameplay, exactly as the single-chart stack has it,
        // and the cells need it — the numbers are read against the map's own art.
        var dim = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black };
        dim.Alpha = (float)backgroundDim.Value;
        cellDims.Add(dim);
        content.Add(dim);

        // All cells or none. Giving the first four a storyboard and the rest a bare background
        // would make the cells visually inconsistent, which is worse for a COMPARISON than all of
        // them being plain.
        if (MultiReplayLayout.StoryboardsInEveryCell(replays.Count))
        {
            var storyboard = new LazerStoryboardLayer(set, osuFile);

            storyboard.StoryboardShown.BindTo(showStoryboard);
            storyboard.VideoShown.BindTo(showVideo);

            storyboards.Add(storyboard);

            // Only the SOUNDING cell's storyboard samples play, for the same reason only one cell's
            // hitsounds do: N copies of the same keysound a few milliseconds apart is a flam.
            content.Add(index == 0
                ? storyboard
                : new AudioContainer { RelativeSizeAxes = Axes.Both, Volume = { Value = 0 }, Child = storyboard });
        }

        content.Add(layer);

        content.Add(new Container
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
                        scoreLabels[index] = label(FormatScore(replay), 20, FontWeight.Bold, Anchor.TopCentre),
                        accuracyLabels[index] = label(FormatAccuracy(replay), 13, FontWeight.Regular, Anchor.TopCentre),
                    },
                },
                comboLabels[index] = label(FormatCombo(replay), 18, FontWeight.Bold, Anchor.BottomLeft),
                label(DisplayName(replay), 15, FontWeight.SemiBold, Anchor.BottomRight),
            },
        });

        return new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            Children = content.ToArray(),
        };
    }

    private readonly List<Box> cellDims = new List<Box>();

    private readonly Dictionary<int, OsuSpriteText> scoreLabels = new Dictionary<int, OsuSpriteText>();
    private readonly Dictionary<int, OsuSpriteText> accuracyLabels = new Dictionary<int, OsuSpriteText>();
    private readonly Dictionary<int, OsuSpriteText> comboLabels = new Dictionary<int, OsuSpriteText>();

    private void loadBackgroundTexture()
    {
        if (set.BackgroundFile == null)
            return;

        try
        {
            backgroundTextures = new TextureStore(host.Renderer,
                host.CreateTextureLoaderStore(new StorageBackedResourceStore(new NativeStorage(set.Directory, host))),
                useAtlas: false, scaleAdjust: 1);

            string relative = Path.GetRelativePath(set.Directory, set.BackgroundFile).Replace('\\', '/');
            backgroundTexture = backgroundTextures.Get(relative);
        }
        catch (Exception e)
        {
            // A missing or unreadable background costs the cells their art, nothing more.
            Logger.Log($"Multi-replay grid could not load the background: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        backgroundDim.BindValueChanged(e =>
        {
            foreach (var dim in cellDims)
                dim.Alpha = (float)e.NewValue;
        }, true);
    }

    protected override void Update()
    {
        base.Update();

        // Each cell's processor only exists once its ruleset has finished loading, so the binding
        // is done here rather than at build. Once bound, the numbers follow the judgements — and
        // therefore the shared clock — with no further work from this class.
        for (int i = 0; i < cells.Count; i++)
        {
            if (liveBound.Contains(i) || cells[i].LiveScore is not { } processor)
                continue;

            liveBound.Add(i);

            // Copied per iteration, deliberately: a `for` loop's variable is ONE variable shared by
            // every closure made in it, so binding against `i` directly would have all three
            // callbacks looking up cells.Count once the loop finished — which is not a cell.
            int cell = i;

            processor.TotalScore.BindValueChanged(e => scoreLabels[cell].Text = ((long)e.NewValue).ToString("00000000"), true);
            processor.Accuracy.BindValueChanged(e => accuracyLabels[cell].Text = (e.NewValue * 100).ToString("0.00") + "%", true);
            processor.Combo.BindValueChanged(e => comboLabels[cell].Text = e.NewValue.ToString("N0") + "x", true);
        }
    }

    private readonly HashSet<int> liveBound = new HashSet<int>();

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
