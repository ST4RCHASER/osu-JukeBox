#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JukeBox.Game.Online;

/// <summary>A watched player as osu! describes them: who they are, and whether they are online.</summary>
/// <param name="Id">Their numeric osu! id — what every later request is keyed on.</param>
/// <param name="Username">Their name as osu! spells it, which may differ in case from what was typed.</param>
/// <param name="Presence">The real online/offline fact. See <see cref="SpectatePresence"/>.</param>
public readonly record struct SpectateUser(int Id, string Username, SpectatePresence Presence);

/// <summary>
/// One completed play, as much of it as the public API tells us — enough to decide whether it is
/// worth downloading and, if it is, exactly which difficulty to fetch.
/// </summary>
/// <param name="ScoreId">The score's id. This is the download key AND the new-play signal: a score
/// id we have already seen means nothing has happened since the last poll.</param>
/// <param name="BeatmapSetId">The set to fetch, straight from the score — no checksum search needed.</param>
/// <param name="BeatmapChecksum">The played difficulty's MD5, which is what picks the right .osu out
/// of the downloaded set.</param>
/// <param name="DifficultyName">The difficulty's name, the fallback when a mirror has rewritten the
/// .osu bytes and its MD5 no longer matches (the same problem, and the same fallback, that the
/// dropped-replay importer has).</param>
/// <param name="EndedAt">When the play finished — the input to <see cref="SpectateStateRules.For"/>.</param>
/// <param name="Passed">Whether it passed. The one activity fact osu! states outright.</param>
/// <param name="HasReplay">Whether a replay exists at all. Plays from clients that do not upload
/// one are real scores with nothing to watch, and downloading them would spend budget on a 404.</param>
public readonly record struct SpectateScore(
    long ScoreId,
    int BeatmapSetId,
    string BeatmapChecksum,
    string DifficultyName,
    DateTimeOffset EndedAt,
    bool Passed,
    bool HasReplay);

/// <summary>
/// The three questions spectating asks osu!, behind an interface so the poller can be tested
/// against fakes rather than the network.
/// </summary>
public interface ISpectateApi
{
    /// <summary>Turns a typed username into an account, or null when no such user exists.</summary>
    Task<SpectateUser?> ResolveUserAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Presence for a whole list of ids in ONE request. Batched deliberately: presence is the only
    /// thing every watched player needs every round, and asking per player would multiply the poll
    /// by the size of the list for information osu! is happy to hand over all at once.
    /// </summary>
    Task<IReadOnlyList<SpectateUser>> PresenceAsync(IReadOnlyList<int> userIds, CancellationToken ct = default);

    /// <summary>
    /// The player's most recently completed play, including failed ones, or null when they have
    /// none in osu!'s recent window.
    /// </summary>
    Task<SpectateScore?> LatestScoreAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Downloads one replay to <paramref name="destinationPath"/>.
    /// </summary>
    /// <exception cref="SpectateThrottledException">osu! answered 429 — the budget must back off.</exception>
    Task DownloadReplayAsync(long scoreId, string destinationPath, CancellationToken ct = default);
}

/// <summary>Anything the spectate endpoints refused, with a message fit to show a person.</summary>
public class SpectateApiException : Exception
{
    public SpectateApiException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// osu! answered 429 on a replay download. Separate from <see cref="SpectateApiException"/> because
/// the response is different in kind: not "this failed" but "stop asking for a while", which is
/// <see cref="ReplayDownloadBudget.Throttled"/>'s job rather than an error to report.
/// </summary>
public class SpectateThrottledException : SpectateApiException
{
    public SpectateThrottledException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// <see cref="ISpectateApi"/> against the real osu! API v2.
///
/// <para>
/// The token comes from a provider rather than being minted here, because there are two legitimate
/// sources and the caller knows which it has: the signed-in user's token
/// (<see cref="OsuAccount.GetAccessTokenAsync"/>) when an account is connected, and the app-only
/// <c>client_credentials</c> token otherwise. Everything this class asks for is in the
/// <c>public</c> scope, which BOTH carry — verified live, replay downloads included — so spectating
/// works for someone who has only pasted API credentials, and signing in simply makes it their own
/// quota being spent.
/// </para>
///
/// <para>
/// Nothing here is first-party: these are the same documented public endpoints any third-party
/// site uses. The live spectator hub, which is not, stays untouched — see
/// <c>.superpowers/spectate-research.md</c>.
/// </para>
/// </summary>
public sealed class OsuSpectateApi : ISpectateApi
{
    public const string USERS_ENDPOINT = "https://osu.ppy.sh/api/v2/users";
    public const string SCORES_ENDPOINT = "https://osu.ppy.sh/api/v2/scores";

