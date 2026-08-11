#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Logging;

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
            {
                // Touch the dir's mtime on every cache hit so EvictToLimit's LRU ordering
                // reflects last-played time, not last-downloaded time.
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
                return LoadFromDirectory(setId, dir);
            }

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

    /// <summary>
    /// Deletes cached set directories, least-recently-played first (by directory mtime, which
    /// <see cref="GetAsync"/> touches on every cache hit), until the total cache size is at or
    /// under <paramref name="maxBytes"/>. Directories whose set id is in
    /// <paramref name="protectedIds"/> (e.g. the currently playing or queued sets) are never
    /// deleted, even if they're the least-recently-played.
    /// </summary>
    public void EvictToLimit(long maxBytes, IReadOnlyCollection<int> protectedIds)
    {
        if (!Directory.Exists(root))
            return;

        var candidates = new List<(int SetId, string Dir, long Size, DateTime MTime)>();
        long total = 0;

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(dir);

            // Skip non-set directories: in-progress extractions and anything not a plain set id.
            if (!int.TryParse(name, out int setId) || name.EndsWith(".extracting", StringComparison.OrdinalIgnoreCase))
                continue;

            long size = dirSize(dir);
            total += size;
            candidates.Add((setId, dir, size, Directory.GetLastWriteTimeUtc(dir)));
        }

        if (total <= maxBytes)
            return;

        foreach (var candidate in candidates.OrderBy(c => c.MTime))
        {
            if (total <= maxBytes)
                break;

            if (protectedIds.Contains(candidate.SetId))
                continue;

            try
            {
                Directory.Delete(candidate.Dir, true);
                total -= candidate.Size;
                Logger.Log($"BeatmapCache: evicted set {candidate.SetId} ({candidate.Size / (1024.0 * 1024.0):F1} MB) to stay under cache limit");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"BeatmapCache: failed to evict set {candidate.SetId}");
            }
        }
    }

    private static long dirSize(string dir)
        => Directory.EnumerateFiles(dir, "*", osu_enum_options).Sum(f => new FileInfo(f).Length);
}
