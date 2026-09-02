#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.LazerPlayer;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osu.Game.Overlays.Dialog;
using osu.Game.Overlays.Settings;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The Settings tab's last section: the two destructive housekeeping actions, sitting at the
/// bottom because nothing here is a setting — they are things you DO, once, and they cannot be
/// undone.
///
/// <para>
/// Both confirm first, through lazer's own <see cref="DangerousActionDialog"/> pushed at the
/// <see cref="IDialogOverlay"/> (see <see cref="JukeBoxDialogOverlay"/> for why the host is ours
/// but the dialog is not), fronted by lazer's pink <see cref="DangerousSettingsButton"/>. That is
/// the same shape osu!'s own Settings → Maintenance uses.
/// </para>
///
/// <para>
/// Results are reported in a status line inside the section rather than as a toast. A toast is
/// owned by <c>MainScreen</c> and would have to be plumbed through it, and the outcome here is
/// something you want to read next to the button you just pressed — one line, replaced on each
/// operation, never stacked.
/// </para>
/// </summary>
internal partial class MaintenanceSection : LazerSection
{
    public MaintenanceSection()
        : base("Maintenance", FontAwesome.Solid.Broom)
    {
    }

    [Resolved]
    private BeatmapCache cache { get; set; } = null!;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    /// <summary>
    /// Optional for the same reason <see cref="SettingsOverlay"/>'s is: a settings test scene may
    /// cache a config manager and nothing else. Without a library the skin rows simply don't
    /// appear, and the cache row still works.
    /// </summary>
    [Resolved(canBeNull: true)]
    private SkinLibrary? skinLibrary { get; set; }

    /// <summary>
    /// Optional so this section can be constructed without a dialog host — but note what that
    /// means: with no host there is nowhere to confirm, so the destructive actions do NOTHING.
    /// Failing closed is the only safe direction for a delete.
    /// </summary>
    [Resolved(canBeNull: true)]
    private IDialogOverlay? dialogOverlay { get; set; }

    /// <summary>
    /// Total vertical margin lazer puts on every <see cref="SettingsButton"/> — <c>Vertical = -5</c>,
    /// so -10 across the pair. It is NEGATIVE, and a <see cref="FillFlowContainer"/> steps by a
    /// child's LayoutSize (DrawSize + margin), so that margin comes straight off the gap between
    /// two buttons. A plain <c>Spacing</c> of 8 renders as a 2px OVERLAP rather than an 8px gap —
    /// measured, not guessed, and invisible in the source either way.
    /// </summary>
    private const float settings_button_total_vertical_margin = -10;

    /// <summary>
    /// Cancels the margin above first, then adds the gap actually wanted, so the space between two
    /// rendered buttons really is <see cref="Theme.RowSpacing"/>.
    /// </summary>
    private static readonly Vector2 button_spacing = new Vector2(0, Theme.RowSpacing - settings_button_total_vertical_margin);

    private DangerousSettingsButton clearCacheButton = null!;
    private OsuSpriteText status = null!;
    private FillFlowContainer skinRows = null!;

    /// <summary>Test hook: the button that starts a cache clear.</summary>
    internal DangerousSettingsButton ClearCacheButton => clearCacheButton;

    /// <summary>Test hook: what the section last reported.</summary>
    internal string Status => status.Text.ToString();

    /// <summary>Test hook: the per-skin removal buttons, in listed order.</summary>
    internal IReadOnlyList<DangerousSettingsButton> SkinRemoveButtons
        => skinRows.Children.OfType<DangerousSettingsButton>().ToList();