    /// <summary>
    /// The API version header. Pinned to the same value the search backend uses so both halves of
    /// the app see one schema — notably the one where a recent score's <c>id</c> is the new-format
    /// score id that <c>/scores/{id}/download</c> expects.
    /// </summary>
    public const string API_VERSION = OfficialBeatmapSearch.API_VERSION;

    /// <summary>
    /// How many recent plays to ask for. One is all the poller uses: every state it can infer comes
    /// from the NEWEST play, and a longer list would be paid for on every player every round.
    /// </summary>
    private const int recent_limit = 1;

    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<string?>> tokenProvider;
    private readonly string usersEndpoint;
    private readonly string scoresEndpoint;

    /// <param name="http">The app's shared client.</param>
    /// <param name="tokenProvider">Hands back a bearer token, or null when the app has no
    /// credentials at all — which reads as "this feature is unavailable", not as a failure.</param>
    /// <param name="usersEndpoint">Overridable so a test can point at a stub server.</param>
    /// <param name="scoresEndpoint">Overridable for the same reason.</param>
    public OsuSpectateApi(HttpClient http, Func<CancellationToken, Task<string?>> tokenProvider,
                          string usersEndpoint = USERS_ENDPOINT, string scoresEndpoint = SCORES_ENDPOINT)
    {
        this.http = http;
        this.tokenProvider = tokenProvider;
        this.usersEndpoint = usersEndpoint;
        this.scoresEndpoint = scoresEndpoint;
    }

