#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Import;
using JukeBox.Game.Replays;
using osu.Framework.Logging;

namespace JukeBox.Game.Online;

/// <summary>
/// One watched player, as the UI reads them: the two facts of different quality kept apart (see
/// <see cref="SpectatePresence"/>), plus whatever we have managed to fetch.
/// </summary>
/// <param name="Username">The name as typed, or as osu! spells it once resolved.</param>
/// <param name="UserId">Their osu! id, or null before the first successful resolve.</param>
/// <param name="Presence">Online/offline — REAL.</param>
/// <param name="Activity">What they seem to be doing — INFERRED from their newest play.</param>
/// <param name="Entry">The play currently loaded for them, or null when there is nothing to show.</param>
/// <param name="Note">Why there is nothing to show, when that needs saying — a failed lookup, a
/// play whose beatmap could not be fetched, or a wait on the download budget.</param>
public readonly record struct WatchedPlayer(
    string Username,
    int? UserId,
    SpectatePresence Presence,
    SpectateState Activity,
    SpectateEntry? Entry,
    string? Note)
{
    /// <summary>The one-line status the row shows: the real dot's word, then the inferred one.</summary>
    public string Status => Note ?? SpectateStateRules.Describe(Presence, Activity);
}

/// <summary>
/// The spectating engine: who is being watched, what they last did, and which of their plays are
/// currently loaded for rendering.
///
/// <para>
/// A plain class with an explicit <see cref="PollAsync"/> rather than a self-driving loop, so a
/// test can run exactly one round, move a clock, and run another — which is the only practical way
/// to prove things like "the same score is never downloaded twice" and "the budget is never
/// exceeded". <see cref="SpectateController"/> is the thing that decides WHEN to call it.
/// </para>
///
/// <para>
/// What this is NOT: live spectating. Every round asks osu! for each player's most recent COMPLETED
/// play and, when that play is new, downloads its replay and renders it from the start. So the wall
/// shows what people have just finished doing, seconds to a couple of minutes behind them, and the
/// state chips say so honestly (see <see cref="SpectateState"/>). The live route is the spectator
/// hub, which is first-party-only — see <c>.superpowers/spectate-research.md</c>.
/// </para>
/// </summary>
public sealed class SpectateSession
{
    /// <summary>
    /// Downloaded replays older than this are deleted at the end of a round. Spectating fetches a
    /// file per play per player, so without a sweep the folder grows without bound for a feature
    /// whose files stop being interesting the moment the next play lands.
    /// </summary>
    public static readonly TimeSpan REPLAY_RETENTION = TimeSpan.FromHours(6);

    private readonly ISpectateApi api;
    private readonly ReplayDownloadBudget budget;
    private readonly Func<int, CancellationToken, Task<CachedBeatmapSet>> fetchSet;
    private readonly string replayDirectory;
    private readonly Func<DateTimeOffset> clock;

    private readonly object sync = new object();

    /// <summary>Watched players in the order they were added, keyed by name for lookup.</summary>
    private readonly List<PlayerState> players = new List<PlayerState>();

