#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Hosts osu!lazer's REAL gameplay renderer (<see cref="DrawableRuleset"/>) for one difficulty,
/// driven by autoplay and our shared playback clock — 100% authentic osu! gameplay visuals for
/// all four rulesets, replacing the old hand-rolled chart renderers.
///
/// The heavy machinery lazer normally wires through <c>OsuGameBase</c>/<c>Player</c> is provided
/// here in its minimal standalone form, mirroring what lazer's own DrawableRuleset test scenes
/// cache: the game-level pieces (<c>RealmAccess</c>, <c>OsuConfigManager</c>,
/// <c>IRulesetConfigCache</c>, <c>OsuColour</c>, <c>IStorageResourceProvider</c>) come from
/// <see cref="JukeBoxGameBase"/>, while the per-beatmap pieces (mods list, skin chain, sample
/// gating) are cached by this layer.
/// </summary>
public partial class LazerChartLayer : CompositeDrawable, IBeatSyncProvider
{
    private readonly WorkingBeatmap working;
    private readonly string osuFile;

    private DrawableRuleset? drawableRuleset;
    private Score? autoplayScore;
    private IReadOnlyList<Mod> mods = Array.Empty<Mod>();
    private IBeatmap? playableBeatmap;

    private readonly List<IDisposable> ownedSkins = new List<IDisposable>();

    /// <summary>
    /// Gates lazer's own hitsound/keysound playback (the samples DrawableHitObjects play through
    /// the skin chain). Sample playback is additionally suppressed while the frame-stable clock is
    /// catching up after a seek, exactly like lazer's own gameplay clock does — otherwise every
    /// object skipped over during the catch-up would fire its hitsound.
    /// </summary>
    public readonly BindableBool HitSoundsEnabled = new BindableBool();

    private readonly BindableBool samplePlaybackDisabled = new BindableBool(true);

    /// <summary>The ruleset instance rendering this difficulty (test hook; assigned during load).</summary>
    internal Ruleset? Ruleset { get; private set; }

    /// <summary>Number of hit objects in the playable (converted) beatmap, 0 until loaded (test hook).</summary>
    internal int ObjectCount => playableBeatmap?.HitObjects.Count ?? 0;

    /// <summary>The hosted lazer DrawableRuleset (test hook).</summary>
    internal DrawableRuleset? DrawableRuleset => drawableRuleset;

    private DependencyContainer dependencies = null!;

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        => dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

    // IBeatSyncProvider (auto-cached to children via the interface's [Cached] attribute): kiai-
    // synced skin pieces (BeatSyncedContainer) beat off the playable beatmap's control points and
    // the same frame-stable clock the gameplay runs on. No audio amplitudes available — our track
    // playback lives outside lazer.
    ControlPointInfo? IBeatSyncProvider.ControlPoints => playableBeatmap?.ControlPointInfo;
    IClock IBeatSyncProvider.Clock => drawableRuleset?.FrameStableClock ?? Clock;
    ChannelAmplitudes IHasAmplitudes.CurrentAmplitudes => ChannelAmplitudes.Empty;

    /// <param name="working">The decoded difficulty to render. Must contain at least one hit object.</param>
    /// <param name="osuFile">Absolute path of the .osu file (locates the beatmap folder for
    /// beatmap-provided skin elements and hitsound samples).</param>
    public LazerChartLayer(WorkingBeatmap working, string osuFile)
    {
        this.working = working;
        this.osuFile = osuFile;

        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load(GameHost host, AudioManager audio, IStorageResourceProvider resourceProvider)
    {
        var ruleset = createRuleset(working.BeatmapInfo.Ruleset.OnlineID);
        Ruleset = ruleset;

        var autoplay = ruleset.GetAutoplayMod()
                       ?? throw new InvalidOperationException($"{ruleset.ShortName} provides no autoplay mod");
        mods = new Mod[] { autoplay };

        playableBeatmap = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);

        // Per-beatmap dependencies for the DrawableRuleset subtree, mirroring lazer's Player:
        // the active mods list, and the sample-playback gate (lazer's GameplayClockContainer
        // normally provides the latter — ours additionally implements the hitsound setting).
        dependencies.CacheAs(mods);
        dependencies.CacheAs<ISamplePlaybackDisabler>(new SamplePlaybackGate(samplePlaybackDisabled));

        drawableRuleset = ruleset.CreateDrawableRulesetWith(playableBeatmap, mods);

        var replay = autoplay.CreateReplayData(playableBeatmap, mods);
        autoplayScore = new Score { Replay = replay.Replay };

        // Skin chain in lazer's lookup order: beatmap-folder skin (beatmap-provided elements,
        // combo colours from the .osu [Colours] section and custom hitsound samples), then the
        // lazer default skin (Argon), then the classic legacy skin as the final legacy-name
        // fallback — the same stack RulesetSkinProvidingContainer builds, minus the realm-backed
        // SkinManager it resolves (we have no realm-imported skins to offer anyway).
        ISkin? beatmapSkin = null;

        try
        {
            var folderSkin = new BeatmapFolderSkin(osuFile, resourceProvider, host);
            ownedSkins.Add(folderSkin);
            beatmapSkin = folderSkin;
        }
        catch (Exception e)
        {
            osu.Framework.Logging.Logger.Error(e, $"Failed to load beatmap folder skin for '{osuFile}' — continuing with default skin only");
        }

        var argon = new ArgonSkin(resourceProvider);
        var classic = new DefaultLegacySkin(resourceProvider);
        var rulesetResources = new ResourceStoreBackedSkin(ruleset.CreateResourceStore(), host, audio);
        ownedSkins.Add(argon);
        ownedSkins.Add(classic);
        ownedSkins.Add(rulesetResources);

        InternalChild = new LazerSkinProvider(ruleset, playableBeatmap, beatmapSkin, new ISkin[] { argon, classic }, rulesetResources)
        {
            RelativeSizeAxes = Axes.Both,
            Child = drawableRuleset,
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Same timing as lazer's ReplayPlayer.PrepareReplay: the ruleset's input manager and
        // frame-stability container exist once loaded, so the replay can be attached now. From
        // here the autoplay replay drives gameplay entirely off our inherited playback clock.
        if (autoplayScore != null)
            drawableRuleset?.SetReplayScore(autoplayScore);
    }

    protected override void Update()
    {
        base.Update();

        samplePlaybackDisabled.Value =
            !HitSoundsEnabled.Value || drawableRuleset?.FrameStableClock.IsCatchingUp.Value == true;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);

        foreach (var skin in ownedSkins)
            skin.Dispose();
        ownedSkins.Clear();
    }

