#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Configuration;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace JukeBox.Game.Online;

/// <summary>
/// The beatmap-search state machine shared by both listing presentations (the compact sidebar
/// <see cref="UI.BeatmapListingOverlay"/>, which only renders results, and the
/// <see cref="UI.FullscreenListingOverlay"/> the user actually searches and filters in):
/// a debounced keyword search refined by the filter bindables below and paginated with
/// infinite-scroll semantics. Extracted from the (previously monolithic) listing overlay so the two
/// presentations are pure views over one engine — their filter chips bind the same bindables (so
/// selections stay in sync across views), their card grids rebuild off the same
/// <see cref="ResultsChanged"/> event, and no search logic is duplicated.
///
/// TWO BACKENDS, picked by <see cref="Api"/> (the user's "Search API" setting), differing in what
/// they can express and therefore in how they page:
///
/// <list type="bullet">
/// <item><see cref="SearchApi.Mirror"/> (the default) — <see cref="IBeatmapMirror"/>'s legacy
/// search, paged by <see cref="currentPage"/>. It cannot express genre or language, so
/// <see cref="GenreId"/>/<see cref="LanguageId"/> are applied CLIENT-SIDE over the already-loaded
/// results: flipping them never issues a request directly, but if the filtered results leave a
/// view's viewport underfilled (possibly empty) while more pages exist, further pages are
/// auto-chained via <see cref="UpdatePaging"/>, bounded by <see cref="max_auto_chain_pages"/> per
/// user action.</item>
/// <item><see cref="SearchApi.Official"/> — <see cref="OfficialBeatmapSearch"/>, paged by the
/// endpoint's own cursor. Every filter is a real server-side parameter, so there is no client-side
/// sieve and no auto-chaining on this path at all, <see cref="HasMore"/> is exact rather than
/// guessed from a short page, and <see cref="TotalResults"/> is real. A failure here (missing or
/// rejected credentials, rate limit, network) never dead-ends the listing: the same request is
/// re-run against the mirror and the reason is surfaced through <see cref="Status"/> and
/// <see cref="LastError"/>.</item>
/// </list>
///
/// Every other filter (query, mode, category, extras, sort, stars) is a server-side parameter on
/// both paths — changing any of them restarts the search from the first page.
///
/// A slower, older search response can never overwrite a newer one (<see cref="searchSequence"/>
/// guard). A <see cref="Component"/> so the debounce runs on this drawable's own scheduler —
/// hosted by whoever owns it (<see cref="Screens.MainScreen"/> in the app, so the engine keeps
/// ticking regardless of which listing is on screen; a self-owning listing in bare test scenes).
/// Results outlive any one view, which is what lets the sidebar keep showing whatever the user
/// last searched for in the fullscreen listing after closing it.
/// </summary>
public partial class BeatmapSearchEngine : Component
{
    /// <summary>Keystroke debounce on the mirror path — the mirrors publish no rate policy.</summary>
    private const double mirror_debounce_ms = 300;

    /// <summary>
    /// Keystroke debounce on the official path. osu!'s terms ask for no more than roughly one
    /// request per second (the enforced ceiling is far higher, but exceeding the stated limit is
    /// grounds for revoking a token), so 300ms — which a fast typist beats — is not an acceptable
    /// floor there.
    /// </summary>
    private const double official_debounce_ms = 500;

    internal const int PAGE_SIZE = 30;

    /// <summary>
    /// Cap on consecutive automatically-chained page fetches (pages requested without the user
    /// scrolling — e.g. when the client-side genre/language filter reduces every loaded page to
    /// fewer cards than fill the viewport, possibly zero). Bounded per user action so an
    /// all-filtered result stream can't hammer the mirror indefinitely; the budget refreshes
    /// whenever content becomes scrollable again or the user changes the search/filters.
    /// The client-side sieve it exists for is mirror-only, but the bound applies to both backends:
    /// any stream of pages too short to fill a viewport would otherwise be fetched without limit.
    /// </summary>
    private const int max_auto_chain_pages = 5;

    // ---- Backend -----------------------------------------------------------------------------

    /// <summary>
    /// Which backend answers searches. Bound to the persisted setting when a config manager is in
    /// DI (the app); left free-standing in bare test scenes. Changing it restarts the search.
    /// </summary>
    public readonly Bindable<SearchApi> Api = new Bindable<SearchApi>(SearchApi.Mirror);

