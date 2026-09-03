#nullable enable

using System;
using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// What a right-column tab needs to expose so typing can land in its search box instead of opening
/// the beatmap listing. See <c>MainScreen.OnKeyDown</c>, which routes by the active tab.
/// </summary>
internal interface ITabSearch
{
    /// <summary>Focuses this tab's search box and seeds it with the character just typed.</summary>
    void BeginSearch(char first);

    /// <summary>Empties the filter and drops focus. Called when the tab is switched away from.</summary>
    void ClearSearch();
}

/// <summary>
/// A settings-style tab body: a search box pinned above a scrolling list of sections that filters
/// live as you type.
///
/// <para>
/// Almost nothing here is ours. osu!framework's <see cref="SearchContainer"/> walks the whole
/// drawable subtree and hides anything implementing <c>IFilterable</c> whose <c>FilterTerms</c>
/// miss — and lazer's settings components already implement it: <c>SettingsItem</c> reports its
/// <c>LabelText</c>, <c>SettingsSection</c> and <c>SettingsSubsection</c> report their headers, and
/// a section stays visible exactly as long as one descendant matched. So every row and every header
/// in both tabs became searchable by being wrapped in this, with no per-row work at all.
/// </para>
///
/// <para>
/// Shared by <see cref="SettingsOverlay"/> and <see cref="ChartPanel"/> rather than written twice:
/// the two tabs must filter and take focus identically, and the typing-routes-here behaviour has
/// one implementation to be correct in.
/// </para>
/// </summary>
internal partial class TabSearchBody : CompositeDrawable, ITabSearch
{
    private readonly TabSearchTextBox searchBox;
    private readonly SearchContainer searchContainer;

    /// <summary>The scrolling region, for callers that scroll a particular row into view.</summary>
    public OsuScrollContainer Scroll { get; }

    /// <summary>Test seam: the live filter term.</summary>
    internal string SearchTerm => searchContainer.SearchTerm ?? string.Empty;

    /// <summary>Test seam: whether the box currently holds keyboard focus.</summary>
    internal bool SearchHasFocus => searchBox.HasFocus;

    public TabSearchBody(IEnumerable<Drawable> sections, string placeholder)
    {
        RelativeSizeAxes = Axes.Both;

        InternalChild = new GridContainer
        {
            RelativeSizeAxes = Axes.Both,

            // The box is pinned, not scrolled: a filter you cannot see is a filter you cannot
            // undo, and scrolling down through results must never take the box off screen.
            RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize), new Dimension() },
            Content = new[]
            {
                new Drawable[]
                {
                    searchBox = new TabSearchTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        PlaceholderText = placeholder,
                        Margin = new MarginPadding
                        {
                            Horizontal = SettingsPanel.CONTENT_MARGINS,
                            Top = 14,
                            Bottom = 8,
                        },
                    },
                },
                new Drawable[]
                {
                    Scroll = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        ScrollbarVisible = true,
                        Child = searchContainer = new SearchContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Padding = new MarginPadding { Bottom = 20 },
                            Direction = FillDirection.Vertical,
                            Children = new List<Drawable>(sections),
                        },
                    },
                },
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        searchBox.Current.BindValueChanged(term => searchContainer.SearchTerm = term.NewValue, true);
        searchBox.Escaped = ClearSearch;
    }

    public void BeginSearch(char first)
    {
        // Appended rather than assigned: a second keystroke arriving before focus has settled must
        // extend the term rather than replace what is already there.
        if (!searchBox.HasFocus)
            searchBox.TakeFocus();

        searchBox.Current.Value += first;
    }

    public void ClearSearch()
    {
        searchBox.Current.Value = string.Empty;

        if (searchBox.HasFocus)
            searchBox.KillFocus();
    }

    /// <summary>
    /// lazer's own settings search box, with Escape wired to clear rather than only unfocus — a
    /// box that loses focus while still filtering leaves rows hidden with no visible cause.
    /// </summary>
    private partial class TabSearchTextBox : SettingsSearchTextBox
    {
        /// <summary>
        /// How far the placeholder has to rise to sit where typed text sits.
        ///
        /// <para>
        /// The two are laid out by different machinery and it shows. Typed characters are
        /// positioned per GLYPH, so their ink lands on the box's centre line. The placeholder is a
        /// single <see cref="SpriteText"/> whose LINE BOX is centred instead — and a line box
        /// reserves more room above the x-height (for ascenders) than below it (for descenders),
        /// so the words themselves come out low. Measured against a 35px box: the placeholder's
        /// x-height band centred 2px under the typed text's.
        /// </para>
        /// </summary>
        private const float placeholder_optical_offset = -2;

        public Action? Escaped;

        protected override SpriteText CreatePlaceholder()
        {
            var placeholder = base.CreatePlaceholder();
            placeholder.Y = placeholder_optical_offset;
            return placeholder;
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Key == Key.Escape && !e.Repeat)
            {
                // Only claim Escape while there is a filter to clear. An empty box must let it
                // through to whatever Escape normally does here (closing an overlay, say).
                if (Current.Value.Length > 0)
                {
                    Escaped?.Invoke();
                    return true;
                }
            }

            return base.OnKeyDown(e);
        }
    }
}
