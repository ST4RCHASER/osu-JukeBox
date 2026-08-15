#nullable enable

using System;
using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;
using osu.Framework.Utils;
using osu.Game.Graphics.UserInterface;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// Lazer-style dropdown listing every difficulty of the currently playing set. Each item reads
/// "[ruleset icon] [★ rating] Version" — the same vocabulary the fullscreen listing's expanded
/// difficulty rows use (see <see cref="RulesetIcons"/> and <see cref="StarRatingPill"/>), so a
/// difficulty looks the same wherever it is named. Picking an item switches chart/hitsounds/
/// storyboard to that difficulty while playback continues at the current time (via
/// <see cref="PlaybackController.SwitchDifficultyAsync"/>). A single-difficulty set still shows
/// its one entry (locked via <see cref="Bindable{T}.Disabled"/> — non-interactive, since there's
/// nothing to switch between); a zero-difficulty set hides the dropdown entirely.
/// </summary>
public partial class DifficultySwitcher : CompositeDrawable
{
    /// <summary>Keep the dropdown tidy on sets with absurd difficulty counts.</summary>
    private const int max_items = 10;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    // Star ratings are NOT in a .osu file — they're a computed quantity, and the only place this app
    // has them is the online metadata for the set being played. Resolved optionally (canBeNull) for
    // the same reason NowPlayingPanel's thumbnail store is: bare test scenes construct this switcher
    // with no jukebox at all, and a missing rating simply drops the star pill (see starsFor).
    [Resolved(canBeNull: true)]
    private Jukebox? jukebox { get; set; }

    private DifficultyDropdown dropdown = null!;

    /// <summary>
    /// Guards <see cref="onSelectionCommitted"/> against firing <see cref="PlaybackController.SwitchDifficultyAsync"/>
    /// for our own programmatic syncing of the dropdown's <c>Current</c> (new set loaded, or
    /// another component moved <see cref="PlaybackController.SelectedOsuFile"/>) rather than an
    /// actual user pick from the dropdown menu.
    /// </summary>
    private bool settingSelection;

    /// <summary>Test-only access to the dropdown (JukeBox.Game.Tests has InternalsVisibleTo).</summary>
    internal DifficultyDropdown Dropdown => dropdown;

