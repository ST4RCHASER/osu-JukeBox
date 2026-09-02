#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Game.Scoring;

namespace JukeBox.Game.Import;

/// <summary>
/// The single entry point for files dragged onto the window: subscribes to the SDL window's
/// <see cref="IWindow.DragDrop"/> event (one path per event — the framework raises it once per
/// dropped file), classifies each path by extension and routes it to the matching importer.
///
/// <para>
/// <see cref="IWindow.DragDrop"/> fires on the SDL window's own thread, so every path from here on
/// is marshalled onto the update thread through <c>Schedule</c> before it touches anything the game
/// owns — the same rule <see cref="JukeBoxGameBase"/>'s focus handling follows for
/// <see cref="GameHost.IsActive"/>. The actual import work then runs off the update thread again
/// (zip extraction, mirror lookups and downloads all block), reporting back through
/// <see cref="Notification"/> — which is written on the update thread, since UI binds to it.
/// </para>
/// </summary>
public partial class DroppedFileImporter : Component
{
    /// <summary>
    /// The most recent user-facing outcome of a drop. UI (see <c>MainScreen</c>) binds a copy of
    /// this and turns each new value into a toast. Always written on the update thread.
    /// </summary>
    public readonly Bindable<DropNotification?> Notification = new();

    private int notificationSequence;

    [Resolved]
    private GameHost host { get; set; } = null!;

    [Resolved]
    private BeatmapCache cache { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    [Resolved]
    private SkinLibrary skinLibrary { get; set; } = null!;

    [Resolved]
    private ReplayStore replays { get; set; } = null!;

    private IWindow? subscribedWindow;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Null under a headless host (tests) and in any environment without a real window — the
        // importer stays fully usable in that case, it just has no OS-level source of drops.
        // Kept in a field rather than re-reading host.Window on dispose: the host tears its window
        // down during shutdown, and unsubscribing from a different (or null) object would leak the
        // handler.
        subscribedWindow = host.Window;

        if (subscribedWindow != null)
            subscribedWindow.DragDrop += onWindowDragDrop;
    }

    /// <summary>
    /// Paths dropped in one gesture but delivered one event at a time, waiting to be imported
    /// together. See <see cref="onWindowDragDrop"/>.
    /// </summary>
    private readonly List<string> pendingDrop = new List<string>();

    private ScheduledDelegate? pendingDropFlush;

    /// <summary>
    /// How long to keep collecting dropped paths before importing them as one batch. The OS
    /// delivers a multi-file drop as one event PER FILE, so without this a five-replay drop is five
    /// unrelated imports and there is nothing left to tell us they arrived together.
    /// </summary>
    internal const double DROP_BATCH_MS = 150;

    private void onWindowDragDrop(string path) => Schedule(() =>
    {
        pendingDrop.Add(path);

        // Trailing window: each further file in the same drop pushes the flush back, so the batch
        // closes only once the drop has actually finished arriving.
        pendingDropFlush?.Cancel();
        pendingDropFlush = Scheduler.AddDelayed(() =>
        {
            pendingDropFlush = null;

            string[] batch = pendingDrop.ToArray();
            pendingDrop.Clear();

            // Deliberately not awaited: a drop has no caller to await it, and the importer reports
            // its own outcome through Notification. Discarded explicitly so that stays a decision
            // rather than an oversight.
            _ = ImportMany(batch);
        }, DROP_BATCH_MS);
    });

    /// <summary>
    /// Imports one dropped path. Fire-and-forget by design (a drop has no caller to await it), but
    /// returns the task so tests can await the whole round trip. Safe to call with anything —
    /// unrecognised extensions and unreadable files are reported through <see cref="Notification"/>
    /// rather than thrown.
    /// </summary>
    public Task Import(string path) => ImportMany(new[] { path });

    /// <summary>
    /// Imports a whole drop at once. The batch matters for REPLAYS and only for replays: several
    /// .osr naming the same beatmap are one viewing session, so they resolve and download that
    /// beatmap once and arrive as a single queue entry carrying all of them. Replays for different
    /// beatmaps, and every other kind of file, are imported independently exactly as before.
    /// </summary>
    public async Task ImportMany(IReadOnlyList<string> paths)
    {
        var replayPaths = new List<string>();

        foreach (string path in paths)
        {
            var kind = DroppedFile.Classify(path);
            Logger.Log($"[drop] {Path.GetFileName(path)} classified as {kind}");

            if (kind == DroppedFileKind.Replay)
                replayPaths.Add(path);
            else
                await importAsync(path, kind).ConfigureAwait(false);
        }

        if (replayPaths.Count > 0)
            await importReplayBatchAsync(replayPaths).ConfigureAwait(false);
    }

