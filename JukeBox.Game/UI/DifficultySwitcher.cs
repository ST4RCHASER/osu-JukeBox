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
///
/// <para>
/// The list is ordered EASIEST FIRST by star rating and a set starts on its HARDEST difficulty —
/// see <see cref="listedDifficulties"/> and <see cref="applyHardestDefault"/>. Both need ratings,
/// which exist only in the set's online metadata (a .osu file has no star rating; it is computed),
/// so both degrade to the previous behaviour on a set that has none.
/// </para>
/// </summary>
public partial class DifficultySwitcher : CompositeDrawable
{
    /// <summary>
    /// How tall the open list may get before it scrolls. EVERY difficulty is listed — a cap used to
    /// drop them at ten, which silently hid two thirds of a big marathon set and, worse, made the
    /// "start on the hardest" default a lie, since the real hardest difficulty was often one of the
    /// ones dropped. Height is bounded instead of the item count, so nothing is unreachable: this is
    /// roughly a dozen rows, which fits the column without the menu covering the whole panel.
    /// </summary>
    private const float menu_max_height = 400;

    /// <summary>How far the dropdown dims while locked — see <see cref="updateLockedState"/>.
    /// Enough to read as unavailable, not so much that the selected difficulty stops being
    /// legible, since that difficulty is still the useful information.</summary>
    private const float locked_alpha = 0.55f;

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

    /// <summary>The set <see cref="applyHardestDefault"/> has already been applied to, so it runs
    /// exactly once per set however many times its inputs land.</summary>
    private CachedBeatmapSet? autoSelectedFor;

    /// <summary>What the list was last built from — see <see cref="refreshItems"/>.</summary>
    private string? itemsSignature;

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