    [BackgroundDependencyLoader]
    private void load()
    {
        // Full width of whatever hosts this (the Playback tab's column), rather than the fixed
        // 220px it carried while it sat in the old wide bottom bar: a dropdown stopping short of
        // the column's right edge read as misaligned against the full-width rows around it.
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;

        InternalChild = dropdown = new DifficultyDropdown(starsFor)
        {
            RelativeSizeAxes = Axes.X,
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        playback.Current.BindValueChanged(onSetChanged, true);
        playback.SelectedOsuFile.BindValueChanged(_ => syncSelection());
        dropdown.Current.BindValueChanged(onSelectionCommitted);

        // The closed dropdown draws the selected difficulty with the same icon/pill as the menu
        // rows, so it has to be refreshed both when the pick changes and when the ratings behind it
        // arrive — NowPlaying is what carries them, and it can land after the set has already been
        // handed to playback.
        dropdown.Current.BindValueChanged(e => dropdown.RefreshHeader(e.NewValue), true);
        jukebox?.NowPlaying.BindValueChanged(_ => dropdown.RefreshHeader(dropdown.Current.Value));
    }

    /// <summary>
    /// The online star rating for a locally scanned difficulty, matched on ruleset + difficulty
    /// name against the set currently playing — the pair the osu! API itself treats as a
    /// difficulty's identity within a set. Null (no pill) whenever the set is playing without
    /// online metadata: a local folder opened directly, or a mirror response that carried no
    /// beatmap list.
    /// </summary>
    private double? starsFor(DifficultyInfo difficulty)
    {
        string mode = RulesetIcons.ModeString(difficulty.Mode);

        return jukebox?.NowPlaying.Value?.Beatmaps
                      .FirstOrDefault(b => b.Version == difficulty.Version && b.Mode == mode)
                      ?.DifficultyRating;
    }

    private void onSetChanged(ValueChangedEvent<CachedBeatmapSet?> change)
    {
        var set = change.NewValue;

        // Deferred a frame: playback.Current fires its initial (true) BindValueChanged
        // synchronously from LoadComplete, before dropdown's own Menu has ever run an Update() —
        // and Dropdown<T>.Items's underlying Menu.Insert sorts existing items by a LayoutPosition
        // that flow machinery only assigns once a frame has actually ticked, throwing
        // InvalidOperationException if touched before that. A same-frame difficulty change (rare —
        // requires two PlaybackController.Current writes inside one Update) would queue two
        // Schedules in order, still landing on the correct final state.
        Schedule(() =>
        {
            // Unlock before touching Items/Current below — Bindable<T>.Value throws if written
            // while Disabled (which it will be, left over from a previous single-difficulty set).
            dropdown.Current.Disabled = false;

            // Populating Items is emphatically not a user pick, but Dropdown<T> moves its own
            // Current when the item list is replaced (the old selection is no longer in it), which
            // reaches onSelectionCommitted and fires a real SwitchDifficultyAsync for whatever the
            // control happened to land on — the set's default difficulty. That silently undid any
            // difficulty another component had just selected: this Schedule runs a frame after the
            // Current write that queued it, so a difficulty chosen in between (Jukebox selecting
            // the one a dropped replay was recorded on) was reverted to the set default the moment
            // this ran. Hence the same guard syncSelection uses, extended over the assignment.
            settingSelection = true;

            if (set == null || set.Difficulties.Count == 0)
            {
                dropdown.Items = System.Array.Empty<DifficultyInfo?>();
                settingSelection = false;
                dropdown.Hide(); // AutoSizeAxes on this CompositeDrawable then collapses to zero size, same as the old empty chip flow
                return;
            }

            dropdown.Show();
            dropdown.Items = set.Difficulties.Take(max_items).Cast<DifficultyInfo?>().ToArray();
            settingSelection = false;

            syncSelection();

            // A single-difficulty set still shows its one entry (effectively always-selected) —
            // just nothing to switch between, so it's locked non-interactive.
            dropdown.Current.Disabled = set.Difficulties.Count <= 1;
        });
    }

    private void syncSelection()
    {
        var set = playback.Current.Value;
        if (set == null || set.Difficulties.Count == 0)
            return;

        string? selectedPath = playback.SelectedOsuFile.Value ?? set.PreferredOsuFile;
        var match = set.Difficulties.FirstOrDefault(d => d.Path == selectedPath) ?? set.Difficulties[0];

        // Only sync if the target difficulty actually made it into the (possibly capped) dropdown
        // Items list — matches the old chip flow's max_items cap.
        if (!dropdown.Items.Contains(match))
            return;

        bool wasDisabled = dropdown.Current.Disabled;
        dropdown.Current.Disabled = false;

        settingSelection = true;
        dropdown.Current.Value = match;
        settingSelection = false;

        dropdown.Current.Disabled = wasDisabled;
    }

    private void onSelectionCommitted(ValueChangedEvent<DifficultyInfo?> change)
    {
        // Ignore changes caused by our own syncSelection()/onSetChanged() above — only an actual
        // user pick from the dropdown menu should trigger a difficulty switch.
        if (settingSelection || change.NewValue == null)
            return;

        _ = playback.SwitchDifficultyAsync(change.NewValue.Path);
    }

    /// <summary>
    /// Lazer's dropdown, with every row (and the closed header) prefixed by the difficulty's ruleset
    /// icon and star-rating pill instead of the old "[osu!]" text tag. Both presentations delegate
    /// to <see cref="DifficultyBadges"/> so they can never drift apart.
    /// </summary>
    internal partial class DifficultyDropdown : OsuDropdown<DifficultyInfo?>
    {
        private readonly Func<DifficultyInfo, double?> starsFor;
        private DifficultyDropdownHeader header = null!;

        public DifficultyDropdown(Func<DifficultyInfo, double?> starsFor)
        {
            this.starsFor = starsFor;
        }

        /// <summary>Redraws the closed dropdown's badges for <paramref name="selected"/>. Driven
        /// from <see cref="DifficultySwitcher.LoadComplete"/> rather than from the framework's
        /// <c>Label</c> setter, which only carries the item's text.</summary>
        public void RefreshHeader(DifficultyInfo? selected) => header.SetDifficulty(selected, badgesFor(selected));

        /// <summary>The badge strip for one item, or null when there is nothing to draw (the empty
        /// selection a set with no difficulties leaves behind).</summary>
        private Drawable? badgesFor(DifficultyInfo? item)
            => item == null ? null : new DifficultyBadges(item, starsFor(item));

        protected override LocalisableString GenerateItemText(DifficultyInfo? item)
            => item?.Version ?? string.Empty;

        /// <summary>Test-only access to the protected label generator (JukeBox.Game.Tests has
        /// InternalsVisibleTo).</summary>
        internal LocalisableString GenerateItemTextForTest(DifficultyInfo? item) => GenerateItemText(item);

        /// <summary>Test-only: the badge strip this dropdown would draw for an item, so a test can
        /// assert its contents without opening the menu.</summary>
        internal Drawable? BadgesForTest(DifficultyInfo? item) => badgesFor(item);

        protected override DropdownHeader CreateHeader() => header = new DifficultyDropdownHeader();

        protected override DropdownMenu CreateMenu() => new DifficultyMenu(badgesFor);

        private partial class DifficultyMenu : OsuDropdownMenu
        {
            private readonly Func<DifficultyInfo?, Drawable?> badgesFor;

            public DifficultyMenu(Func<DifficultyInfo?, Drawable?> badgesFor)
            {
                this.badgesFor = badgesFor;
            }

            protected override DrawableDropdownMenuItem CreateDrawableDropdownMenuItem(MenuItem item)
                => new DifficultyMenuItem(item, badgesFor((item as DropdownMenuItem<DifficultyInfo?>)?.Value));

            private partial class DifficultyMenuItem : DrawableOsuDropdownMenuItem
            {
                private readonly Drawable? badges;

                public DifficultyMenuItem(MenuItem item, Drawable? badges)
                    : base(item)
                {
                    this.badges = badges;
                }

                protected override Drawable CreateContent() => new BadgedContent(badges);

                /// <summary>Lazer's own row content with the badges slotted in ahead of its label.
                /// The label's left padding tracks the badge strip's measured width rather than a
                /// guessed constant, since the pill's width follows the rendered rating text.</summary>
                private partial class BadgedContent : DrawableOsuDropdownMenuItem.Content
                {
                    /// <summary>Lazer's own text inset, kept as the badges' left margin so a badged
                    /// row starts exactly where an unbadged one would.</summary>
                    private const float text_inset = 15;

                    private const float badge_gap = 6;

                    private readonly Container? badgeContainer;

                    public BadgedContent(Drawable? badges)
                    {
                        if (badges == null)
                            return;

                        AddInternal(badgeContainer = new Container
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            AutoSizeAxes = Axes.Both,
                            X = text_inset,
                            Child = badges,
                        });
                    }

                    protected override void Update()
                    {
                        base.Update();

                        if (badgeContainer == null)
                            return;

                        // Read a frame late (the container's own auto-size only resolves after this
                        // Update runs), which is invisible: the row settles on its very next frame,
                        // before any of it is interactive.
                        float inset = text_inset + badgeContainer.DrawWidth + badge_gap;

                        if (!Precision.AlmostEquals(Label.Padding.Left, inset))
                            Label.Padding = new MarginPadding { Left = inset };
                    }
                }
            }
        }

        /// <summary>The closed dropdown: lazer's header with the same badge strip ahead of its
        /// text, so collapsing the menu doesn't change how the current difficulty reads.</summary>
        private partial class DifficultyDropdownHeader : OsuDropdownHeader
        {
            private const float badge_gap = 6;

            private readonly Container badgeContainer;
            private readonly float baseTextPadding;

            public DifficultyDropdownHeader()
            {
                baseTextPadding = Text.Padding.Left;

                Foreground.Add(badgeContainer = new Container
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Both,
                    X = baseTextPadding,
                });
            }

            public void SetDifficulty(DifficultyInfo? difficulty, Drawable? badges)
            {
                badgeContainer.Clear();

                if (difficulty != null && badges != null)
                    badgeContainer.Add(badges);
            }

            protected override void Update()
            {
                base.Update();

                // Same one-frame-late measurement as the menu rows above.
                float inset = badgeContainer.Children.Count == 0
                    ? baseTextPadding
                    : baseTextPadding + badgeContainer.DrawWidth + badge_gap;

                if (!Precision.AlmostEquals(Text.Padding.Left, inset))
                    Text.Padding = new MarginPadding { Left = inset };
            }
        }
    }

    /// <summary>The "[ruleset icon] [★ 4.21]" strip shared by the dropdown's rows and its closed
    /// header. The pill is omitted when the rating is unknown (see
    /// <see cref="DifficultySwitcher.starsFor"/>), leaving the icon alone.</summary>
    internal partial class DifficultyBadges : FillFlowContainer
    {
        public DifficultyBadges(DifficultyInfo difficulty, double? stars)
        {
            Anchor = Anchor.CentreLeft;
            Origin = Anchor.CentreLeft;
            AutoSizeAxes = Axes.Both;
            Direction = FillDirection.Horizontal;
            Spacing = new Vector2(5, 0);

            Add(RulesetIcons.Create(difficulty.Mode).With(icon =>
            {
                icon.Anchor = Anchor.CentreLeft;
                icon.Origin = Anchor.CentreLeft;
                icon.Size = new Vector2(12);
            }));

            if (stars != null)
                Add(StarRatingPill.Create(stars.Value, fontSize: 10));
        }
    }
}
