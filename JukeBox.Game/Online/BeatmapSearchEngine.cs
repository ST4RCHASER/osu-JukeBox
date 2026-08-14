#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace JukeBox.Game.Online;

/// <summary>
/// The beatmap-search state machine shared by both listing presentations (the docked/compact
/// <see cref="UI.BeatmapListingOverlay"/> and the fullscreen <see cref="UI.FullscreenListingOverlay"/> —
/// see <see cref="Configuration.SearchStyle"/>): a 300ms-debounced keyword search against
/// <see cref="IBeatmapMirror"/>, refined by the filter bindables below and paginated with
/// infinite-scroll semantics. Extracted from the (previously monolithic) listing overlay so the two
/// presentations are pure views over one engine — their filter chips bind the same bindables (so
/// selections stay in sync across views), their card grids rebuild off the same
/// <see cref="ResultsChanged"/> event, and no search logic is duplicated.
///
/// Mode/category/extras/sort/stars are server-side parameters — changing them restarts the search
/// at page 0. <see cref="GenreId"/>/<see cref="LanguageId"/> are CLIENT-SIDE filters over the
/// already-loaded results (the legacy mirror API can't express them): flipping them never issues a
/// request directly, but if the filtered results leave a view's viewport underfilled (possibly
/// empty) while more pages exist, further pages are auto-chained via <see cref="UpdatePaging"/>,
/// bounded by <see cref="max_auto_chain_pages"/> per user action.
///
/// A slower, older search response can never overwrite a newer one (<see cref="searchSequence"/>
/// guard). A <see cref="Component"/> so the debounce runs on this drawable's own scheduler —
/// hosted as an internal child of the docked listing, preserving the old behaviour that the
/// debounced search only ticks while that listing is present.
/// </summary>
public partial class BeatmapSearchEngine : Component
{
    private const double debounce_ms = 300;
    internal const int PAGE_SIZE = 30;

    /// <summary>
    /// Cap on consecutive automatically-chained page fetches (pages requested without the user
    /// scrolling — e.g. when the client-side genre/language filter reduces every loaded page to
    /// fewer cards than fill the viewport, possibly zero). Bounded per user action so an
    /// all-filtered result stream can't hammer the mirror indefinitely; the budget refreshes
    /// whenever content becomes scrollable again or the user changes the search/filters.
    /// </summary>
    private const int max_auto_chain_pages = 5;

    // ---- Query + server-side filters (any change restarts the search at page 0) --------------

    public readonly Bindable<string> Query = new Bindable<string>(string.Empty);

    /// <summary>NeriNyan `m`: "o"/"t"/"c"/"m", null = any.</summary>
    public readonly Bindable<string?> Mode = new Bindable<string?>();

    /// <summary>NeriNyan `s`.</summary>
    public readonly Bindable<string> Category = new Bindable<string>("ranked");

    public readonly BindableBool HasVideo = new BindableBool();
    public readonly BindableBool HasStoryboard = new BindableBool();

    /// <summary>`sort` = {key}_{asc|desc}, split across these two bindables.</summary>
    public readonly Bindable<string> SortKey = new Bindable<string>("ranked");

    public readonly BindableBool SortDescending = new BindableBool(true);

    public readonly BindableDouble MinStars = new BindableDouble { MinValue = 0, MaxValue = 10, Precision = 0.1 };
    public readonly BindableDouble MaxStars = new BindableDouble(10) { MinValue = 0, MaxValue = 10, Precision = 0.1 };

    // ---- Client-side filters (re-filter loaded results, never issue a request) ----------------

    public readonly Bindable<int?> GenreId = new Bindable<int?>();
    public readonly Bindable<int?> LanguageId = new Bindable<int?>();

    // ---- Results ------------------------------------------------------------------------------

    private readonly List<BeatmapSetInfo> loadedSets = new List<BeatmapSetInfo>();

    /// <summary>Every set loaded so far, unfiltered.</summary>
    public IReadOnlyList<BeatmapSetInfo> LoadedSets => loadedSets;

    /// <summary>The loaded sets passing the client-side genre/language filters — what views render.</summary>
    public IEnumerable<BeatmapSetInfo> VisibleSets => loadedSets.Where(s => MatchesClientFilters(s, GenreId.Value, LanguageId.Value));

    public bool IsLoading { get; private set; }
    public bool HasMore { get; private set; }

    /// <summary>Human-readable search lifecycle line ("searching…", "no results", …), shown by
    /// every view's status text.</summary>
    public readonly Bindable<string> Status = new Bindable<string>(string.Empty);

    /// <summary>
    /// The rendered result set changed — fired on the update thread. The argument is true for a
    /// FRESH search (views should scroll back to the top), false for a page append or a
    /// client-side filter change (views rebuild in place).
    /// </summary>
    public event Action<bool>? ResultsChanged;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    private ScheduledDelegate? debounceDelegate;

    // Guards against a slower, older search response overwriting the results of a newer one that
    // happened to complete first. Bumped by every fetch (fresh search or next page), so a stale
    // page-append can't interleave with a newer fresh search either.
    private int searchSequence;

    private int currentPage;

    /// <summary>Auto-chained fetches consumed since the last user action — see
    /// <see cref="max_auto_chain_pages"/>.</summary>
    private int autoChainedPages;

