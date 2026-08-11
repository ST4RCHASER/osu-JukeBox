#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;

namespace JukeBox.Game.Beatmaps;

public class BeatmapCache
{
    private readonly string root;
    private readonly IBeatmapMirror mirror;
    private readonly bool noVideo;
    private readonly ConcurrentDictionary<int, Task<CachedBeatmapSet>> inflight = new();

    private static readonly EnumerationOptions osu_enum_options = new() { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive };

    public BeatmapCache(string rootDirectory, IBeatmapMirror mirror, bool noVideo = false)
    {
        root = rootDirectory;
        this.mirror = mirror;
        this.noVideo = noVideo;
    }

    public bool IsCached(int setId) => hasOsuFiles(Path.Combine(root, setId.ToString()));

    private static bool hasOsuFiles(string dir)
        => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.osu", osu_enum_options).Any();

    public Task<CachedBeatmapSet> GetAsync(int setId, CancellationToken ct = default)
        => inflight.GetOrAdd(setId, id => getInternal(id, ct));

    private async Task<CachedBeatmapSet> getInternal(int setId, CancellationToken ct)
    {
        try
        {
            string dir = Path.Combine(root, setId.ToString());
            if (IsCached(setId))
                return LoadFromDirectory(setId, dir);

            string tmpOsz = Path.Combine(root, $"{setId}.osz.part");
            Directory.CreateDirectory(root);
            await using (var fs = File.Create(tmpOsz))
                await mirror.DownloadAsync(setId, noVideo, fs, ct).ConfigureAwait(false);
            string tmpDir = dir + ".extracting";
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            ZipFile.ExtractToDirectory(tmpOsz, tmpDir);
            File.Delete(tmpOsz);
            Directory.Move(tmpDir, dir);
            return LoadFromDirectory(setId, dir);
        }
        finally { inflight.TryRemove(setId, out _); }
    }

    public CachedBeatmapSet LoadFromDirectory(int setId, string dir)
    {
        string[] osuFiles = Directory.EnumerateFiles(dir, "*.osu", osu_enum_options)
                                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                      .ToArray();

        var set = new CachedBeatmapSet
        {
            SetId = setId,
            Directory = Path.GetFullPath(dir),
            OsuFiles = osuFiles.ToList(),
            OsbFile = Directory.EnumerateFiles(dir, "*.osb", osu_enum_options)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault(),
        };

        string? preferred = null;
        OsuFileInfo? preferredInfo = null;

        foreach (string osuFile in osuFiles)
        {
            OsuFileInfo info = OsuFileScanner.Scan(osuFile);
            if (info.Mode == 0)
            {
                preferred = osuFile;
                preferredInfo = info;
                break;
            }

            preferred ??= osuFile;
            preferredInfo ??= info;
        }

        set.PreferredOsuFile = preferred;

        if (preferred != null && preferredInfo != null)
        {
            string baseDir = Path.GetDirectoryName(preferred) ?? dir;
            set.Widescreen = preferredInfo.Widescreen;

            if (preferredInfo.AudioFilename != null)
            {
                string audioPath = Path.Combine(baseDir, preferredInfo.AudioFilename);
                set.AudioFile = File.Exists(audioPath) ? audioPath : null;
            }

            if (preferredInfo.BackgroundFilename != null)
            {
                string bgPath = Path.Combine(baseDir, preferredInfo.BackgroundFilename);
                set.BackgroundFile = File.Exists(bgPath) ? bgPath : null;
            }

            if (preferredInfo.VideoFilename != null)
            {
                string videoPath = Path.Combine(baseDir, preferredInfo.VideoFilename);
                set.VideoFile = File.Exists(videoPath) ? videoPath : null;
            }
        }

        return set;
    }
}
