#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using JukeBox.Game.Beatmaps;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace JukeBox.Game.Playback;

/// <summary>
/// Per-beatmapset audio offset (±250ms), persisted as a small JSON map (set id → ms) in app
/// storage. <see cref="CurrentOffset"/> always mirrors the offset of the set currently playing:
/// it reloads on every song change and persists user edits under that set's id (edits while
/// nothing plays affect only the session). The offset is consumed by BeatmapVisuals, which shifts
/// its whole visual clock (storyboard + chart + hitsound timing) relative to the track — positive
/// values run the visuals earlier, compensating audio that sounds late.
/// </summary>
public partial class BeatmapOffsetStore : Component
{
    private const string file_name = "beatmap-offsets.json";

    public readonly BindableDouble CurrentOffset = new BindableDouble { MinValue = -250, MaxValue = 250, Precision = 1 };

    private readonly Dictionary<int, double> offsets = new Dictionary<int, double>();
    private readonly Bindable<CachedBeatmapSet?> currentSong = new Bindable<CachedBeatmapSet?>();

    private Storage storage = null!;

    // Test seam: storage override (defaults to the host's app storage).
    private readonly Storage? customStorage;

    public BeatmapOffsetStore(Storage? storage = null)
    {
        customStorage = storage;
    }

    // Set while CurrentOffset is being repointed at another set's stored value, so the
    // value-changed persistence callback doesn't write set A's offset under set B's id.
    private bool switching;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [BackgroundDependencyLoader]
    private void load(GameHost host)
    {
        storage = customStorage ?? host.Storage;

        try
        {
            if (!storage.Exists(file_name))
                return;

            using var stream = storage.GetStream(file_name);

            if (stream != null)
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<int, double>>(stream);

                if (loaded != null)
                {
                    foreach (var pair in loaded)
                        offsets[pair.Key] = Math.Clamp(pair.Value, -250, 250);
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Failed to read {file_name} — starting with no per-beatmap offsets");
        }
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        currentSong.BindTo(playback.Current);
        currentSong.BindValueChanged(e =>
        {
            switching = true;
            CurrentOffset.Value = e.NewValue != null ? GetOffset(e.NewValue.SetId) : 0;
            switching = false;
        }, true);

        CurrentOffset.BindValueChanged(e =>
        {
            if (switching || currentSong.Value == null)
                return;

            offsets[currentSong.Value.SetId] = e.NewValue;
            save();
        });
    }

    public double GetOffset(int setId) => offsets.TryGetValue(setId, out double value) ? value : 0;

    private void save()
    {
        try
        {
            using var stream = storage.CreateFileSafely(file_name);
            JsonSerializer.Serialize(stream, offsets);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Failed to write {file_name}");
        }
    }
}