    // Subscriptions live in load() (synchronous with being added to the tree), NOT LoadComplete:
    // a floating listing starts hidden, and a hidden (non-present) parent defers its children's
    // LoadComplete until the first visible frame — by which time ShowWithInitialChar has already
    // seeded Query, so a LoadComplete-registered callback would miss that first change and the
    // initial search would never fire.
    [BackgroundDependencyLoader]
    private void load()
    {
        Query.BindValueChanged(_ => ScheduleSearch());
        Mode.BindValueChanged(_ => ScheduleSearch());
        Category.BindValueChanged(_ => ScheduleSearch());
        HasVideo.BindValueChanged(_ => ScheduleSearch());
        HasStoryboard.BindValueChanged(_ => ScheduleSearch());
        SortKey.BindValueChanged(_ => ScheduleSearch());
        SortDescending.BindValueChanged(_ => ScheduleSearch());
        MinStars.BindValueChanged(_ => ScheduleSearch());
        MaxStars.BindValueChanged(_ => ScheduleSearch());

        // Genre/language changed — a user action: reset the auto-chain budget and let the views
        // re-filter the loaded results. If the new filter leaves a viewport underfilled (or empty)
        // with more pages available, UpdatePaging auto-chains further fetches.
        GenreId.BindValueChanged(_ => applyClientFilterChange());
        LanguageId.BindValueChanged(_ => applyClientFilterChange());
    }

    /// <summary>Kicks off a debounced fresh search (page 0), superseding any pending one.</summary>
    public void ScheduleSearch()
    {
        debounceDelegate?.Cancel();
        debounceDelegate = Scheduler.AddDelayed(() => { _ = runSearchAsync(fresh: true); }, debounce_ms);
    }

    /// <summary>Cancels a pending debounced search — called when a floating view closes, so the
    /// search doesn't fire (and mutate results) after the overlay is gone.</summary>
    public void CancelPendingSearch() => debounceDelegate?.Cancel();

    /// <summary>
    /// Per-frame paging driver, called from a visible view's Update with its own scroll geometry.
    /// When content overflows the viewport, further paging is user-driven (infinite scroll:
    /// <paramref name="nearEnd"/>) and the auto-chain budget refreshes; when it underfills (the
    /// client-side filter possibly reduced every loaded page to nothing) the next page is chained
    /// automatically, bounded by <see cref="max_auto_chain_pages"/> per user action.
    /// </summary>
    public void UpdatePaging(bool contentOverflows, bool nearEnd)
    {
        // Driven off the RAW loaded count, not the filtered count: the client-side filter can
        // legitimately reduce a full page to zero visible cards, and later pages may still match.
        if (IsLoading || !HasMore || loadedSets.Count == 0)
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
        else if (!VisibleSets.Any())
        {
            Status.Value = $"no matches in the first {loadedSets.Count} loaded results — refine the filters or keywords";
        }
    }

    internal SearchRequest BuildRequest(int page)
    {
        double? min = MinStars.Value > 0 ? MinStars.Value : null;
        double? max = MaxStars.Value < 10 ? MaxStars.Value : null;

        // The two sliders are independent; if they cross, treat the crossed pair as an
        // empty-but-valid band rather than sending an inverted range.
        if (min != null && max != null && min > max)
            (min, max) = (max, min);

        return new SearchRequest
        {
            Query = Query.Value,
            Page = page,
            PageSize = PAGE_SIZE,
            Status = Category.Value,
            Sort = $"{SortKey.Value}_{(SortDescending.Value ? "desc" : "asc")}",
            Mode = Mode.Value,
            Extra = HasVideo.Value && HasStoryboard.Value ? SearchExtra.VideoAndStoryboard
                : HasVideo.Value ? SearchExtra.Video
                : HasStoryboard.Value ? SearchExtra.Storyboard
                : SearchExtra.None,
            MinStars = min,
            MaxStars = max,
        };
    }

    private async Task runSearchAsync(bool fresh)
    {
        int mySequence = ++searchSequence;
        int page = fresh ? 0 : currentPage + 1;

        IsLoading = true;
        Schedule(() => Status.Value = fresh ? "searching…" : "loading more…");

        var request = BuildRequest(page);

        List<BeatmapSetInfo> results;

        try
        {
            results = await mirror.SearchAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Beatmap search failed");
            results = new List<BeatmapSetInfo>();
        }

        Schedule(() =>
        {
            // A newer search superseded this one while it was in flight — drop this response
            // (the superseding search resets IsLoading when it completes).
            if (mySequence != searchSequence)
                return;

            IsLoading = false;
            currentPage = page;
            HasMore = results.Count >= PAGE_SIZE;

            if (fresh)
            {
                loadedSets.Clear();

                // A fresh search is a user action — the auto-chain budget starts over.
                autoChainedPages = 0;
            }

            loadedSets.AddRange(results);
            updateStatus();
            ResultsChanged?.Invoke(fresh);
        });
    }

    private void applyClientFilterChange()
    {
        autoChainedPages = 0;
        updateStatus();
        ResultsChanged?.Invoke(false);
    }

    private void updateStatus()
    {
        if (!VisibleSets.Any())
            Status.Value = loadedSets.Count == 0 ? "no results" : "no results (client-side genre/language filter)";
        else
            Status.Value = string.Empty;
    }

    /// <summary>
    /// Whether <paramref name="set"/> passes the client-side genre/language filters — the legacy
    /// mirror search can't express either, so they're applied to loaded results instead. A set
    /// with no genre/language assigned (NeriNyan serves nulls) only passes the "Any" filter.
    /// </summary>
    internal static bool MatchesClientFilters(BeatmapSetInfo set, int? genreId, int? languageId)
        => (genreId == null || set.Genre?.Id == genreId)
           && (languageId == null || set.Language?.Id == languageId);
}