    private async Task importAsync(string path, DroppedFileKind kind)
    {
        try
        {
            switch (kind)
            {
                case DroppedFileKind.BeatmapArchive:
                    await importBeatmapArchiveAsync(path).ConfigureAwait(false);
                    break;

                case DroppedFileKind.SkinArchive:
                    await importSkinArchiveAsync(path).ConfigureAwait(false);
                    break;

                case DroppedFileKind.Replay:
                    // Replays never reach here: ImportMany routes them all through the batch path
                    // so a group can be recognised. Kept explicit so a future caller adding a
                    // fourth kind doesn't quietly reintroduce the one-at-a-time replay import.
                    await importReplayBatchAsync(new[] { path }).ConfigureAwait(false);
                    break;

                default:
                    Notify($"Can't import {Path.GetFileName(path)} — drop a .osz, .osk or .osr", isError: true);
                    break;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, $"[drop] import of '{path}' failed");
            Notify($"Import failed: {e.Message}", isError: true);
        }
    }

    /// <summary>
    /// Extracts a dropped .osz into the beatmap cache and queues it. The extraction runs on the
    /// threadpool (it is synchronous, disk-bound and can take seconds for a big set), and the
    /// enqueue hops back onto the update thread — <see cref="Jukebox.EnqueueAndMaybePlayAsync"/>
    /// touches the queue's <c>BindableList</c> directly and documents that requirement.
    /// </summary>
    private async Task importBeatmapArchiveAsync(string path)
    {
        var cached = await Task.Run(() => cache.ImportArchive(path)).ConfigureAwait(false);
        var set = LocalBeatmapMetadata.Describe(cached);

        await onUpdateThread(() => jukebox.EnqueueAndMaybePlayAsync(set, announce: false)).ConfigureAwait(false);

        Notify($"Added: {(set.DisplayTitle.Length > 0 ? set.DisplayTitle : Path.GetFileName(path))}", isError: false);
    }

    /// <summary>
    /// Extracts a dropped .osk into the app's <c>skins/</c> storage and selects it immediately.
    /// The selection is two config writes — the imported folder name, then
    /// <see cref="JukeBoxSkin.Custom"/> — which persist like any other setting, so the skin is
    /// still active after a restart. <see cref="LazerPlayer.SkinSelection"/> turns those into a
    /// live chart-layer rebuild.
    ///
    /// <para>
    /// Imports ACCUMULATE. The folder is named after the archive, so each distinct .osk lands
    /// beside the others and joins the library (<see cref="SkinLibrary"/>), while re-importing the
    /// same archive replaces that one folder in place — see <see cref="SkinArchive.Extract"/>.
    /// </para>
    /// </summary>
    private async Task importSkinArchiveAsync(string path)
    {
        string folder = SkinArchive.SanitiseName(Path.GetFileNameWithoutExtension(path));
        string skinsRoot = host.Storage.GetFullPath(SkinLibrary.STORAGE_DIRECTORY);

        string extracted = await Task.Run(() => SkinArchive.Extract(path, skinsRoot, folder)).ConfigureAwait(false);

        // The name the skin calls itself, which is what the dropdown will show — not necessarily
        // the archive's filename that the folder is named after.
        string displayName = SkinLibrary.ReadDisplayName(extracted);

        await onUpdateThread(() =>
        {
            // Folder name first: writing Skin=Custom while CustomSkinPath still points at the
            // PREVIOUS import would briefly build the old skin.
            config.SetValue(JukeBoxSetting.CustomSkinPath, folder);
            config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Custom);

            // Re-list, so the new skin is in the dropdown straight away. Needed even when the
            // config writes above changed nothing (re-importing the skin already selected): the
            // library is read off disk, and the disk is what just moved.
            skinLibrary.Refresh();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        Notify($"Skin applied: {displayName}", isError: false);
    }

    /// <summary>
    /// Imports a dropped .osr: read its header, find the beatmap it was played on, make sure that
    /// beatmap is cached, decode the replay against it, and queue the set with the replay attached
    /// so playback renders the real play instead of autoplay.
    /// </summary>
    /// <summary>
    /// Imports a batch of dropped .osr, grouped by the beatmap each was PLAYED ON — the replay's
    /// own MD5, which is the only beatmap identity a .osr carries. Grouping on that rather than on
    /// title is what makes it exact: two people playing the same song on different difficulties, or
    /// on a re-upload of the set, are genuinely different beatmaps and stay separate entries.
    ///
    /// <para>
    /// Each group resolves and downloads its beatmap ONCE and becomes ONE queue entry carrying all
    /// of its replays, so five .osr for one map is one row credited to five people rather than five
    /// identical rows. Headers that won't read are reported and dropped from the batch rather than
    /// failing the rest of it.
    /// </para>
    /// </summary>
    private async Task importReplayBatchAsync(IReadOnlyList<string> paths)
    {
        var byBeatmap = new Dictionary<string, List<(string Path, OsrHeader Header)>>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            OsrHeader header;

            try
            {
                header = await Task.Run(() => OsrReader.ReadHeader(path)).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                Notify($"Not a readable replay: {e.Message}", isError: true);
                continue;
            }

            if (!byBeatmap.TryGetValue(header.BeatmapMd5, out var group))
                byBeatmap[header.BeatmapMd5] = group = new List<(string, OsrHeader)>();

            group.Add((path, header));
        }

        foreach (var group in byBeatmap.Values)
            await importReplayGroupAsync(group).ConfigureAwait(false);
    }