    // ---- Query + server-side filters (any change restarts the search) --------------------------

    public readonly Bindable<string> Query = new Bindable<string>(string.Empty);

    /// <summary>Ruleset filter as the mirrors' letter ("o"/"t"/"c"/"m"), null = any. The official
    /// backend re-encodes it as osu!'s ruleset int (see <see cref="OfficialBeatmapSearch.ModeInt"/>).</summary>
    public readonly Bindable<string?> Mode = new Bindable<string?>();

    /// <summary>Status filter, in the mirrors' spelling ("all" for any).</summary>
    public readonly Bindable<string> Category = new Bindable<string>("ranked");

    public readonly BindableBool HasVideo = new BindableBool();
    public readonly BindableBool HasStoryboard = new BindableBool();

    /// <summary>`sort` = {key}_{asc|desc}, split across these two bindables.</summary>
    public readonly Bindable<string> SortKey = new Bindable<string>("ranked");

    public readonly BindableBool SortDescending = new BindableBool(true);

    public readonly BindableDouble MinStars = new BindableDouble { MinValue = 0, MaxValue = 10, Precision = 0.1 };
    public readonly BindableDouble MaxStars = new BindableDouble(10) { MinValue = 0, MaxValue = 10, Precision = 0.1 };

    // ---- Genre/language: server-side on the official path, client-side on the mirror path -------

    public readonly Bindable<int?> GenreId = new Bindable<int?>();
    public readonly Bindable<int?> LanguageId = new Bindable<int?>();

    // ---- Results ------------------------------------------------------------------------------

    private readonly List<BeatmapSetInfo> loadedSets = new List<BeatmapSetInfo>();

    /// <summary>Every set loaded so far, unfiltered.</summary>
    public IReadOnlyList<BeatmapSetInfo> LoadedSets => loadedSets;

    /// <summary>
    /// What views render: the loaded sets, passed through the client-side genre/language filters
    /// only when the loaded results came from a MIRROR (which can't express those filters). Keyed
    /// on where the results actually came from rather than on <see cref="Api"/>, so a fallback to
    /// the mirror still gets its filtering.
    /// </summary>
    public IEnumerable<BeatmapSetInfo> VisibleSets => loadedFromOfficial
        ? loadedSets
        : loadedSets.Where(s => MatchesClientFilters(s, GenreId.Value, LanguageId.Value));

    /// <summary>
    /// True while a request is in flight. A bindable (rather than a plain flag) because every view
    /// renders it as a real <c>LoadingSpinner</c>/<c>LoadingLayer</c> rather than status text —
    /// see <see cref="LoadingFresh"/> for which of the two.
    /// </summary>
    public readonly BindableBool IsLoading = new BindableBool();

    /// <summary>
    /// True while the in-flight request is a FRESH search (first page) rather than a "load more"
    /// page append. Views cover their whole result area for a fresh search (everything is about to
    /// be replaced) but only show a footer spinner while appending (the existing cards stay valid).
    /// </summary>
    public readonly BindableBool LoadingFresh = new BindableBool();

    public bool HasMore { get; private set; }

    /// <summary>
    /// Total upstream matches for the current search, or null when the backend doesn't report one
    /// (the mirrors don't — a page is all they say).
    /// </summary>
    public int? TotalResults { get; private set; }

    /// <summary>Human-readable search lifecycle line ("no results", …), shown by every view's
    /// status text. Deliberately says nothing about being busy — that's the spinners' job.</summary>
    public readonly Bindable<string> Status = new Bindable<string>(string.Empty);

    /// <summary>
    /// Set to the reason the OFFICIAL backend failed each time a search falls back to the mirror,
    /// so the app can raise a toast for something the user has to act on (bad credentials) rather
    /// than leaving it in a status line they may not be looking at. Null while nothing has failed.
    /// </summary>
    public readonly Bindable<string?> LastError = new Bindable<string?>();

    /// <summary>
    /// Names the mirror that answered the last search WITHOUT being able to apply its filters, or
    /// null when the results really are filtered. Only reachable on the mirror backend, and only
    /// once every mirror that could express the filters has failed (see <see cref="MirrorChain"/>).
    /// </summary>
    public readonly Bindable<string?> FiltersDroppedBy = new Bindable<string?>();

