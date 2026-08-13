#nullable enable

using System;
using System.Collections.Generic;
using osu.Framework.Audio;
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
/// </summary>
public class LazerRulesetConfigCache : IRulesetConfigCache, IDisposable
{
    private readonly RealmAccess realm;
    private readonly Dictionary<string, IRulesetConfigManager?> configs = new Dictionary<string, IRulesetConfigManager?>();

    public LazerRulesetConfigCache(RealmAccess realm)
    {
        this.realm = realm;
    }

    public IRulesetConfigManager? GetConfigFor(Ruleset ruleset)
    {
        lock (configs)
        {
            if (!configs.TryGetValue(ruleset.ShortName, out var config))
                configs[ruleset.ShortName] = config = ruleset.CreateConfig(new SettingsStore(realm));

            return config;
        }
    }

    public void Dispose()
    {
        lock (configs)
        {
            foreach (var config in configs.Values)
                (config as IDisposable)?.Dispose();

            configs.Clear();
        }
    }
}