    /// <summary>One beatmap's worth of replays: resolve it once, decode each replay against it, and
    /// queue a single entry carrying all of them.</summary>
    private async Task importReplayGroupAsync(IReadOnlyList<(string Path, OsrHeader Header)> group)
    {
        var header = group[0].Header;

        string player = header.PlayerName.Length > 0 ? header.PlayerName : "an unknown player";

        var found = await findSetByChecksumAsync(header.BeatmapMd5).ConfigureAwait(false);

        if (found == null)
        {
            // Unsubmitted maps, and maps the mirror simply doesn't index, have no set to find —
            // there is genuinely nothing to play, so say so rather than queueing a wrong beatmap.
            Notify($"No beatmap found for {player}'s replay (checksum {header.BeatmapMd5[..8]}…)", isError: true);
            return;
        }

        var cached = await cache.GetAsync(found.Id).ConfigureAwait(false);

        // The replay names ONE .osu by checksum; the set generally has several. Matching here is
        // what lets playback select the difficulty actually played.
        string? osuFile = await Task.Run(() => ResolveDifficulty(cached, found, header.BeatmapMd5)).ConfigureAwait(false);

        if (osuFile == null)
            Logger.Log($"[drop] set {found.Id} has no difficulty matching the replay's checksum — falling back to autoplay on its default difficulty", level: LogLevel.Important);

        var attachments = new List<ReplayAttachment>(group.Count);

        // One decode per replay, all against the SAME difficulty — the group is defined by that
        // difficulty's checksum, so there is nothing per-replay left to resolve.
        foreach (var (path, replayHeader) in group)
        {
            Score? score = null;

            if (osuFile != null)
            {
                try
                {
                    score = await Task.Run(() => new JukeBoxScoreDecoder(osuFile).Decode(path)).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // The set and difficulty are still worth queueing (with the credit shown) even
                    // if the frames themselves won't decode — the user gets the map they dropped.
                    Logger.Error(e, $"[drop] failed to decode replay '{path}' — queueing the beatmap with autoplay instead");
                }
            }

            var mods = ReplayMods.ForGameplay(score);
            var (rateTempo, rateFrequency) = ReplayMods.TrackAdjustmentsFor(mods);

            var attachment = new ReplayAttachment
            {
                PlayerName = replayHeader.PlayerName,
                SourcePath = path,
                BeatmapMd5 = replayHeader.BeatmapMd5,
                OsuFile = osuFile,
                Score = score,
                ModAcronyms = ReplayMods.Acronyms(mods),
                RateTempo = rateTempo,
                RateFrequency = rateFrequency,
                PlayedAt = replayHeader.PlayedAt,
            };

            if (attachment.ModAcronyms.Count > 0)
            {
                Logger.Log($"[drop] {PlayerLabel(attachment)} played with {string.Join(" ", attachment.ModAcronyms)}"
                           + $" at {attachment.Rate:0.##}× speed (tempo {rateTempo:0.##}, frequency {rateFrequency:0.##})");
            }

            replays.Register(attachment);
            attachments.Add(attachment);
        }

        found.Replays = attachments;

        await onUpdateThread(() => jukebox.EnqueueAndMaybePlayAsync(found, announce: false)).ConfigureAwait(false);

        Notify($"Added: {found.DisplayTitle} — played by {PlayerCredit(attachments)}", isError: false);
    }

    /// <summary>One replay's player, as something that reads in a sentence when the name is empty.</summary>
    internal static string PlayerLabel(ReplayAttachment replay)
        => replay.PlayerName.Length > 0 ? replay.PlayerName : "an unknown player";