    /// <summary>
    /// Which filters the backend about to serve the next search can actually express — the listing
    /// shows exactly these rows and hides the rest, rather than offering controls that would be
    /// silently ignored. Moves with the backend setting AND with mirror health, so a row can vanish
    /// when the only mirror that could apply it goes down and return when it recovers. The
    /// corresponding bindables keep their values throughout; they are simply not sent (see
    /// <see cref="BuildRequest"/>).
    /// </summary>
    public readonly Bindable<SearchFilters> AvailableFilters = new Bindable<SearchFilters>(SearchFilters.All);

    /// <summary>
    /// The rendered result set changed — fired on the update thread. The argument is true for a
    /// FRESH search (views should scroll back to the top), false for a page append or a
    /// client-side filter change (views rebuild in place).
    /// </summary>
    public event Action<bool>? ResultsChanged;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    // Absent in bare test scenes (and in any build with no credentials wired) — the official path
    // then behaves exactly as it does on a failure: it falls back to the mirror and says why.
    [Resolved(canBeNull: true)]
    private OfficialBeatmapSearch? officialSearch { get; set; }

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    /// <summary>The persisted "Search API" setting <see cref="Api"/> follows. A field purely to keep
    /// it alive — see <see cref="load"/>.</summary>
    private Bindable<SearchApi>? apiConfig;

    private ScheduledDelegate? debounceDelegate;

    // Guards against a slower, older search response overwriting the results of a newer one that
    // happened to complete first. Bumped by every fetch (fresh search or next page), so a stale
    // page-append can't interleave with a newer fresh search either.
    private int searchSequence;

    private int currentPage;

    /// <summary>The official endpoint's next-page cursor; null when there is no next page.</summary>
    private string? nextCursor;

    /// <summary>
    /// Whether <see cref="loadedSets"/> came from the official backend. Distinct from
    /// <c>Api.Value == Official</c>, which only says what was ASKED for — a fallback leaves the
    /// setting on Official while the loaded results are the mirror's, and those still need the
    /// client-side genre/language filtering the mirror can't do.
    /// </summary>
    private bool loadedFromOfficial;

    /// <summary>Auto-chained fetches consumed since the last user action — see
    /// <see cref="max_auto_chain_pages"/>.</summary>
    private int autoChainedPages;

    /// <summary>The keystroke debounce currently in force — longer on the official backend, whose
    /// terms of use ask for roughly one request per second. Internal so tests can assert the two
    /// values without waiting them out in real time.</summary>
    internal double DebounceMs => Api.Value == SearchApi.Official ? official_debounce_ms : mirror_debounce_ms;

    /// <summary>Test-only view of where the currently loaded results came from — see
    /// <see cref="loadedFromOfficial"/>, which is what distinguishes a successful official search
    /// from one that silently fell back to the mirror.</summary>
    internal bool LoadedFromOfficial => loadedFromOfficial;

    // Subscriptions live in load() (synchronous with being added to the tree), NOT LoadComplete:
    // a floating listing starts hidden, and a hidden (non-present) parent defers its children's
    // LoadComplete until the first visible frame — by which time ShowWithInitialChar has already
    // seeded Query, so a LoadComplete-registered callback would miss that first change and the
    // initial search would never fire.
    [BackgroundDependencyLoader]
    private void load()
    {
        // Bound BEFORE the subscription below so adopting the persisted value at startup isn't
        // itself treated as the user switching backends.
        if (config != null)
        {
            // The returned bindable is kept in a FIELD, never bound to inline. ConfigManager hands
            // back a bound COPY that it only references weakly, so a copy nobody else keeps alive
            // is collected — after which the setting still reads correctly at startup but stops
            // propagating, and changing "Search API" in settings silently does nothing.
            apiConfig = config.GetBindable<SearchApi>(JukeBoxSetting.SearchApi);
            Api.BindTo(apiConfig);
        }

        Api.BindValueChanged(_ =>
        {
            updateAvailableFilters();
            ScheduleSearch();
        });

        updateAvailableFilters();

        Query.BindValueChanged(_ => ScheduleSearch());
        Mode.BindValueChanged(_ => ScheduleSearch());
        Category.BindValueChanged(_ => ScheduleSearch());
        HasVideo.BindValueChanged(_ => ScheduleSearch());
        HasStoryboard.BindValueChanged(_ => ScheduleSearch());
        SortKey.BindValueChanged(_ => ScheduleSearch());
        SortDescending.BindValueChanged(_ => ScheduleSearch());
        MinStars.BindValueChanged(_ => ScheduleSearch());
        MaxStars.BindValueChanged(_ => ScheduleSearch());

        // Genre/language: a real search parameter on the official backend, a filter over loaded
        // results on the mirror one (which can't express it) — see the class summary.
        GenreId.BindValueChanged(_ => applyGenreOrLanguageChange());
        LanguageId.BindValueChanged(_ => applyGenreOrLanguageChange());
    }

