#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Online;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace JukeBox.Game.Import;

/// <summary>
/// Turns the command line into queue entries: <c>osu-jukebox &lt;set link&gt; &lt;song.osz&gt;
/// &lt;replay url&gt;</c>.
///
/// <para>
/// Arguments are handled STRICTLY IN ORDER, one awaited at a time, because the queue has no
/// insert-at-index — <see cref="MusicQueue.Enqueue"/> appends, so position is decided by whichever
/// import happens to finish first. Resolving in parallel would put a small local .osz ahead of a
/// set link that had to be downloaded, which is not the order the user typed. Sequential also
/// means the first argument starts playing while the rest are still resolving.
/// </para>
///
/// <para>
/// One bad argument never kills the batch: each is caught on its own and reported by name, and the
/// next one still runs. Files go through <see cref="DroppedFileImporter"/> rather than a parallel
/// implementation, so a path given on the command line behaves exactly as it would if dropped on
/// the window — replay beatmap lookup and skin selection included.
/// </para>
/// </summary>
public partial class LaunchArgumentImporter : Component
{
    /// <summary>
    /// User-facing outcomes, in the same shape <see cref="DroppedFileImporter.Notification"/>
    /// uses; <c>MainScreen</c> binds both and turns each into a toast. Separate from the file
    /// importer's own so a per-argument failure ("that isn't a beatmapset") reads as its own
    /// event rather than being attributed to a file import that never started.
    /// </summary>
    public readonly Bindable<DropNotification?> Notification = new Bindable<DropNotification?>();

    private int notificationSequence;

    [Resolved]
    private DroppedFileImporter files { get; set; } = null!;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    /// <summary>
    /// osu!'s own API, and the only thing here that can turn a beatmap id into its set. Its
    /// credentials are the user's own and empty by default, so every path through it checks
    /// <see cref="OfficialBeatmapSearch.HasCredentials"/> first rather than firing a request that
    /// can only come back as an authentication failure.
    /// </summary>
    [Resolved]
    private OfficialBeatmapSearch official { get; set; } = null!;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private BeatmapCache cache { get; set; } = null!;

    [Resolved]
    private GameHost host { get; set; } = null!;

    [Resolved]
    private HttpClient http { get; set; } = null!;

    /// <summary>
    /// Handles a batch, in order, reporting each failure and carrying on. Returns when every
    /// argument has been dealt with — awaited by tests; fire-and-forget in the app.
    /// </summary>
    public async Task HandleAsync(IReadOnlyList<string> arguments)
    {
        // Local files are gathered and imported as ONE batch at the end. Several .osr named on a
        // single command line are the same "these belong together" gesture as dropping them at
        // once, and grouping them into one queue entry is only possible while something still
        // knows they arrived together — a per-argument loop throws that away immediately.
        var localFiles = new List<string>();

        foreach (string argument in arguments)
        {
            if (LaunchArgument.IsSwitch(argument))
                continue;

            try
            {
                var classified = LaunchArgument.Classify(argument);

                if (classified.Kind == LaunchArgumentKind.LocalFile)
                {
                    if (File.Exists(classified.Path))
                        localFiles.Add(classified.Path);
                    else
                        notify($"No such file: {classified.Raw}", isError: true);

                    continue;
                }

                await handleAsync(classified).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Deliberately per-argument: a mirror timing out on argument 2 must not cost the
                // user arguments 3 and 4.
                Logger.Error(e, $"[args] '{argument}' failed");
                notify($"Couldn't add {argument}: {e.Message}", isError: true);
            }
        }

        if (localFiles.Count == 0)
            return;

        try
        {
            await files.ImportMany(localFiles).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[args] local file import failed");
            notify($"Couldn't add the dropped files: {e.Message}", isError: true);
        }
    }

    private Task handleAsync(LaunchArgument argument)
    {
        switch (argument.Kind)
        {
            case LaunchArgumentKind.BeatmapSet:
                return queueSetAsync(argument);

            case LaunchArgumentKind.Beatmap:
                return queueBeatmapAsync(argument);

            case LaunchArgumentKind.LocalFile:
                return importLocalAsync(argument);

            case LaunchArgumentKind.RemoteFile:
                return importRemoteAsync(argument);

            default:
                notify($"Don't know what to do with {argument.Raw}", isError: true);
                return Task.CompletedTask;
        }
    }

