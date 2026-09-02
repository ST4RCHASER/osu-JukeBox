#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace JukeBox.Game.Online;

/// <summary>
/// Resolves a beatmapset ID to the set itself, through whichever mirror is configured.
///
/// <para>
/// Extracted so the two places a user can name a set by id — the map-ID dialog and the command
/// line — resolve it the same way. The two-step dance below is not obvious enough to be worth
/// writing twice: mirrors differ in whether they honour a field-restricted search, so a plain
/// query is needed as a fallback, and its results must be filtered by id because a plain query for
/// "12345" also matches sets with 12345 anywhere in their metadata.
/// </para>
/// </summary>
public static class BeatmapSetLookup
{
    /// <summary>
    /// NeriNyan's field-restricted search option — the only one any mirror here exposes, and a SET
    /// filter, which is why a beatmap id cannot be resolved this way (see
    /// <see cref="BeatmapLinkKind.Beatmap"/>).
    /// </summary>
    public const string SET_ID_OPTION = "setId";

    /// <summary>
    /// The set with this id, or null when no mirror has it. Never throws: a mirror being down is
    /// reported to the caller as "not found" and logged, because every caller's next move is the
    /// same either way.
    /// </summary>
    public static async Task<BeatmapSetInfo?> ResolveAsync(IBeatmapMirror mirror, int id, CancellationToken ct = default)
    {
        string query = id.ToString();

        try
        {
            var restricted = await mirror.SearchAsync(new SearchRequest { Query = query, Option = SET_ID_OPTION }, ct).ConfigureAwait(false);

            if (restricted.Count > 0 && restricted[0].Id == id)
                return restricted[0];

            // The mirror either ignored the restriction or genuinely has nothing under it. A plain
            // query reaches sets the restricted form misses, filtered by id since a free-text
            // search for the number matches far more than the set that carries it.
            var fallback = await mirror.SearchAsync(new SearchRequest { Query = query }, ct).ConfigureAwait(false);
            return fallback.FirstOrDefault(s => s.Id == id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"BeatmapSetLookup: lookup of set {id} failed");
            return null;
        }
    }
}