    /// <summary>Kicks off a debounced fresh search, superseding any pending one. The debounce is
    /// longer on the official backend — see <see cref="official_debounce_ms"/>.</summary>
    public void ScheduleSearch()
    {
        debounceDelegate?.Cancel();
        debounceDelegate = Scheduler.AddDelayed(() => { _ = runSearchAsync(fresh: true); }, DebounceMs);
    }

    /// <summary>Cancels a pending debounced search — called when a floating view closes, so the
    /// search doesn't fire (and mutate results) after the overlay is gone.</summary>
    public void CancelPendingSearch() => debounceDelegate?.Cancel();

    /// <summary>
    /// Per-frame paging driver, called from a visible view's Update with its own scroll geometry.
    /// When content overflows the viewport, further paging is user-driven (infinite scroll:
    /// <paramref name="nearEnd"/>) and the auto-chain budget refreshes; when it underfills, the
    /// next page is chained automatically, bounded by <see cref="max_auto_chain_pages"/> per user
    /// action. On the mirror path an underfilled viewport usually means the client-side
    /// genre/language filter thinned the loaded pages — which is what exhausting the budget
    /// reports; on the official path it just means a viewport taller than one page.
    /// </summary>
    public void UpdatePaging(bool contentOverflows, bool nearEnd)
    {
        // Driven off the RAW loaded count, not the filtered count: the client-side filter can
        // legitimately reduce a full page to zero visible cards, and later pages may still match.
        if (IsLoading.Value || !HasMore || loadedSets.Count == 0)
            return;

        if (contentOverflows)
        {
            autoChainedPages = 0;

            if (nearEnd)
                _ = runSearchAsync(fresh: false);
        }
        else if (autoChainedPages < max_auto_chain_pages)
        {
            autoChainedPages++;
            _ = runSearchAsync(fresh: false);
        }
        else if (!loadedFromOfficial && !VisibleSets.Any())
        {
            // Mirror-only wording: it names the client-side sieve as the thing to loosen, which is
            // the only reason a page of loaded results can render as nothing on that path.
            Status.Value = $"no matches in the first {loadedSets.Count} loaded results — refine the filters or keywords";
        }
    }

    internal SearchRequest BuildRequest(int page, string? cursor = null)
    {
        double? min = MinStars.Value > 0 ? MinStars.Value : null;
        double? max = MaxStars.Value < 10 ? MaxStars.Value : null;

        // The two sliders are independent; if they cross, treat the crossed pair as an
        // empty-but-valid band rather than sending an inverted range.
        if (min != null && max != null && min > max)
            (min, max) = (max, min);

        // Filters the active backend can't express are left OUT of the request rather than sent to
        // be ignored — sending one would only push the chain onto a mirror that can't serve it. The
        // BINDABLES keep their values (the rows are merely hidden, see AvailableFilters), so a
        // filter comes back exactly as the user left it the moment a backend that can apply it
        // returns.
        var available = AvailableFilters.Value;

        bool can(SearchFilters filter) => (available & filter) != 0;

        return new SearchRequest
        {
            Query = can(SearchFilters.Keyword) ? Query.Value : string.Empty,
            Page = can(SearchFilters.Paging) ? page : 0,
            PageSize = PAGE_SIZE,
            Status = can(SearchFilters.Status) ? Category.Value : SearchRequest.ANY_STATUS,
            Sort = can(SearchFilters.Sort)
                ? $"{SortKey.Value}_{(SortDescending.Value ? "desc" : "asc")}"
                : SearchRequest.DEFAULT_SORT,
            Mode = can(SearchFilters.Mode) ? Mode.Value : null,
            Extra = !can(SearchFilters.Extra) ? SearchExtra.None
                : HasVideo.Value && HasStoryboard.Value ? SearchExtra.VideoAndStoryboard
                : HasVideo.Value ? SearchExtra.Video
                : HasStoryboard.Value ? SearchExtra.Storyboard
                : SearchExtra.None,
            MinStars = can(SearchFilters.Stars) ? min : null,
            MaxStars = can(SearchFilters.Stars) ? max : null,
            GenreId = can(SearchFilters.Genre) ? GenreId.Value : null,
            LanguageId = can(SearchFilters.Language) ? LanguageId.Value : null,
            Cursor = can(SearchFilters.Paging) ? cursor : null,
            // Matching the mirrors, which apply no explicit-content filter of their own: the
            // official default for a user-less token would otherwise silently hide sets the mirror
            // backend shows, making the two backends disagree for no reason the user can see.
            IncludeNsfw = true,
        };
    }