    private static Ruleset createRuleset(int onlineId) => onlineId switch
    {
        1 => new TaikoRuleset(),
        2 => new CatchRuleset(),
        3 => new ManiaRuleset(),
        _ => new OsuRuleset(),
    };

    /// <summary>
    /// Trivial <see cref="ISamplePlaybackDisabler"/> handing lazer's PausableSkinnableSound a
    /// bindable we control from <see cref="Update"/>.
    /// </summary>
    private class SamplePlaybackGate : ISamplePlaybackDisabler
    {
        private readonly IBindable<bool> disabled;

        public SamplePlaybackGate(IBindable<bool> disabled)
        {
            this.disabled = disabled;
        }

        public IBindable<bool> SamplePlaybackDisabled => disabled;
    }

    /// <summary>
    /// The beatmap folder as a legacy skin: beatmap-provided skin textures, custom hitsound
    /// samples, and the .osu's own [Colours] section as skin configuration — the standalone
    /// equivalent of lazer's realm-backed <see cref="LegacyBeatmapSkin"/>.
    /// </summary>
    private class BeatmapFolderSkin : LegacySkin
    {
        public BeatmapFolderSkin(string osuFile, IStorageResourceProvider resources, GameHost host)
            : base(
                new SkinInfo { Name = Path.GetFileName(osuFile) },
                resources,
                new StorageBackedResourceStore(new NativeStorage(Path.GetDirectoryName(osuFile)!, host)),
                // LegacySkin's configuration parser accepts a .osu file (same trick as
                // LegacyBeatmapSkin) — combo colours et al come from the beatmap itself.
                Path.GetFileName(osuFile))
        {
            // Known deviation from LegacyBeatmapSkin: its AllowDefaultComboColoursFallback=false
            // is internal to osu.Game, so a beatmap with no [Colours] section uses the classic
            // default combo colours here instead of the active skin's own palette.
        }
    }

    /// <summary>
    /// A standalone stand-in for lazer's <c>RulesetSkinProvidingContainer</c>: every skin source
    /// wrapped in the ruleset's own skin transformer (legacy/argon per-ruleset adaptations), plus
    /// the ruleset's bundled resources as the final source — minus the realm-backed SkinManager
    /// lookup that class requires.
    /// </summary>
    private partial class LazerSkinProvider : SkinProvidingContainer
    {
        // Matches RulesetSkinProvidingContainer: sources are complete here; never consult parents.
        protected override bool AllowFallingBackToParent => false;

        public LazerSkinProvider(Ruleset ruleset, IBeatmap beatmap, ISkin? beatmapSkin, IEnumerable<ISkin> userSkins, ISkin rulesetResources)
        {
            var sources = new List<ISkin>();

            if (beatmapSkin != null)
                sources.Add(transform(ruleset, beatmap, beatmapSkin));

            sources.AddRange(userSkins.Select(s => transform(ruleset, beatmap, s)));
            sources.Add(rulesetResources);

            SetSources(sources);
        }

        private static ISkin transform(Ruleset ruleset, IBeatmap beatmap, ISkin skin)
            => ruleset.CreateSkinTransformer(skin, beatmap) ?? skin;
    }
}