    /// <param name="api">osu!, or a fake.</param>
    /// <param name="budget">The replay-download allowance. Shared with nothing else.</param>
    /// <param name="fetchSet">Downloads-or-hits a beatmap set — <see cref="BeatmapCache.GetAsync"/>
    /// in production, injected so tests need no network and no archive.</param>
    /// <param name="replayDirectory">Where downloaded .osr files live.</param>
    /// <param name="clock">Current time. Injected so the state windows and the budget can be driven
    /// deterministically.</param>
    public SpectateSession(ISpectateApi api, ReplayDownloadBudget budget,
                           Func<int, CancellationToken, Task<CachedBeatmapSet>> fetchSet,
                           string replayDirectory, Func<DateTimeOffset>? clock = null)
    {
        this.api = api;
        this.budget = budget;
        this.fetchSet = fetchSet;
        this.replayDirectory = replayDirectory;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Sets the watch list, keeping everything already known about the names that stay.
    ///
    /// <para>
    /// Preserving state across an edit is what stops removing one player from re-resolving and
    /// re-downloading for everybody else — an edit would otherwise cost the whole download budget.
    /// </para>
    /// </summary>
    public void SetWatched(IReadOnlyList<string> usernames)
    {
        lock (sync)
        {
            var existing = players.ToList();
            players.Clear();

            foreach (string username in usernames)
            {
                var kept = existing.FirstOrDefault(p => string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
                players.Add(kept ?? new PlayerState(username));
            }
        }
    }

    /// <summary>Every watched player, in list order — what the settings rows render.</summary>
    public IReadOnlyList<WatchedPlayer> Players
    {
        get
        {
            lock (sync)
                return players.Select(p => p.Snapshot()).ToList();
        }
    }

    /// <summary>
    /// The plays that get a pane: those with something loaded and an activity worth rendering,
    /// newest first, capped at <see cref="SpectatePanePlan.MAX_PANES"/>.
    ///
    /// <para>
    /// Newest-first IS the rotation the cap needs. With more watched players than panes, ordering by
    /// when each play finished means the wall follows whoever is actually playing: a fifth player
    /// who lands a score displaces the stalest of the four rather than waiting for a free slot.
    /// </para>
    ///
    /// <para>
    /// Recency SELECTS but does not ORDER: the four that survive the cap come back sorted by name.
    /// Otherwise every landed score would reshuffle the wall, and a pane cannot be reordered without
    /// being rebuilt — which restarts its audio. Sorting by something that doesn't move means a
    /// player stays in the same cell for as long as they are on it.
    /// </para>
    /// </summary>
    public IReadOnlyList<SpectateEntry> Rendered
    {
        get
        {
            lock (sync)
            {
                var candidates = players.Where(p => p.Entry != null && SpectateStateRules.ShouldRender(p.Activity))
                                        .OrderByDescending(p => p.EndedAt ?? DateTimeOffset.MinValue)
                                        .Select(p => p.Entry!.Value)
                                        .ToList();

                return SpectatePanePlan.Rendered(candidates)
                                       .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                                       .ToList();
            }
        }
    }

    /// <summary>
    /// Runs one round: resolve new names, refresh presence for everyone, read each player's newest
    /// play, and download whatever is both new and affordable.
    ///
    /// <para>
    /// Never throws for an ordinary failure. A poll that dies on one player's request would take
    /// the whole wall down with it, so each failure is recorded against the player it belongs to
    /// and the round carries on.
    /// </para>
    /// </summary>
    /// <returns>Whether anything visible changed, so the UI can skip a rebuild when nothing did.</returns>
    public async Task<bool> PollAsync(CancellationToken ct = default)
    {
        var now = clock();

        var watched = snapshotStates();

        if (watched.Count == 0)
            return false;

        bool changed = false;

        foreach (var player in watched)
        {
            if (player.UserId != null || player.Activity == SpectateState.Unknown_User)
                continue;

            changed |= await resolveAsync(player, ct).ConfigureAwait(false);
        }

        changed |= await refreshPresenceAsync(watched, ct).ConfigureAwait(false);

        foreach (var player in watched)
        {
            if (player.UserId == null)
                continue;

            changed |= await refreshPlayAsync(player, now, ct).ConfigureAwait(false);
        }

        sweepOldReplays(now);

        return changed;
    }

    private List<PlayerState> snapshotStates()
    {
        lock (sync)
            return players.ToList();
    }

    private async Task<bool> resolveAsync(PlayerState player, CancellationToken ct)
    {
        try
        {
            var user = await api.ResolveUserAsync(player.Username, ct).ConfigureAwait(false);

            lock (sync)
            {
                if (user == null)
                {
                    // A name that does not exist is settled, not pending: leaving it Unknown would
                    // make every future round re-ask osu! the same question forever.
                    player.Activity = SpectateState.Unknown_User;
                    player.Note = "no such player";
                    return true;
                }

                player.UserId = user.Value.Id;
                player.Username = user.Value.Username;
                player.Presence = user.Value.Presence;
                player.Note = null;
            }

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return note(player, describe(e, $"couldn't look up {player.Username}"));
        }
    }

    private async Task<bool> refreshPresenceAsync(IReadOnlyList<PlayerState> watched, CancellationToken ct)
    {
        var ids = watched.Where(p => p.UserId != null).Select(p => p.UserId!.Value).ToList();

        if (ids.Count == 0)
            return false;

        try
        {
            var users = await api.PresenceAsync(ids, ct).ConfigureAwait(false);

            bool changed = false;

            lock (sync)
            {
                foreach (var user in users)
                {
                    var player = watched.FirstOrDefault(p => p.UserId == user.Id);

                    if (player == null || player.Presence == user.Presence)
                        continue;

                    player.Presence = user.Presence;
                    changed = true;
                }
            }

            return changed;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Presence is the garnish, not the feature: log it and let the round get on with the
            // plays, which is what people actually came to watch.
            Logger.Log($"[spectate] presence check failed: {e.Message}", level: LogLevel.Debug);
            return false;
        }
    }

    private async Task<bool> refreshPlayAsync(PlayerState player, DateTimeOffset now, CancellationToken ct)
    {
        SpectateScore? latest;

        try
        {
            latest = await api.LatestScoreAsync(player.UserId!.Value, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return note(player, describe(e, $"couldn't read {player.Username}'s recent plays"));
        }

        var activity = SpectateStateRules.For(latest?.EndedAt, latest?.Passed ?? false, now);

        bool changed;

        lock (sync)
        {
            changed = player.Activity != activity;
            player.Activity = activity;
            player.EndedAt = latest?.EndedAt;

            // A play that has aged out of the active window gives up what it was holding. The
            // WALL would drop it either way (Rendered filters on the same rule), so what this
            // actually does is release the replay: an entry still referenced here is protected
            // from sweepOldReplays forever, and a player who stopped playing yesterday would
            // otherwise pin their .osr for as long as the app ran.
            //
            // Its note goes with it: a note explains why an expected pane is missing, and once
            // nothing is expected there is nothing left to explain.
            //
            // Notes are otherwise left ALONE here rather than cleared each round, because the thing
            // most of them describe — a beatmap that will not download, a play with no replay — is
            // still true on the next round, and clearing it would flash the row back to "playing"
            // twenty seconds at a time while nothing was actually showing.
            if (!SpectateStateRules.ShouldRender(activity))
            {
                if (player.Entry != null)
                {
                    player.Entry = null;
                    changed = true;
                }

                if (player.Note != null)
                {
                    player.Note = null;
                    changed = true;
                }
            }
        }

        if (latest == null || latest.Value.ScoreId == player.LoadedScoreId)
            return changed;

        if (!latest.Value.HasReplay)
            return note(player, "their latest play has no replay to watch") || changed;

        if (!SpectateStateRules.ShouldRender(activity))
            return changed;

        if (!budget.TryTake(now))
        {
            // Not an error and not a permanent state — the same play is retried next round, by
            // which point the sliding window will usually have room.
            return note(player, budget.IsThrottled(now) ? "waiting out osu!'s download limit" : "waiting for a download slot") || changed;
        }

        return await loadPlayAsync(player, latest.Value, ct).ConfigureAwait(false) || changed;
    }

    private async Task<bool> loadPlayAsync(PlayerState player, SpectateScore score, CancellationToken ct)
    {
        string replayPath = Path.Combine(replayDirectory, $"{score.ScoreId.ToString(CultureInfo.InvariantCulture)}.osr");

        try
        {
            if (!File.Exists(replayPath))
                await api.DownloadReplayAsync(score.ScoreId, replayPath, ct).ConfigureAwait(false);

            var cached = await fetchSet(score.BeatmapSetId, ct).ConfigureAwait(false);

            string? osuFile = DroppedFileImporter.ResolveDifficulty(cached, score.BeatmapChecksum, score.DifficultyName);

            if (osuFile == null)
                return note(player, "couldn't match the difficulty they played");

            var decoded = new JukeBoxScoreDecoder(osuFile).Decode(replayPath);

            var mods = ReplayMods.ForGameplay(decoded);
            var (rateTempo, rateFrequency) = ReplayMods.TrackAdjustmentsFor(mods);

            var attachment = new ReplayAttachment
            {
                PlayerName = player.Username,
                SourcePath = replayPath,
                BeatmapMd5 = score.BeatmapChecksum,
                OsuFile = osuFile,
                Score = decoded,
                ModAcronyms = ReplayMods.Acronyms(mods),
                RateTempo = rateTempo,
                RateFrequency = rateFrequency,
                PlayedAt = score.EndedAt,
            };

            lock (sync)
            {
                player.LoadedScoreId = score.ScoreId;
                player.Entry = new SpectateEntry(cached.Directory, osuFile, attachment, player.Username);
                player.Note = null;
            }

            return true;
        }
        catch (SpectateThrottledException)
        {
            budget.Throttled(clock());
            return note(player, "waiting out osu!'s download limit");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The score id is recorded as loaded even though it failed, so a play whose beatmap
            // simply cannot be fetched is not retried every round for as long as it stays newest.
            lock (sync)
                player.LoadedScoreId = score.ScoreId;

            return note(player, describe(e, $"couldn't load {player.Username}'s play"));
        }
    }

    /// <summary>
    /// Deletes replays we are no longer showing. Runs at the END of a round so a file downloaded
    /// this round can never be swept before the pane that wants it has been built.
    /// </summary>
    private void sweepOldReplays(DateTimeOffset now)
    {
        try
        {
            if (!Directory.Exists(replayDirectory))
                return;

            var keep = new HashSet<string>(
                snapshotStates().Where(p => p.Entry != null).Select(p => p.Entry!.Value.Replay.SourcePath),
                StringComparer.Ordinal);

            foreach (string file in Directory.EnumerateFiles(replayDirectory, "*.osr"))
            {
                if (keep.Contains(file) || now - File.GetLastWriteTimeUtc(file) < REPLAY_RETENTION)
                    continue;

                File.Delete(file);
            }
        }
        catch (Exception e)
        {
            Logger.Log($"[spectate] couldn't tidy old replays: {e.Message}", level: LogLevel.Debug);
        }
    }

    private bool note(PlayerState player, string message)
    {
        lock (sync)
        {
            if (player.Note == message)
                return false;

            player.Note = message;
            return true;
        }
    }

    /// <summary>
    /// What to show for a thrown failure. Our own exceptions already carry a sentence written for a
    /// person; anything else (a socket dying, a disk refusing) does not, and its raw
    /// <see cref="Exception.Message"/> in a status row is noise.
    /// </summary>
    private static string describe(Exception e, string fallback)
        => e is SpectateApiException ? e.Message : fallback;

    /// <summary>The mutable half, never handed out — callers get <see cref="WatchedPlayer"/>.</summary>
    private sealed class PlayerState
    {
        public string Username;
        public int? UserId;
        public SpectatePresence Presence = SpectatePresence.Unknown;
        public SpectateState Activity = SpectateState.Unknown;
        public SpectateEntry? Entry;
        public string? Note;

        /// <summary>The newest score id we have DOWNLOADED — the guard against re-fetching one.</summary>
        public long? LoadedScoreId;

        /// <summary>When their newest known play ended, which orders the pane rotation.</summary>
        public DateTimeOffset? EndedAt;

        public PlayerState(string username)
        {
            Username = username;
        }

        public WatchedPlayer Snapshot() => new WatchedPlayer(Username, UserId, Presence, Activity, Entry, Note);
    }
}