    private async Task queueSetAsync(LaunchArgument argument)
    {
        var found = await BeatmapSetLookup.ResolveAsync(mirror, argument.Id).ConfigureAwait(false);

        if (found == null)
        {
            // A bare number is READ as a set id, but it may well have been a difficulty id — the
            // two are indistinguishable as text. Before reporting a miss, try resolving it as a
            // beatmap; that is the whole reason this second attempt exists.
            if (await resolveViaOfficialAsync(argument, quietWhenUnavailable: true).ConfigureAwait(false))
                return;

            notify($"No beatmapset {argument.Id} on this mirror", isError: true);
            return;
        }

        if (argument.DifficultyId > 0)
            await preferDifficultyAsync(found, argument.DifficultyId).ConfigureAwait(false);

        // EnqueueAndMaybePlayAsync touches the queue directly and documents that it must run on
        // the update thread.
        await onUpdateThread(() => jukebox.EnqueueAndMaybePlayAsync(found, announce: false)).ConfigureAwait(false);

        notify($"Added: {(found.DisplayTitle.Length > 0 ? found.DisplayTitle : argument.Id.ToString())}", isError: false);
    }

    /// <summary>
    /// A <c>/b/</c> or <c>/beatmaps/</c> link — it already says it names a difficulty, so there is
    /// no set lookup to try first.
    /// </summary>
    private async Task queueBeatmapAsync(LaunchArgument argument)
    {
        // Reaching here means the lookup actually ran and osu! has no such beatmap — the
        // no-credentials case reports itself, because that is a different problem with a
        // different remedy and the two must not be worded alike.
        if (!await resolveViaOfficialAsync(argument, quietWhenUnavailable: false).ConfigureAwait(false))
            notify($"osu! has no beatmap {argument.Id}", isError: true);
    }

    /// <summary>
    /// Resolves an id as a BEATMAP through osu!'s own API and queues the set it belongs to.
    /// Returns whether it got there — including whether it already reported why it could not, so
    /// the caller does not report a second, contradictory reason.
    ///
    /// <para>
    /// This is the only route from a beatmap id to its set: the mirrors expose a set filter and
    /// nothing else. It therefore needs the user's own osu! API credentials, which are optional and
    /// empty by default, so most installs will fall through this. That is the honest ceiling of the
    /// feature rather than a bug — and when the credentials are the missing piece, the message says
    /// so instead of implying the beatmap does not exist.
    /// </para>
    /// </summary>
    /// <param name="argument">
    /// The argument being resolved. Its <see cref="LaunchArgument.Id"/> is read as a BEATMAP id
    /// here regardless of how it was classified, and its <see cref="LaunchArgument.Raw"/> is what
    /// any message names, so the user sees back what they typed.
    /// </param>
    /// <param name="quietWhenUnavailable">
    /// True when the caller has its own fallback message to show (a bare id that was read as a set
    /// first). It keeps a missing-credentials note out of the way of "no beatmapset 12345", which
    /// is the likelier explanation for a number that resolved as neither.
    /// </param>
    private async Task<bool> resolveViaOfficialAsync(LaunchArgument argument, bool quietWhenUnavailable)
    {
        if (!official.HasCredentials)
        {
            if (!quietWhenUnavailable)
            {
                notify($"{argument.Raw} names one difficulty — looking up its beatmapset needs osu! API credentials (Settings → Online)",
                    isError: true);
                return true;
            }

            return false;
        }

        int? setId;

        try
        {
            setId = await official.ResolveBeatmapSetIdAsync(argument.Id).ConfigureAwait(false);
        }
        catch (OfficialSearchException e)
        {
            // Already worded for a user by the API layer.
            notify($"Couldn't look up {argument.Raw}: {e.Message}", isError: true);
            return true;
        }

        if (setId == null)
            return false;

        var found = await BeatmapSetLookup.ResolveAsync(mirror, setId.Value).ConfigureAwait(false);

        if (found == null)
        {
            notify($"{argument.Raw} belongs to beatmapset {setId.Value}, which this mirror doesn't have", isError: true);
            return true;
        }

        // The argument named a difficulty, so open on it — the same preference a deep link gets.
        await preferDifficultyAsync(found, argument.Id).ConfigureAwait(false);
        await onUpdateThread(() => jukebox.EnqueueAndMaybePlayAsync(found, announce: false)).ConfigureAwait(false);

        notify($"Added: {(found.DisplayTitle.Length > 0 ? found.DisplayTitle : setId.Value.ToString())}", isError: false);
        return true;
    }

