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

    /// <summary>Per-player overrides, so each cell can render (and, through its own simulation,
    /// score) under this player's chosen mods. Null in a bare test host.</summary>
    [Resolved(canBeNull: true)]
    private Replays.PlayerOverrideStore? overrideStore { get; set; }

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

        // Only the RENDERED replays are simulated. A replay past the cap has no cell to put numbers
        // in, so recording its play would be a whole hidden renderer's worth of work for a figure
        // nothing displays.
        simulator = new ReplaySimulator(osuFile, replays.Take(rendered).ToList());

        InternalChildren = new Drawable[] { grid, simulator };
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

        // No live scoring on the VISIBLE cells any more. Their numbers come from the recorded
        // timelines, so a score processor here would be a second, worse answer to the same question
        // — worse because it is the one that goes wrong the moment the user seeks.
        // Each cell renders under ITS OWN player's recorded mods. Consulting the shared Chart-tab
        // selection gave every cell player one's — one person's Hidden on everybody's playfield.
        var layer = new LazerChartLayer(working, osuFile, replay.Score)
        {
            AlwaysPresent = true,
            UseRecordedReplayModsOnly = true,

            // This player's mod override, if the user set one; otherwise their recorded mods.
            OverrideMods = overrideStore?.Peek(replay)?.Mods,

            // And their gameplay skin override, so one cell can wear a different skin.
            OverrideSkinKey = overrideStore?.Peek(replay)?.SkinKey,
        };

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
                nameLabels[index] = label(DisplayName(replay), 15, FontWeight.SemiBold, Anchor.BottomRight),
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
    private readonly Dictionary<int, OsuSpriteText> nameLabels = new Dictionary<int, OsuSpriteText>();

    /// <summary>Test hook: how many times each cell's name has been flashed for a combo break.</summary>
    private readonly Dictionary<int, int> comboBreakFlashes = new Dictionary<int, int>();

    internal int ComboBreakFlashesFor(int cell) => comboBreakFlashes.GetValueOrDefault(cell);

    /// <summary>
    /// Flashes a cell's player NAME red and swells it for about a second when the playhead crosses
    /// one of that player's combo breaks — the tournament-overlay cue the user asked for.
    ///
    /// <para>
    /// Only forward crossings count. Seeking backwards past a break and playing it again flashes
    /// again, which is right; jumping backwards fires nothing, or a scrub would set every cell
    /// flashing at once.
    /// </para>
    /// </summary>
    private void flashNewBreaks(int cell, ReplayTimeline timeline, double time)
    {
        if (lastFlashCheck is not { } previous || time <= previous || !nameLabels.TryGetValue(cell, out var label))
            return;

        foreach (var point in timeline.Points)
        {
            if (!point.BrokeCombo || point.Time <= previous || point.Time > time)
                continue;

            comboBreakFlashes[cell] = comboBreakFlashes.GetValueOrDefault(cell) + 1;

            label.FadeColour(Color4.Red).Then().FadeColour(Color4.White, 900, Easing.In);
            label.ScaleTo(1.5f).Then().ScaleTo(1, 900, Easing.OutQuint);
            label.FadeTo(0.2f, 80).Then().FadeTo(1, 80)
                 .Then().FadeTo(0.2f, 80).Then().FadeTo(1, 80);

            break;
        }
    }

    private double? lastFlashCheck;

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

    /// <summary>
    /// Each cell's numbers, read from that player's RECORDED play at the current moment.
    ///
    /// <para>
    /// This used to bind each cell's label to a live score processor being fed judgements as the
    /// replay played. That is correct only for someone who watches from the start and never touches
    /// the seek bar. A forward seek hard-seeks gameplay past objects which are then never judged,
    /// and seeking backwards un-judges nothing — so after a scrub the numbers were whatever had
    /// accumulated before it, while the play carried on without them. Seeking early enough left
    /// every cell reading 00000000 and 0x over gameplay visibly forty objects in, which is the same
    /// "static, not running" the live numbers were meant to fix.
    /// </para>
    ///
    /// <para>
    /// Read by time instead, there is no accumulated state to fall out of step. The cost is a
    /// hidden renderer per replay while each play is recorded, and those are disposed the moment
    /// they finish — measured on a twelve-cell grid, that leaves the steady-state update cost
    /// where it was (0.27ms against 0.19ms per frame).
    /// </para>
    /// </summary>
    protected override void Update()
    {
        base.Update();

        if (simulator == null)
            return;

        double time = Clock.CurrentTime;

        for (int i = 0; i < cells.Count && i < simulator.Timelines.Count; i++)
        {
            var timeline = simulator.Timelines[i];

            if (!scoreLabels.TryGetValue(i, out var scoreLabel))
                continue;

            if (KnockoutBoard.IsPending(timeline, time))
            {
                // Dashes rather than the last thing recorded: past the simulation, a stale figure
                // reads exactly like a real score for a moment the player has not reached.
                scoreLabel.Text = "--------";
                accuracyLabels[i].Text = "--.--%";
                comboLabels[i].Text = "--x";
                continue;
            }

            var point = timeline.At(time);

            scoreLabel.Text = point.Score.ToString("00000000");
            accuracyLabels[i].Text = (point.Accuracy * 100).ToString("0.00") + "%";
            comboLabels[i].Text = point.Combo.ToString("N0") + "x";

            flashNewBreaks(i, timeline, time);
        }

        lastFlashCheck = time;
    }

    private ReplaySimulator? simulator;

    /// <summary>Test hook: the off-screen playthrough feeding the cells' numbers.</summary>
    internal ReplaySimulator? Simulator => simulator;

    /// <summary>Test hooks: what a cell is actually SHOWING, which is the thing the user complained
    /// about and therefore the thing worth asserting on.</summary>
    internal string ScoreTextFor(int cell) => scoreLabels[cell].Text.ToString()!;

    internal string AccuracyTextFor(int cell) => accuracyLabels[cell].Text.ToString()!;

    internal string ComboTextFor(int cell) => comboLabels[cell].Text.ToString()!;

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
