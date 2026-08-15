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

/// <summary>
/// How far along a set's in-flight download is. <see cref="Indeterminate"/> means the mirror never
/// advertised a <c>Content-Length</c> (or hasn't sent the first byte yet), so there is no honest
/// denominator to draw a bar against — UI shows a spinner instead of a percentage in that case.
/// </summary>
public readonly record struct DownloadProgress(double Value, bool Indeterminate);

public class BeatmapCache
{
    private readonly string root;
    private readonly IBeatmapMirror mirror;
    private readonly bool noVideo;
    private readonly ConcurrentDictionary<int, Task<CachedBeatmapSet>> inflight = new();

    /// <summary>
    /// Per-set download progress for whatever is currently in flight, written from the mirror's
    /// download thread and read by UI on the update thread — a <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// (rather than a bindable) precisely because those are different threads: readers take a
    /// lock-free snapshot of an immutable value, with no cross-thread event dispatch to marshal.
    /// Entries appear when a download starts and are removed when it finishes or fails, so
    /// <see cref="TryGetDownloadProgress"/> returning false means "not downloading".
    /// </summary>
    private readonly ConcurrentDictionary<int, DownloadProgress> downloadProgress = new();

    private static readonly EnumerationOptions osu_enum_options = new() { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive };

    public BeatmapCache(string rootDirectory, IBeatmapMirror mirror, bool noVideo = false)
    {
        root = rootDirectory;
        this.mirror = mirror;
        this.noVideo = noVideo;
    }

    public bool IsCached(int setId) => hasOsuFiles(Path.Combine(root, setId.ToString()));

    /// <summary>
    /// True while a <see cref="GetAsync"/> call for <paramref name="setId"/> is in flight and
    /// hasn't completed yet — i.e. it's tracked in <see cref="inflight"/> but not yet cached on
    /// disk. Once the download/extract finishes, <see cref="IsCached"/> becomes true and this
    /// reverts to false (there's a narrow window where both could technically read true/false
    /// depending on scheduling, but the "not cached yet" check keeps this false for a set that
    /// was already on disk and is just being re-touched via a cache hit).
    /// </summary>
    public bool IsDownloading(int setId) => inflight.ContainsKey(setId) && !IsCached(setId);

    /// <summary>
    /// Snapshots how far <paramref name="setId"/>'s download has got, or returns false when nothing
    /// is downloading for it (never started, already cached, or finished/failed). Safe to call from
    /// any thread; callers on the update thread can poll it every frame.
    /// </summary>
    public bool TryGetDownloadProgress(int setId, out DownloadProgress progress)
        => downloadProgress.TryGetValue(setId, out progress);

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

            // Published before the request goes out (not on the first progress callback) so UI can
            // show *something* for the round trip spent waiting on response headers — which on a
            // cold mirror is a visible fraction of the whole wait.
            downloadProgress[setId] = new DownloadProgress(0, true);

            await using (var fs = File.Create(tmpOsz))
                await mirror.DownloadAsync(setId, noVideo, fs, ct, (read, total) => reportProgress(setId, read, total)).ConfigureAwait(false);

            // The extract/scan that follows has no meaningful percentage of its own, so the row
            // falls back to the indeterminate spinner rather than sitting frozen at 100%.
            downloadProgress[setId] = new DownloadProgress(1, true);