    /// <summary>
    /// The players of a group, as a credit line: "A", "A and B", "A, B and C". Beyond
    /// <see cref="CREDIT_NAMES"/> it becomes "A, B and 6 others" rather than a paragraph.
    /// </summary>
    internal static string PlayerCredit(IReadOnlyList<ReplayAttachment> group)
    {
        if (group.Count == 0)
            return "an unknown player";

        var names = group.Select(PlayerLabel).ToList();

        if (names.Count == 1)
            return names[0];

        if (names.Count > CREDIT_NAMES)
        {
            int others = names.Count - CREDIT_NAMES;
            return $"{string.Join(", ", names.Take(CREDIT_NAMES))} and {others} other{(others == 1 ? string.Empty : "s")}";
        }

        return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    /// <summary>How many players a credit names before it starts counting the rest.</summary>
    internal const int CREDIT_NAMES = 2;

    /// <summary>
    /// Finds the beatmapset a replay's beatmap checksum belongs to — the only identity a replay
    /// carries. Issued as a checksum-restricted search (see
    /// <see cref="SearchRequest.CHECKSUM_OPTION"/>), so whichever mirror the chain lands on either
    /// answers it properly or refuses and lets the next one try. The retry widens the status
    /// filter, since the default only covers ranked maps and people replay loved and graveyard
    /// ones too.
    /// </summary>
    private async Task<Online.BeatmapSetInfo?> findSetByChecksumAsync(string md5)
    {
        foreach (string status in new[] { "ranked", "any" })
        {
            try
            {
                var results = await mirror.SearchAsync(new SearchRequest
                {
                    Query = md5,
                    Option = SearchRequest.CHECKSUM_OPTION,
                    Status = status,
                    PageSize = 5,
                }).ConfigureAwait(false);

                if (results.Count > 0)
                    return results[0];
            }
            catch (Exception e)
            {
                Logger.Error(e, $"[drop] checksum lookup for {md5} failed (status={status})");
            }
        }

        return null;
    }

    /// <summary>
    /// Which cached .osu the replay was played on, given the checksum it recorded.
    ///
    /// <para>
    /// The obvious answer — hash each cached .osu and compare — is tried first and is correct
    /// whenever the archive we hold contains the canonical files. It is NOT always correct: mirrors
    /// repack, and NeriNyan in particular rewrites .osu files when serving a no-video download
    /// (<see cref="Configuration.JukeBoxSetting.NoVideoDownloads"/>), which changes their bytes and
    /// so their MD5 while the beatmap is otherwise the same one. Measured against a real set: every
    /// cached difficulty hashed differently from the checksums osu! itself publishes.
    /// </para>
    ///
    /// <para>
    /// So the fallback goes through the mirror's own per-difficulty <see cref="BeatmapInfo.Checksum"/>
    /// values, which DO name the canonical files: find the difficulty whose published checksum the
    /// replay recorded, then match that difficulty's name against the cached files' own
    /// <c>[Metadata] Version</c>. Null when neither route identifies one — the set still plays,
    /// just on its default difficulty under autoplay.
    /// </para>
    /// </summary>
    internal static string? ResolveDifficulty(Beatmaps.CachedBeatmapSet cached, Online.BeatmapSetInfo set, string replayMd5)
    {
        string? exact = cached.OsuFiles.FirstOrDefault(f => md5OfFile(f) == replayMd5);

        if (exact != null)
            return exact;

        string? version = set.Beatmaps
                             .FirstOrDefault(b => string.Equals(b.Checksum, replayMd5, StringComparison.OrdinalIgnoreCase))
                             ?.Version;

        if (string.IsNullOrEmpty(version))
            return null;

        string? byVersion = cached.Difficulties
                                  .FirstOrDefault(d => string.Equals(d.Version, version, StringComparison.Ordinal))
                                  ?.Path;

        if (byVersion != null)
            Logger.Log($"[drop] cached files don't hash to the replay's checksum (repacked archive); matched difficulty '{version}' through the mirror's published checksums instead");

        return byVersion;
    }

    private static string md5OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the update thread and completes once the task it returns
    /// does. Mirrors <c>Jukebox.onUpdateThread</c>, and exists for the same reason: everything this
    /// importer does after its first <c>await</c> is on the threadpool, but the jukebox's queue may
    /// only be touched from the update thread.
    /// </summary>
    private Task onUpdateThread(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Schedule(() =>
        {
            try
            {
                action().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        tcs.SetException(t.Exception!.InnerExceptions);
                    else
                        tcs.SetResult();
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Publishes a user-facing outcome. Callable from any thread — the write itself is scheduled
    /// onto the update thread, because UI binds to <see cref="Notification"/> and bindable change
    /// callbacks run wherever the write happened.
    /// </summary>
    protected void Notify(string message, bool isError)
        => Schedule(() => Notification.Value = new DropNotification(++notificationSequence, message, isError));

    protected override void Dispose(bool isDisposing)
    {
        if (subscribedWindow != null)
        {
            subscribedWindow.DragDrop -= onWindowDragDrop;
            subscribedWindow = null;
        }

        base.Dispose(isDisposing);
    }
}