    /// <summary>
    /// Best-effort: opens a deep-linked set on the difficulty the link named. The link carries a
    /// BEATMAP id, while the player selects a difficulty by FILE, so the two are bridged by the
    /// checksum the mirror reports for that beatmap — and a mirror that returns no per-difficulty
    /// checksums simply leaves the set on its default difficulty, which is the pre-existing
    /// behaviour rather than a failure worth interrupting the user about.
    /// </summary>
    private async Task preferDifficultyAsync(BeatmapSetInfo set, int difficultyId)
    {
        try
        {
            string? checksum = set.Beatmaps.FirstOrDefault(b => b.Id == difficultyId)?.Checksum;

            if (string.IsNullOrEmpty(checksum))
                return;

            var cached = await cache.GetAsync(set.Id).ConfigureAwait(false);
            string? osuFile = await Task.Run(() => DroppedFileImporter.ResolveDifficulty(cached, set, checksum)).ConfigureAwait(false);

            if (osuFile != null)
                cached.PreferredOsuFile = osuFile;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"[args] could not open set {set.Id} on difficulty {difficultyId}");
        }
    }

    private Task importLocalAsync(LaunchArgument argument)
    {
        if (!File.Exists(argument.Path))
        {
            notify($"No such file: {argument.Raw}", isError: true);
            return Task.CompletedTask;
        }

        // Straight into the drag-and-drop importer: same classification, same handlers, same
        // toasts. A path typed on the command line and the same path dropped on the window must
        // not be able to behave differently.
        return files.Import(argument.Path);
    }

    private async Task importRemoteAsync(LaunchArgument argument)
    {
        string? downloaded = null;

        try
        {
            downloaded = await downloadAsync(argument).ConfigureAwait(false);

            if (downloaded == null)
                return;

            await files.Import(downloaded).ConfigureAwait(false);
        }
        finally
        {
            // The import copies what it needs into the cache/skins directory, so the download is
            // scratch either way — including when the import threw.
            if (downloaded != null)
            {
                try
                {
                    File.Delete(downloaded);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"[args] could not clean up '{downloaded}'");
                }
            }
        }
    }

    /// <summary>
    /// Fetches a URL to a scratch file whose EXTENSION is the point: everything downstream
    /// classifies by it. The name is taken from Content-Disposition, then the URL's own path, and
    /// failing both from what the bytes turn out to be — mirror download links such as
    /// <c>catboy.best/d/1234</c> carry no extension anywhere in the URL.
    /// </summary>
    private async Task<string?> downloadAsync(LaunchArgument argument)
    {
        using var response = await http.GetAsync(argument.Path).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            notify($"Couldn't download {argument.Raw}: {(int)response.StatusCode} {response.ReasonPhrase}", isError: true);
            return null;
        }

        string directory = Path.Combine(Path.GetTempPath(), "jukebox-args");
        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, Path.GetRandomFileName());

        await using (var stream = File.Create(scratch))
            await response.Content.CopyToAsync(stream).ConfigureAwait(false);

        string extension = extensionFrom(response, argument.Path) ?? SniffExtension(scratch);

        if (extension.Length == 0)
        {
            File.Delete(scratch);
            notify($"Couldn't tell what {argument.Raw} is — expected a .osz, .osk or .osr", isError: true);
            return null;
        }

        string named = scratch + extension;
        File.Move(scratch, named, overwrite: true);
        return named;
    }

    private static string? extensionFrom(HttpResponseMessage response, string url)
    {
        string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName;

        string extension = Path.GetExtension(fileName?.Trim('"') ?? string.Empty);

        if (extension.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            extension = Path.GetExtension(uri.AbsolutePath);

        return DroppedFile.Classify("x" + extension) == DroppedFileKind.Unsupported ? null : extension;
    }

    /// <summary>
    /// Last resort for a URL that names no file — and mirror download links routinely name none.
    /// Decided by CONTENT, in the order that can be told apart cheaply:
    ///
    /// <list type="bullet">
    /// <item>.osz and .osk are both zips, so they are separated by what is inside: a beatmap
    /// carries .osu difficulties, a skin declares itself with skin.ini.</item>
    /// <item>.osr is not a zip, and is confirmed by actually parsing its header with the same
    /// reader the drag-and-drop path uses — a signature check would only guess at the bytes that
    /// reader already knows how to read.</item>
    /// </list>
    ///
    /// <para>Internal so it can be tested on files directly, without standing up an HTTP stub to
    /// deliver them.</para>
    /// </summary>
    internal static string SniffExtension(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            if (archive.Entries.Any(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
                return ".osz";

            if (archive.Entries.Any(e => Path.GetFileName(e.FullName).Equals("skin.ini", StringComparison.OrdinalIgnoreCase)))
                return ".osk";

            // A zip that is neither: nothing here can import it.
            return string.Empty;
        }
        catch (Exception)
        {
            // Not a zip. Fall through to the replay check.
        }

        try
        {
            OsrReader.ReadHeader(path);
            return ".osr";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private Task onUpdateThread(Func<Task> action)
    {
        var completion = new TaskCompletionSource();

        Schedule(() => action().ContinueWith(t =>
        {
            if (t.IsFaulted)
                completion.SetException(t.Exception!.InnerExceptions);
            else
                completion.SetResult();
        }));

        return completion.Task;
    }

    private void notify(string message, bool isError)
        => Schedule(() => Notification.Value = new DropNotification(++notificationSequence, message, isError));
}
