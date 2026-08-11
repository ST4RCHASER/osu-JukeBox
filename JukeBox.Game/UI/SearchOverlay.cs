#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Online;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// "Type to search" beatmap set browser. An <see cref="AccentTextBox"/> drives a 300ms-debounced
/// search against <see cref="IBeatmapMirror"/>, rendered as a scrollable list of
/// <see cref="SearchResultRow"/>s. Up/Down move the highlighted row; Enter (or clicking a row)
/// fires <see cref="SetPicked"/> with the selected set (falling back to the first result) and
/// closes the overlay; Escape just closes it. See <see cref="Docked"/> for the one exception to
/// both of those "closes it" behaviours, and for the two visual modes this drawable renders in:
/// docked (an always-visible column in <see cref="Screens.MainScreen"/>'s Split layout, no dim
/// scrim, its own panel-surface background) vs the default floating "type anywhere" dropdown
/// (a dim scrim over the whole screen behind a rounded, elevated search card).
/// </summary>
public partial class SearchOverlay : FocusedOverlayContainer
{
    private const double debounce_ms = 300;
    private const int page_size = 30;

    public event Action<BeatmapSetInfo>? SetPicked;

    /// <summary>
    /// When true, this overlay is docked inline (e.g. MainScreen's Split layout) rather than
    /// acting as a dismissable modal: picking a result still fires <see cref="SetPicked"/> but
    /// doesn't hide the overlay, and Escape doesn't hide it either. Also switches this drawable's
    /// own chrome from a fullscreen dim scrim to a plain panel-surface background, since the
    /// dimming/elevation is provided by the panel it's docked into instead.
    /// </summary>
    public bool Docked
    {
        get => docked;
        set
        {
            docked = value;
            updateChromeForDockedState();
        }
    }

    private bool docked;

    [Resolved]
    private IBeatmapMirror mirror { get; set; } = null!;

    private Box scrim = null!;
    private Container card = null!;
    private AccentTextBox searchBox = null!;
    private FillFlowContainer<SearchResultRow> resultsFlow = null!;
    private SpriteText statusText = null!;

    private ScheduledDelegate? debounceDelegate;

    // Guards against a slower, older search response overwriting the results of a newer one that
    // happened to complete first.
    private int searchSequence;