            string tmpDir = dir + ".extracting";
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            ZipFile.ExtractToDirectory(tmpOsz, tmpDir);
            File.Delete(tmpOsz);
            Directory.Move(tmpDir, dir);
            return LoadFromDirectory(setId, dir);
        }
        finally
        {
            inflight.TryRemove(setId, out _);
            downloadProgress.TryRemove(setId, out _);
        }
    }

    private void reportProgress(int setId, long read, long? total)
    {
        downloadProgress[setId] = total is > 0
            ? new DownloadProgress(Math.Clamp((double)read / total.Value, 0, 1), false)
            : new DownloadProgress(0, true);
    }

    /// <summary>
    /// Imports a .osz that is already on disk (dragged onto the window — see
    /// <see cref="Import.DroppedFileImporter"/>) into this cache, so it is indistinguishable from a
    /// downloaded set everywhere downstream: same <c>cache/{setId}/</c> layout, same
    /// <see cref="CachedBeatmapSet"/>, and a later <see cref="GetAsync"/> for the same id is a
    /// plain cache hit that never touches a mirror.
    ///
    /// <para>
    /// The id comes from the archive's own contents (see <see cref="ResolveArchiveSetId"/>). Extraction
    /// goes to a staging directory first and is only moved into place once the id is known — the
    /// staging name is deliberately not a bare integer, so <see cref="EvictToLimit"/> ignores it
    /// exactly as it ignores <c>.extracting</c> directories. An existing directory for the resolved
    /// id is replaced: re-dropping a .osz is the natural way to repair a partially-extracted or
    /// stale copy.
    /// </para>
    /// </summary>
    /// <returns>The imported set, loaded the same way a downloaded one is.</returns>
    public CachedBeatmapSet ImportArchive(string archivePath)
    {
        Directory.CreateDirectory(root);

        string staging = Path.Combine(root, $"import-{Guid.NewGuid():N}.extracting");

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);

            if (!hasOsuFiles(staging))
                throw new InvalidDataException("archive contains no .osu difficulty");

            int setId = ResolveArchiveSetId(staging, archivePath);
            string dir = Path.Combine(root, setId.ToString());

            if (Directory.Exists(dir))
                Directory.Delete(dir, true);

            Directory.Move(staging, dir);
            Logger.Log($"BeatmapCache: imported '{Path.GetFileName(archivePath)}' as set {setId}");

            return LoadFromDirectory(setId, dir);
        }
        finally
        {
            // Only still present if something above threw before the move.
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"BeatmapCache: failed to clean up staging directory '{staging}'");
                }
            }
        }
    }

    /// <summary>
    /// The cache id for an extracted archive: the first positive <c>[Metadata] BeatmapSetID</c> any
    /// of its difficulties declares, else a synthetic local id derived from the set's metadata (see
    /// <see cref="LocalSetId"/>).
    ///
    /// <para>
    /// Unsubmitted maps, editor exports and some very old sets carry no id at all (or the -1
    /// sentinel), and two different such sets must not collide on one cache directory — so the
    /// fallback hashes the identity that actually distinguishes them (artist / title / creator),
    /// which also means re-dropping the same .osz lands on the same directory instead of piling up
    /// copies. Falls back to the archive's file name when even that metadata is absent.
    /// </para>
    /// </summary>
    internal static int ResolveArchiveSetId(string extractedDir, string archivePath)
    {
        OsuFileInfo? first = null;

        foreach (string osuFile in Directory.EnumerateFiles(extractedDir, "*.osu", osu_enum_options)
                                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            OsuFileInfo info;

            try
            {
                info = OsuFileScanner.Scan(osuFile);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"BeatmapCache: failed to scan '{osuFile}' while resolving an imported set id");
                continue;
            }

            if (info.BeatmapSetId > 0)
                return info.BeatmapSetId;

            first ??= info;
        }

        string identity = string.Join('|', first?.Artist ?? "", first?.Title ?? "", first?.Creator ?? "");

        if (identity.Replace("|", "").Length == 0)
            identity = Path.GetFileNameWithoutExtension(archivePath);

        return LocalSetId(identity);
    }

    /// <summary>
    /// Hashes <paramref name="identity"/> into the NEGATIVE int range. Real osu! beatmapset ids are
    /// always positive, so a negative id can never collide with one and doubles as the marker for
    /// "local only, no mirror behind it" — which is why <see cref="EvictToLimit"/> refuses to evict
    /// these (nothing could re-download them) and why the UI skips online cover/page lookups for
    /// them. FNV-1a: no cryptographic requirement here, just a stable, dependency-free spread.
    /// </summary>
    internal static int LocalSetId(string identity)
    {
        uint hash = 2166136261;

        foreach (char c in identity)
        {
            hash ^= c;
            hash *= 16777619;
        }

        // [0, int.MaxValue] mapped onto [int.MinValue, -1] — every value negative, none zero.
        return -(int)(hash & 0x7FFFFFFF) - 1;
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
            OsuFileInfo info;

            try
            {
                info = OsuFileScanner.Scan(osuFile);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"BeatmapCache: failed to scan '{osuFile}', skipping difficulty");
                continue;
            }

            set.Difficulties.Add(new DifficultyInfo
            {
                Path = osuFile,
                Version = string.IsNullOrEmpty(info.Version) ? Path.GetFileNameWithoutExtension(osuFile) : info.Version,
                Mode = info.Mode,
                AudioFilename = info.AudioFilename,
            });

            if (preferred == null || (preferredInfo!.Mode != 0 && info.Mode == 0))
            {
                preferred = osuFile;
                preferredInfo = info;
            }
        }

        set.PreferredOsuFile = preferred;

        if (preferred != null && preferredInfo != null)
        {
            string baseDir = Path.GetDirectoryName(preferred) ?? dir;

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

            // Skip non-set directories: this also covers in-progress "<id>.extracting" and
            // "import-<guid>.extracting" dirs, since their names aren't bare integers either.
            if (!int.TryParse(name, out int setId))
                continue;

            // Locally-imported sets (negative ids — see LocalSetId) have no mirror behind them, so
            // evicting one would destroy the only copy of a .osz the user dragged in. They still
            // count towards the measured total below, they're just never candidates for deletion.
            if (setId < 0)
            {
                total += dirSize(dir);
                continue;
            }

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