    public async Task<SpectateUser?> ResolveUserAsync(string username, CancellationToken ct = default)
    {
        // key=username stops a player whose name is all digits from being read as an id — osu!
        // resolves the bare route either way, and "727" is a real username.
        string url = $"{usersEndpoint}/{Uri.EscapeDataString(username)}/osu?key=username";

        using var response = await sendAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        string json = await readAsync(response, $"look up “{username}”", ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        return ParseUser(document.RootElement);
    }

    public async Task<IReadOnlyList<SpectateUser>> PresenceAsync(IReadOnlyList<int> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return Array.Empty<SpectateUser>();

        var query = new List<string>(userIds.Count);

        foreach (int id in userIds)
            query.Add($"ids[]={id.ToString(CultureInfo.InvariantCulture)}");

        using var response = await sendAsync($"{usersEndpoint}?{string.Join('&', query)}", ct).ConfigureAwait(false);

        string json = await readAsync(response, "check who is online", ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);

        var users = new List<SpectateUser>(userIds.Count);

        if (document.RootElement.TryGetProperty("users", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
                users.Add(ParseUser(element));
        }

        return users;
    }

    public async Task<SpectateScore?> LatestScoreAsync(int userId, CancellationToken ct = default)
    {
        // include_fails=1 is what makes a failed play visible at all; without it a player who is
        // audibly struggling reads as idle, which is the opposite of what is happening.
        string url = $"{usersEndpoint}/{userId.ToString(CultureInfo.InvariantCulture)}/scores/recent?include_fails=1&limit={recent_limit}";

        using var response = await sendAsync(url, ct).ConfigureAwait(false);

        string json = await readAsync(response, "read recent plays", ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var element in document.RootElement.EnumerateArray())
            return ParseScore(element);

        return null;
    }

    public async Task DownloadReplayAsync(long scoreId, string destinationPath, CancellationToken ct = default)
    {
        string url = $"{scoresEndpoint}/{scoreId.ToString(CultureInfo.InvariantCulture)}/download";

        using var response = await sendAsync(url, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SpectateThrottledException("osu! is rate-limiting replay downloads — pausing for a couple of minutes.");

        if (!response.IsSuccessStatusCode)
            throw new SpectateApiException($"osu! refused the replay download ({(int)response.StatusCode}).");

        string directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;

        if (directory.Length > 0)
            Directory.CreateDirectory(directory);

        // Written to a temp name and moved into place, so a download cut off halfway can never be
        // mistaken for a cached replay on the next run.
        string temp = destinationPath + ".part";

        await using (var file = File.Create(temp))
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);

        File.Move(temp, destinationPath, true);
    }

    /// <summary>
    /// Reads a user out of either shape osu! returns them in — the full profile from the single
    /// lookup and the compact entry from the batch differ in what else they carry, but agree
    /// exactly on the four fields wanted here.
    /// </summary>
    internal static SpectateUser ParseUser(JsonElement element)
    {
        int id = element.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out int parsedId) ? parsedId : 0;

        string username = element.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : string.Empty;

        bool online = element.TryGetProperty("is_online", out var isOnline) && isOnline.ValueKind == JsonValueKind.True;

        DateTimeOffset? lastVisit = null;

        // Commonly null — users can hide it — so its absence must never be read as "never visited".
        if (element.TryGetProperty("last_visit", out var visit) && visit.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(visit.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedVisit))
        {
            lastVisit = parsedVisit;
        }

        return new SpectateUser(id, username, new SpectatePresence(online, lastVisit));
    }

    /// <summary>Reads one entry of the recent-scores array.</summary>
    internal static SpectateScore ParseScore(JsonElement element)
    {
        long scoreId = element.TryGetProperty("id", out var id) && id.TryGetInt64(out long parsedId) ? parsedId : 0;

        var beatmap = element.TryGetProperty("beatmap", out var map) ? map : default;

        int setId = beatmap.ValueKind == JsonValueKind.Object
                    && beatmap.TryGetProperty("beatmapset_id", out var set) && set.TryGetInt32(out int parsedSet)
            ? parsedSet
            : 0;

        string checksum = beatmap.ValueKind == JsonValueKind.Object
                          && beatmap.TryGetProperty("checksum", out var sum) && sum.ValueKind == JsonValueKind.String
            ? sum.GetString() ?? string.Empty
            : string.Empty;

        string version = beatmap.ValueKind == JsonValueKind.Object
                         && beatmap.TryGetProperty("version", out var difficulty) && difficulty.ValueKind == JsonValueKind.String
            ? difficulty.GetString() ?? string.Empty
            : string.Empty;

        DateTimeOffset endedAt = element.TryGetProperty("ended_at", out var ended) && ended.ValueKind == JsonValueKind.String
                                 && DateTimeOffset.TryParse(ended.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedEnd)
            ? parsedEnd
            : DateTimeOffset.MinValue;

        bool passed = element.TryGetProperty("passed", out var pass) && pass.ValueKind == JsonValueKind.True;
        bool hasReplay = element.TryGetProperty("has_replay", out var replay) && replay.ValueKind == JsonValueKind.True;

        return new SpectateScore(scoreId, setId, checksum, version, endedAt, passed, hasReplay);
    }

    private async Task<HttpResponseMessage> sendAsync(string url, CancellationToken ct)
    {
        string? token = await tokenProvider(ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(token))
            throw new SpectateApiException("Spectating needs osu! API credentials — set them in Settings → Online, or connect your account.");

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("x-api-version", API_VERSION);

        return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The body of a successful response, or a failure phrased around what we were trying to do.
    /// The response body is never quoted back: it is osu!'s own wording for its own concerns, and
    /// the one thing a person needs is which of OUR requests failed.
    /// </summary>
    private static async Task<string> readAsync(HttpResponseMessage response, string attempt, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SpectateThrottledException($"osu! is rate-limiting us — could not {attempt}.");

        if (!response.IsSuccessStatusCode)
            throw new SpectateApiException($"osu! refused to let us {attempt} ({(int)response.StatusCode}).");

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
