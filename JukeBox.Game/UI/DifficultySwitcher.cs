#nullable enable

using System.Linq;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterface;

namespace JukeBox.Game.UI;

/// <summary>
/// Lazer-style dropdown listing every difficulty of the currently playing set, each item labelled
/// "[mode] Version" (e.g. "[taiko] Muzukashii"). Picking an item switches chart/hitsounds/
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
        AutoSizeAxes = Axes.Both;

        InternalChild = dropdown = new DifficultyDropdown
        {
            Width = 220,
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        playback.Current.BindValueChanged(onSetChanged, true);
        playback.SelectedOsuFile.BindValueChanged(_ => syncSelection());
        dropdown.Current.BindValueChanged(onSelectionCommitted);
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

            if (set == null || set.Difficulties.Count == 0)
            {
                dropdown.Items = System.Array.Empty<DifficultyInfo?>();
                dropdown.Hide(); // AutoSizeAxes on this CompositeDrawable then collapses to zero size, same as the old empty chip flow
                return;
            }

            dropdown.Show();
            dropdown.Items = set.Difficulties.Take(max_items).Cast<DifficultyInfo?>().ToArray();
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

    /// <summary>Item labels read "[mode] Version" (e.g. "[taiko] Muzukashii"); real per-item
    /// ruleset icons (lazer's <c>Ruleset.CreateIcon()</c>, as used elsewhere in this app — see
    /// <c>FullscreenBeatmapCard.CreateRulesetIcon</c>) were skipped here: doing so requires
    /// overriding <c>OsuDropdownMenu</c>'s nested item-content drawable, which is a much larger
    /// surface than the plain text override below justifies for this list.</summary>
    internal partial class DifficultyDropdown : OsuDropdown<DifficultyInfo?>
    {
        protected override LocalisableString GenerateItemText(DifficultyInfo? item)
            => item == null ? string.Empty : $"[{modeLabel(item.Mode)}] {item.Version}";

        /// <summary>Test-only access to the protected label generator (JukeBox.Game.Tests has
        /// InternalsVisibleTo).</summary>
        internal LocalisableString GenerateItemTextForTest(DifficultyInfo? item) => GenerateItemText(item);

        // Every known mode (std/taiko/catch/mania) now has a native chart renderer; unknown future
        // modes still play (audio/storyboard) just chartless, labelled "?" rather than guessed.
        private static string modeLabel(int mode) => mode switch
        {
            0 => "osu!",
            1 => "taiko",
            2 => "catch",
            3 => "mania",
            _ => "?",
        };
    }
}
