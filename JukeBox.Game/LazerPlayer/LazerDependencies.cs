#nullable enable

using System;
using System.Collections.Generic;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Configuration;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Minimal <see cref="IStorageResourceProvider"/> for constructing lazer skins outside
/// OsuGameBase: renderer/audio/texture-loading from the framework host, resources from the game
/// resource store (which includes osu.Game.Resources — see <see cref="JukeBoxGameBase"/>), and
/// the shared realm instance. "Files" (realm-imported skin/beatmap files) is intentionally empty —
/// nothing we build is realm-backed.
/// </summary>
public class LazerResourceProvider : IStorageResourceProvider
{
    private readonly GameHost host;

    public LazerResourceProvider(GameHost host, AudioManager audio, IResourceStore<byte[]> resources, RealmAccess realm)
    {
        this.host = host;
        AudioManager = audio;
        Resources = resources;
        RealmAccess = realm;
    }

    public IRenderer Renderer => host.Renderer;
    public AudioManager? AudioManager { get; }
    public IResourceStore<byte[]> Files { get; } = new ResourceStore<byte[]>();
    public IResourceStore<byte[]> Resources { get; }
    public RealmAccess RealmAccess { get; }

    public IResourceStore<TextureUpload>? CreateTextureLoaderStore(IResourceStore<byte[]> underlyingStore)
        => host.CreateTextureLoaderStore(underlyingStore);
}

/// <summary>
/// Standalone replacement for lazer's realm/RulesetStore-coupled <c>RulesetConfigCache</c>:
/// one config manager per ruleset, settings persisted through the shared realm instance
/// (<see cref="osu.Game.Rulesets.Configuration.RulesetConfigManager{T}"/> stores its values as
/// realm objects) — required non-null by <c>DrawableRulesetDependencies</c>.
///
/// Mirrors lazer's own <c>RulesetConfigCache</c> exactly in shape: a <see cref="Component"/> that
/// pre-populates every config in <see cref="LoadComplete"/>, which is guaranteed to run on the
/// update thread. This matters: RulesetConfigManager reads <c>RealmAccess.Realm</c> while
/// loading, and that getter hard-throws off the update thread — lazy creation inside
/// <see cref="GetConfigFor"/> (or eager creation in the game's async BDL) crashes the real
/// MultiThreaded app, which headless single-threaded tests masked.
/// </summary>
public partial class LazerRulesetConfigCache : Component, IRulesetConfigCache
{
    private readonly RealmAccess realm;
    private readonly Dictionary<string, IRulesetConfigManager?> configs = new Dictionary<string, IRulesetConfigManager?>();

    public LazerRulesetConfigCache(RealmAccess realm)
    {
        this.realm = realm;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        var settings = new SettingsStore(realm);

        foreach (var ruleset in new Ruleset[]
                 {
                     new osu.Game.Rulesets.Osu.OsuRuleset(),
                     new osu.Game.Rulesets.Taiko.TaikoRuleset(),
                     new osu.Game.Rulesets.Catch.CatchRuleset(),
                     new osu.Game.Rulesets.Mania.ManiaRuleset(),
                 })
        {
            configs[ruleset.ShortName] = ruleset.CreateConfig(settings);
        }
    }

    public IRulesetConfigManager? GetConfigFor(Ruleset ruleset)
    {
        // Same guard as lazer's RulesetConfigCache: resolving before load would mean a silently
        // missing config (and an unexplained DI failure later, e.g. mania's scroll config).
        if (!IsLoaded)
            throw new InvalidOperationException($"Cannot retrieve {nameof(IRulesetConfigManager)} before {nameof(LazerRulesetConfigCache)} has loaded");

        // Unknown rulesets (none are bundled beyond the four above) get no config rather than a
        // lazy off-thread creation that would trip realm's update-thread affinity.
        return configs.TryGetValue(ruleset.ShortName, out var config) ? config : null;
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);

        // ensures any potential database operations are finalised before game destruction.
        foreach (var config in configs.Values)
            (config as IDisposable)?.Dispose();

        configs.Clear();
    }
}