    [BackgroundDependencyLoader]
    private void load()
    {
        Children = new Drawable[]
        {
            // The buttons need a gap or they render as one continuous pink slab — three touching
            // danger buttons read as a single enormous one. Nested flows rather than per-button
            // margins, so every gap, including the one between the cache button and the first
            // skin, comes from the same constant.
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = button_spacing,
                Children = new Drawable[]
                {
                    clearCacheButton = new DangerousSettingsButton
                    {
                        Text = "Clear beatmap cache",
                        Action = confirmClearCache,
                    },
                    skinRows = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = button_spacing,
                    },
                },
            },
            status = new OsuSpriteText
            {
                Font = OsuFont.GetFont(size: Theme.CaptionTextSize),
                Colour = Theme.TextTertiary,
                Margin = new MarginPadding { Left = SettingsPanel.CONTENT_MARGINS, Top = 6 },
                Alpha = 0,
            },
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (skinLibrary != null)
            skinLibrary.Skins.BindCollectionChanged((_, _) => rebuildSkinRows(), true);
    }

    private void rebuildSkinRows()
    {
        skinRows.Clear();

        if (skinLibrary == null)
            return;

        // Deliberately one button per skin rather than a picker plus a single Remove: the whole
        // point of the library is that the skins are individually named, and a row that says
        // exactly which skin it deletes cannot be misread the way a "remove the selected one"
        // button can.
        foreach (var skin in skinLibrary.Skins)
        {
            var target = skin;

            skinRows.Add(new DangerousSettingsButton
            {
                Text = $"Remove skin \"{target.Label}\"",
                Action = () => confirmRemoveSkin(target),
            });
        }
    }

    private void confirmClearCache()
    {
        // The set that is playing stays: its folder is still being read from for the rest of the
        // song (the storyboard resolves sprites lazily and video decodes continuously), so
        // deleting it would visibly break the song already on screen. Stopping playback to free a
        // few more megabytes is the more surprising of the two options, so it is not what happens.
        //
        // Queued sets are NOT protected — they re-download on demand when they come up, so
        // holding them back would cost disk for nothing.
        int[] inUse = playback.Current.Value is { } current ? new[] { current.SetId } : Array.Empty<int>();

        confirm(new ClearCacheDialog(() =>
        {
            clearCacheButton.Enabled.Value = false;
            report("Clearing…");

            Task.Run(() => cache.Clear(inUse))
                .ContinueWith(t => Schedule(() =>
                {
                    clearCacheButton.Enabled.Value = true;
                    report(t.IsCompletedSuccessfully ? describe(t.Result) : "Could not clear the cache — see the log.");
                }));
        }));
    }

    private void confirmRemoveSkin(ImportedSkin skin)
    {
        confirm(new RemoveSkinDialog(skin.Label, () =>
        {
            // Move the selection off the skin BEFORE deleting it, so SkinSelection is never
            // resolving a folder that is mid-deletion. Classic, not Argon: Classic is this app's
            // legacy-fidelity default, and someone running an imported legacy skin is closer to
            // where they were with Classic than with Argon.
            bool wasSelected = config.Get<JukeBoxSkin>(JukeBoxSetting.Skin) == JukeBoxSkin.Custom
                               && config.Get<string>(JukeBoxSetting.CustomSkinPath) == skin.Folder;

            if (wasSelected)
            {
                config.SetValue(JukeBoxSetting.Skin, JukeBoxSkin.Classic);
                config.SetValue(JukeBoxSetting.CustomSkinPath, string.Empty);
            }

            bool deleted = skinLibrary!.Delete(skin.Folder);

            if (!deleted)
                report($"Could not remove \"{skin.Label}\" — see the log.");
            else if (wasSelected)
                report($"Removed \"{skin.Label}\". Gameplay skin is now Classic.");
            else
                report($"Removed \"{skin.Label}\".");
        }));
    }

    /// <summary>
    /// Pushes a confirmation, or does nothing at all when there is nowhere to push one. A missing
    /// dialog host must never mean "go ahead unconfirmed".
    /// </summary>
    private void confirm(PopupDialog dialog)
    {
        if (dialogOverlay == null)
        {
            report("Cannot confirm here — nothing was changed.");
            return;
        }

        dialogOverlay.Push(dialog);
    }

    private void report(string message)
    {
        status.Text = message;
        status.Alpha = 1;
    }

    private static string describe(CacheClearResult result)
    {
        if (result.SetsDeleted == 0 && result.SetsKeptInUse == 0 && result.SetsKeptLocal == 0)
            return "Cache was already empty.";

        string freed = $"Cleared {result.SetsDeleted} beatmap{(result.SetsDeleted == 1 ? "" : "s")}, {formatBytes(result.BytesFreed)} freed.";

        var kept = new List<string>();

        if (result.SetsKeptInUse > 0)
            kept.Add("1 still playing");

        if (result.SetsKeptLocal > 0)
            kept.Add($"{result.SetsKeptLocal} imported by hand");

        return kept.Count > 0 ? $"{freed} Kept {string.Join(" and ", kept)}." : freed;
    }

    private static string formatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";

        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):0.#} MB";

        if (bytes >= 1024)
            return $"{bytes / 1024.0:0.#} KB";

        return $"{bytes} bytes";
    }

    /// <summary>
    /// lazer's caution dialog, worded for this app's cache. Subclassing
    /// <see cref="DangerousActionDialog"/> is what gets the warning triangle, the hold-to-confirm
    /// dangerous button and the cancel button — none of that is written here.
    /// </summary>
    internal partial class ClearCacheDialog : DangerousActionDialog
    {
        public ClearCacheDialog(Action clear)
        {
            BodyText = "Every downloaded beatmap will be deleted. Anything still queued downloads again when it plays.";
            DangerousAction = clear;
        }
    }

    internal partial class RemoveSkinDialog : DangerousActionDialog
    {
        public RemoveSkinDialog(string skinName, Action remove)
        {
            BodyText = $"\"{skinName}\" will be deleted from disk. Re-importing its .osk is the only way back.";
            DangerousAction = remove;
        }
    }
}