    /// <summary>
    /// Recomputes what the backend about to serve the next search can actually apply. Called at
    /// load, whenever the backend setting changes, and after every search — the mirror set's
    /// capability moves with mirror HEALTH (see <see cref="MirrorHealth"/>), so a mirror failing or
    /// recovering is precisely when a row needs to disappear or come back.
    /// </summary>
    private void updateAvailableFilters()
    {
        var previous = AvailableFilters.Value;

        AvailableFilters.Value = Api.Value == SearchApi.Official
            // osu!'s own API expresses the entire filter block.
            ? SearchFilters.All
            : mirror.SupportedFilters;

        if (AvailableFilters.Value == previous)
            return;

        // The offer just changed, which retires any standing "these filters were dropped" notice:
        // it described a request built against the OLD set, and the rows it apologised for are no
        // longer on screen to apologise about. A genuine drop on the next search raises it again.
        if (FiltersDroppedBy.Value != null)
        {
            FiltersDroppedBy.Value = null;
            updateStatus(null, null);
        }
    }

    private async Task runSearchAsync(bool fresh)
    {
        int mySequence = ++searchSequence;
        int page = fresh ? 0 : currentPage + 1;
        string? cursor = fresh ? null : nextCursor;

        IsLoading.Value = true;
        LoadingFresh.Value = fresh;

        // Cleared rather than set to a "searching…" line: a spinner carries "busy" now, and a
        // stale "no results" left showing underneath it would contradict it.
        Schedule(() => Status.Value = string.Empty);

        // Refreshed immediately BEFORE the request is built, not only after it lands: mirror health
        // can have moved since the last search (a failed download counts too), and the request must
        // be shaped by what will serve THIS search rather than what served the previous one.
        updateAvailableFilters();

        var request = BuildRequest(page, cursor);

        List<BeatmapSetInfo> results;
        string? resultCursor = null;
        int? total = null;
        string? error = null;
        bool fromOfficial = false;

        // Only the mirror path can land here: no mirror that could express the filters answered, so
        // the results on screen are broader than the filter rows claim and the user has to be told.
        // Assigned from the request's callback, which runs on whatever thread the search completed on.
        string? unfilteredBy = null;
        request.OnFiltersDropped = name => unfilteredBy = name;

        if (Api.Value == SearchApi.Official)
        {
            try
            {
                if (officialSearch == null)
                    throw new OfficialSearchException("official osu! API search is unavailable in this session");

                var official = await officialSearch.SearchAsync(request).ConfigureAwait(false);

                results = official.Sets;
                resultCursor = official.CursorString;
                total = official.Total;
                fromOfficial = true;
            }
            catch (Exception ex)
            {
                // Every official failure is recoverable by simply asking the mirrors the same
                // question, so the listing never dead-ends — but it IS logged and surfaced, since
                // silently serving worse results would be indistinguishable from the setting doing
                // nothing at all.
                error = ex is OfficialSearchException ? ex.Message : "official osu! API search failed";
                Logger.Log($"Official osu! search failed ({ex.Message}) — falling back to the beatmap mirror.", level: LogLevel.Important);

                results = await mirrorSearchAsync(request).ConfigureAwait(false);
            }
        }
        else
        {
            results = await mirrorSearchAsync(request).ConfigureAwait(false);
        }

        Schedule(() =>
        {
            // A newer search superseded this one while it was in flight — drop this response
            // (the superseding search resets IsLoading when it completes).
            if (mySequence != searchSequence)
                return;

            IsLoading.Value = false;
            currentPage = page;
            nextCursor = resultCursor;
            loadedFromOfficial = fromOfficial;

            // A search is the only thing that changes mirror health, so it is also the only thing
            // that can change which rows the listing should be offering.
            var offerBefore = AvailableFilters.Value;
            updateAvailableFilters();

            // A drop discovered by THIS search, which also narrowed the offer, needs no notice: the
            // rows it would apologise for have just been taken off screen, and the next search
            // won't ask for them. Reporting it anyway is what left "osu.direct can't apply these
            // filters" sitting under a filter block that no longer had any filters in it.
            if (AvailableFilters.Value != offerBefore)
                unfilteredBy = null;

            // The total describes the SEARCH, not the page, so it is captured once and left alone
            // while paging: osu!'s endpoint sometimes answers a deeper page with Elasticsearch's
            // capped estimate (10,000) instead of the real count, and letting that overwrite the
            // first page's figure made the status line drop from "56,325 results" to "10,000
            // results" mid-scroll. Observed live against osu.ppy.sh.
            if (fresh)
                TotalResults = total;

            // Exact on the official path (the cursor IS the "is there more" answer); a guess on the
            // mirror one, where a full page is the only hint that another may exist.
            HasMore = fromOfficial ? resultCursor != null : results.Count >= PAGE_SIZE;

            if (fresh)
            {
                loadedSets.Clear();

                // A fresh search is a user action — the auto-chain budget starts over.
                autoChainedPages = 0;
            }

            loadedSets.AddRange(results);
            updateStatus(error, unfilteredBy);
            LastError.Value = error;
            FiltersDroppedBy.Value = unfilteredBy;
            ResultsChanged?.Invoke(fresh);
        });
    }