    private int selectedIndex = -1;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            scrim = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ModalScrim,
            },
            card = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding(Theme.PanelPadding),
                Children = new Drawable[]
                {
                    searchBox = new AccentTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 40,
                        PlaceholderText = "Search songs…",
                    },
                    statusText = new SpriteText
                    {
                        Y = 48,
                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                        Colour = Theme.TextTertiary,
                    },
                    new BasicScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Top = 40 + Theme.SectionSpacing },
                        Child = resultsFlow = new FillFlowContainer<SearchResultRow>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, Theme.RowSpacing),
                        }
                    }
                }
            }
        };

        updateChromeForDockedState();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        searchBox.Current.BindValueChanged(e => scheduleSearch(e.NewValue));
        searchBox.OnCommit += (_, _) => confirmSelection();
    }

    /// <summary>
    /// Opens the overlay, seeds the search box with <paramref name="c"/> (kicking off the first
    /// debounced search) and gives it keyboard focus — the "type anywhere to search" entry point.
    /// </summary>
    public void ShowWithInitialChar(char c)
    {
        Show();
        searchBox.Text = c.ToString();

        // FocusedOverlayContainer.UpdateState schedules its own focus-contention pass (which
        // unconditionally clears whatever is currently focused, including a synchronous focus
        // grab made right here) whenever State flips to Visible — see UpdateState/PopIn. Scheduling
        // this call too means it runs after that pass instead of getting immediately wiped by it,
        // so the text box (not the overlay itself) ends up with focus.
        Schedule(() => GetContainingFocusManager()?.ChangeFocus(searchBox));
    }

    // Deliberately instant (no fade animation): PopIn() must leave every descendant (searchBox
    // in particular) IsPresent synchronously, since focus can only land on a present drawable.
    protected override void PopIn() => Alpha = 1;

    protected override void PopOut()
    {
        Alpha = 0;

        // Cancel any pending debounced search so it doesn't fire (and mutate results) after the
        // overlay has closed.
        debounceDelegate?.Cancel();
    }

    private void updateChromeForDockedState()
    {
        // Guarded: the Docked property's setter can in principle run before load() has built
        // scrim/card (external code sets it any time), though in practice MainScreen only ever
        // does so from applyLayout(), well after this overlay's own BDL has completed.
        if (scrim == null)
            return;

        // Docked (Split's left column): the wrapping panel already supplies the rounded
        // panel-surface background/shadow, so this drawable stays transparent rather than
        // doubling up on it. Floating (fullscreen "type anywhere" dropdown): a dim scrim covers
        // the whole screen behind a more generously padded card.
        scrim.Alpha = docked ? 0 : 1;
        card.Padding = docked ? new MarginPadding(Theme.PanelPadding) : new MarginPadding(24);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Repeat)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                if (!Docked)
                    Hide();
                return true;

            case Key.Up:
                moveSelection(-1);
                return true;

            case Key.Down:
                moveSelection(1);
                return true;

            case Key.Enter:
            case Key.KeypadEnter:
                // Also handled via searchBox.OnCommit when the text box itself has focus; kept
                // here too as a fallback for whenever the overlay itself is focused instead.
                confirmSelection();
                return true;
        }

        return base.OnKeyDown(e);
    }

    private void moveSelection(int delta)
    {
        if (resultsFlow.Count == 0)
            return;

        int newIndex = selectedIndex < 0 ? 0 : Math.Clamp(selectedIndex + delta, 0, resultsFlow.Count - 1);
        setSelectedIndex(newIndex);
    }

    private void setSelectedIndex(int index)
    {
        selectedIndex = index;

        for (int i = 0; i < resultsFlow.Count; i++)
            resultsFlow.Children[i].Selected.Value = i == index;
    }

    /// <summary>
    /// Enter was pressed (via the text box's commit) — picks the highlighted row, falling back
    /// to the first result if the user hasn't moved the selection yet.
    /// </summary>
    private void confirmSelection()
    {
        var target = selectedIndex >= 0 && selectedIndex < resultsFlow.Count
            ? resultsFlow.Children[selectedIndex]
            : resultsFlow.Children.FirstOrDefault();

        if (target == null)
            return;

        pick(target);
    }

    private void pick(SearchResultRow row)
    {
        if (row.Set.DownloadDisabled)
            return;

        SetPicked?.Invoke(row.Set);

        if (!Docked)
            Hide();
    }

    private void scheduleSearch(string query)
    {
        debounceDelegate?.Cancel();
        debounceDelegate = Scheduler.AddDelayed(() => { _ = runSearchAsync(query); }, debounce_ms);
    }

    private async Task runSearchAsync(string query)
    {
        int mySequence = ++searchSequence;

        var request = new SearchRequest
        {
            Query = query,
            Status = "ranked",
            Sort = "ranked_desc",
            PageSize = page_size,
        };

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
            // A newer search superseded this one while it was in flight — drop this response.
            if (mySequence != searchSequence)
                return;

            displayResults(results);
        });
    }

    private void displayResults(List<BeatmapSetInfo> sets)
    {
        resultsFlow.Clear();
        selectedIndex = -1;

        if (sets.Count == 0)
        {
            statusText.Text = "no results";
            return;
        }

        statusText.Text = string.Empty;

        foreach (var set in sets)
        {
            var row = new SearchResultRow(set);
            // ClickableContainer.Action's setter also drives its Enabled bindable
            // (Enabled.Value = action != null) — leaving Action non-null for a disabled set would
            // dim it visually but still let it absorb clicks and show hover/press feedback.
            row.Action = set.DownloadDisabled ? null : () => pick(row);
            resultsFlow.Add(row);
        }
    }
}