        // NowPlaying carries the star ratings the header draws, the ORDER the list is sorted into,
        // the difficulty played by default AND whether a replay is driving playback — and it lands
        // AFTER the playback swap that queued onSetChanged, so all four have to be re-evaluated here
        // rather than only on a set change.
        jukebox?.NowPlaying.BindValueChanged(_ =>
        {
            dropdown.RefreshHeader(dropdown.Current.Value);

            // Deferred for exactly the reason onSetChanged is (see its own comment): re-sorting the
            // list goes through Dropdown<T>.Items, whose Menu.Insert reads a LayoutPosition the flow
            // machinery only assigns once a frame has ticked. NowPlaying can land in the very same
            // frame the menu was built in.
            Schedule(() =>
            {
                refreshItems();
                updateLockedState();
                applyHardestDefault();
            });
        });
    }

    /// <summary>
    /// The online star rating for a locally scanned difficulty, matched on ruleset + difficulty
    /// name against the set currently playing — the pair the osu! API itself treats as a
    /// difficulty's identity within a set. Null (no pill, no sort key) whenever the set is playing
    /// without online metadata: a local folder or a dropped .osz, or a mirror response that carried
    /// no beatmap list.
    ///
    /// <para>
    /// The name is not always identical on both sides, which is why the exact comparison has a
    /// fallback — see <see cref="ManiaKeyPrefix"/>.
    /// </para>
    /// </summary>
    private double? starsFor(DifficultyInfo difficulty)
    {
        var ratings = ratingsForCurrentSet();

        if (ratings == null)
            return null;

        string mode = RulesetIcons.ModeString(difficulty.Mode);
        var sameMode = ratings.Where(b => b.Mode == mode).ToList();

        var exact = sameMode.FirstOrDefault(b => b.Version == difficulty.Version);

        if (exact != null)
            return exact.DifficultyRating;

        // Undecorated fallback. Ambiguity is possible in principle (a set carrying both
        // "[4K] Insane" and "[7K] Insane" collapses to one name), and a wrong rating is worse than
        // none — a difficulty would be sorted into the wrong place and labelled with a number that
        // isn't its own — so a tie yields nothing rather than a guess.
        var undecorated = sameMode.Where(b => StripManiaKeyPrefix(b.Version) == difficulty.Version).ToList();

        return undecorated.Count == 1 ? undecorated[0].DifficultyRating : null;
    }

    /// <summary>
    /// osu-web's mania key-count decoration: it prefixes a mania difficulty's name with the key
    /// count (<c>"[4K] Chicken's HEAVENLY"</c>) unless the mapper already put one in the name
    /// (<c>"14K DP Easy"</c> is served unchanged). The .osu file on disk never carries it — it is
    /// added when the beatmap is served — so a mania set whose difficulties are plainly named
    /// matched NOTHING here, losing its star pills, its rating order and its hardest-difficulty
    /// default. Both the official API and the mirrors serve the decorated name, since the mirrors
    /// proxy the same data, so this was never specific to one search backend.
    /// </summary>
    internal static readonly System.Text.RegularExpressions.Regex ManiaKeyPrefix =
        new System.Text.RegularExpressions.Regex(@"^\[\d{1,2}K\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Test seam (JukeBox.Game.Tests has InternalsVisibleTo) for <see cref="ManiaKeyPrefix"/>.</summary>
    internal static string StripManiaKeyPrefix(string version) => ManiaKeyPrefix.Replace(version, string.Empty);

    /// <summary>
    /// The online beatmap list for the set playing RIGHT NOW, or null if there isn't one yet.
    ///
    /// <para>
    /// The set-id check is what makes this safe to call at any moment: Jukebox publishes
    /// <see cref="Jukebox.NowPlaying"/> only AFTER <c>PlayAsync</c> has already moved
    /// <see cref="PlaybackController.Current"/>, so between those two points NowPlaying still
    /// describes the PREVIOUS set. Difficulty names are what these lookups match on, and names like
    /// "Hard"/"Insane"/"Normal" recur across sets — without the check, a difficulty could briefly be
    /// labelled (and sorted) by another song's rating.
    /// </para>
    /// </summary>
    private System.Collections.Generic.List<Online.BeatmapInfo>? ratingsForCurrentSet()
    {
        var online = jukebox?.NowPlaying.Value;
        var set = playback.Current.Value;

        if (online == null || set == null || online.Id != set.SetId)
            return null;

        return online.Beatmaps;
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
                itemsSignature = null;
                settingSelection = false;
                dropdown.Hide(); // AutoSizeAxes on this CompositeDrawable then collapses to zero size, same as the old empty chip flow
                return;
            }

            settingSelection = false;

            // A different set always rebuilds, whatever its difficulties happen to be called.
            itemsSignature = null;

            dropdown.Show();
            refreshItems();
            updateLockedState();
            applyHardestDefault();
        });
    }

    /// <summary>
    /// Rebuilds the list when what it should show has changed. The single place <c>Items</c> is
    /// populated, because BOTH things that can change are driven by ratings that arrive late (see
    /// <see cref="ratingsForCurrentSet"/>):
    ///
    /// <list type="bullet">
    /// <item>the ORDER, which is by rating (<see cref="listedDifficulties"/>);</item>
    /// <item>each row's BADGES, since a row's star pill is built with the row, out of whatever was
    /// known at that moment.</item>
    /// </list>
    ///
    /// <para>
    /// Hence a signature over paths AND ratings rather than a plain order comparison: a set whose
    /// rating order happens to match its alphabetical one (common — mappers name difficulties in
    /// ascending order) would otherwise keep the pill-less rows it was first built with, which is
    /// exactly how the real app ended up showing a rated header above unrated rows.
    /// </para>
    ///
    /// <para>
    /// The no-op case matters: rebuilding <c>Items</c> tears down and recreates every menu row,
    /// throwing away an open menu's hover/preselection state, and NowPlaying is written for reasons
    /// that have nothing to do with difficulty.
    /// </para>
    /// </summary>
    private void refreshItems()
    {
        var set = playback.Current.Value;

        if (set == null || set.Difficulties.Count == 0)
            return;

        var listed = listedDifficulties(set).ToArray();
        string signature = string.Join('|', listed.Select(d => $"{d.Path}@{starsFor(d)?.ToString("0.00") ?? "?"}"));

        if (signature == itemsSignature)
            return;

        itemsSignature = signature;

        // Same unlock-and-guard dance as onSetChanged above, and for the same reason: replacing
        // Items moves Dropdown<T>'s own Current, which must not read as a user pick.
        bool wasDisabled = dropdown.Current.Disabled;
        dropdown.Current.Disabled = false;

        settingSelection = true;
        dropdown.Items = listed.Cast<DifficultyInfo?>().ToArray();
        settingSelection = false;

        dropdown.Current.Disabled = wasDisabled;

        syncSelection();
    }

    /// <summary>
    /// EVERY difficulty in the set, EASIEST FIRST. Nothing is dropped — the list is bounded by
    /// <see cref="menu_max_height"/> and scrolls instead — so what
    /// <see cref="applyHardestDefault"/> picks from is the whole set, and the difficulty it starts
    /// on is genuinely the hardest rather than the hardest of an arbitrary first ten.
    ///
    /// <para>
    /// Difficulties with no known rating sort to the END and keep their relative order, because
    /// <c>OrderBy</c> is a stable sort. A set with NO online metadata at all (a local folder, a
    /// dropped .osz) therefore falls back to exactly the alphabetical order this dropdown used
    /// before — every key is equal, so nothing moves.
    /// </para>
    /// </summary>
    private System.Collections.Generic.IEnumerable<DifficultyInfo> listedDifficulties(CachedBeatmapSet set)
        => set.Difficulties.OrderBy(d => starsFor(d) ?? double.PositiveInfinity);

    /// <summary>
    /// Starts a set on its HARDEST difficulty rather than the set's own default (which is just the
    /// first osu!std file on disk). Applied once per set, and never when it would override a
    /// deliberate choice:
    ///
    /// <list type="bullet">
    /// <item>A <b>replay</b> pins the exact .osu it was recorded on (see
    /// <see cref="updateLockedState"/>) — switching away would drop it back to autoplay.</item>
    /// <item>A difficulty the <b>user picked</b> for this set stands. Any selection that isn't the
    /// set's own <see cref="CachedBeatmapSet.PreferredOsuFile"/> got there by someone choosing it,
    /// so it is left alone.</item>
    /// <item>A set with <b>no star ratings</b> has nothing to rank by, so it keeps today's
    /// behaviour.</item>
    /// </list>
    ///
    /// <para>
    /// Called both when the set changes and when <see cref="Jukebox.NowPlaying"/> lands, because the
    /// ratings this needs arrive on the LATTER — usually after the former (see
    /// <see cref="ratingsForCurrentSet"/>). <see cref="autoSelectedFor"/> is what keeps that from
    /// re-running and stomping a switch the user made in between.
    /// </para>
    /// </summary>
    private void applyHardestDefault()
    {
        var set = playback.Current.Value;

        if (set == null || set.Difficulties.Count <= 1 || ReferenceEquals(autoSelectedFor, set) || ReplayLocked)
            return;

        string? selected = playback.SelectedOsuFile.Value;

        if (selected != null && selected != set.PreferredOsuFile)
            return;

        var hardest = listedDifficulties(set).LastOrDefault(d => starsFor(d) != null);

        if (hardest == null)
            return;

        // Claimed even when no switch follows: the set HAS been considered, and re-considering it
        // later (once the user has moved off this difficulty) would drag them back here.
        autoSelectedFor = set;

        if (hardest.Path != selected)
            _ = playback.SwitchDifficultyAsync(hardest.Path);
    }

    /// <summary>
    /// Locks the dropdown whenever picking a different difficulty would be meaningless or wrong:
    ///
    /// <list type="bullet">
    /// <item>A <b>single-difficulty set</b> has its one entry effectively always-selected — there
    /// is simply nothing to switch between.</item>
    /// <item>A <b>replay</b> is tied to one exact .osu (matched by checksum — see
    /// <see cref="Replays.ReplayAttachment.OsuFile"/>); switching away would silently drop the
    /// replay back to autoplay on a difficulty the player never played. The replay's own
    /// difficulty stays visible in the closed dropdown, it just can't be changed.</item>
    /// </list>
    ///
    /// <para>
    /// Both states also dim the control. A disabled dropdown that looks identical to a live one
    /// reads as a bug rather than a rule — the single-difficulty case had that problem already, and
    /// giving the two locked states one appearance is both less code and less to explain.
    /// </para>
    /// </summary>
    private void updateLockedState()
    {
        var set = playback.Current.Value;

        if (set == null || set.Difficulties.Count == 0)
            return;

        bool locked = set.Difficulties.Count <= 1 || ReplayLocked;

        dropdown.Current.Disabled = locked;
        dropdown.FadeTo(locked ? locked_alpha : 1, Theme.HoverFadeDuration, Easing.OutQuint);
    }

    /// <summary>
    /// Whether a dropped replay is driving what's playing. Read off the now-playing set (the object
    /// the replay actually travels on) rather than the replay registry, so it answers for the SET
    /// being watched rather than for whichever difficulty happens to be selected mid-swap.
    /// </summary>
    internal bool ReplayLocked => jukebox?.NowPlaying.Value?.Replay != null;

    private void syncSelection()
    {
        var set = playback.Current.Value;
        if (set == null || set.Difficulties.Count == 0)
            return;

        string? selectedPath = playback.SelectedOsuFile.Value ?? set.PreferredOsuFile;
        // Falls back to the first LISTED difficulty (the easiest), not set.Difficulties[0] — the
        // list is sorted by rating, so the raw scan order's first entry is rarely the one on top.
        var match = set.Difficulties.FirstOrDefault(d => d.Path == selectedPath) ?? listedDifficulties(set).First();

        // The list holds every difficulty now, so this only guards the window between a set change
        // and the Schedule that repopulates Items — writing a Current the control doesn't yet know
        // about would throw.
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

        protected override DropdownHeader CreateHeader() => header = new DifficultyDropdownHeader();

        protected override DropdownMenu CreateMenu() => new DifficultyMenu(badgesFor) { MaxHeight = menu_max_height };

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
                // Captured on the way out of CreateContent() rather than read back off the
                // inherited Content field, which is unreachable by name here: lazer nests a TYPE
                // called Content inside this class, and it shadows the field.
                private BadgedContent content = null!;

                public DifficultyMenuItem(MenuItem item, Drawable? badges)
                    : base(item)
                {
                    // The badges are applied HERE rather than passed to CreateContent(), because the
                    // framework calls CreateContent() from DrawableMenuItem's own constructor —
                    // before this body runs, so a field assigned here would still be null when the
                    // content is built. That is exactly how these rows silently lost their badges
                    // while the closed header (populated after construction) kept its own.
                    content.SetBadges(badges);
                }

                protected override Drawable CreateContent() => content = new BadgedContent();

                /// <summary>Lazer's own row content with the badges slotted in ahead of its label.
                /// The label's left padding tracks the badge strip's measured width rather than a
                /// guessed constant, since the pill's width follows the rendered rating text.</summary>
                private partial class BadgedContent : DrawableOsuDropdownMenuItem.Content
                {
                    /// <summary>Lazer's own text inset, kept as the badges' left margin so a badged
                    /// row starts exactly where an unbadged one would.</summary>
                    private const float text_inset = 15;

                    private const float badge_gap = 6;

                    private readonly Container badgeContainer;

                    public BadgedContent()
                    {
                        AddInternal(badgeContainer = new Container
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            AutoSizeAxes = Axes.Both,
                            X = text_inset,
                        });
                    }

                    public void SetBadges(Drawable? badges)
                    {
                        badgeContainer.Clear();

                        if (badges != null)
                            badgeContainer.Add(badges);
                    }

                    protected override void Update()
                    {
                        base.Update();

                        // Read a frame late (the container's own auto-size only resolves after this
                        // Update runs), which is invisible: the row settles on its very next frame,
                        // before any of it is interactive.
                        float inset = badgeContainer.Children.Count == 0
                            ? text_inset
                            : text_inset + badgeContainer.DrawWidth + badge_gap;

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
                Add(new StarRatingPill(stars.Value, fontSize: 10));
        }
    }
}