    private async Task<List<BeatmapSetInfo>> mirrorSearchAsync(SearchRequest request)
    {
        try
        {
            return await mirror.SearchAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Beatmap search failed");
            return new List<BeatmapSetInfo>();
        }
    }

    /// <summary>
    /// Genre/language changed. On the official backend they are real parameters, so this is an
    /// ordinary (debounced) search; on the mirror backend nothing is requested — the loaded results
    /// are re-filtered in place, and the auto-chain budget resets because this is a user action.
    /// </summary>
    private void applyGenreOrLanguageChange()
    {
        if (Api.Value == SearchApi.Official)
        {
            ScheduleSearch();
            return;
        }

        autoChainedPages = 0;
        updateStatus(null, FiltersDroppedBy.Value);
        ResultsChanged?.Invoke(false);
    }

    private void updateStatus(string? error, string? unfilteredBy)
    {
        if (error != null)
        {
            Status.Value = $"{error} — showing mirror results";
            return;
        }

        if (!VisibleSets.Any())
        {
            Status.Value = loadedSets.Count == 0 ? "no results" : "no results (client-side genre/language filter)";
            return;
        }

        // Louder than a result count, because it means the visible results are BROADER than the
        // filter rows say — the alternative (staying quiet) is what made the filters look broken.
        if (unfilteredBy != null)
        {
            Status.Value = $"{unfilteredBy} can't apply these filters — showing unfiltered results";
            return;
        }

        // Only the official backend reports a real total; the mirrors say nothing beyond the page
        // they served, so their listing keeps the status line empty on success as before.
        Status.Value = TotalResults is int count ? $"{count:N0} results" : string.Empty;
    }

    /// <summary>
    /// Whether <paramref name="set"/> passes the client-side genre/language filters — the legacy
    /// mirror search can't express either, so on THAT backend they're applied to loaded results
    /// instead (the official backend filters server-side and never reaches this). A set with no
    /// genre/language assigned (NeriNyan serves nulls) only passes the "Any" filter.
    /// </summary>
    internal static bool MatchesClientFilters(BeatmapSetInfo set, int? genreId, int? languageId)
        => (genreId == null || set.GenreIdOrNull == genreId)
           && (languageId == null || set.LanguageIdOrNull == languageId);
}
