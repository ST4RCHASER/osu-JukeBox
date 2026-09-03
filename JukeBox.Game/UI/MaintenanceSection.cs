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
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
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

    /// <summary>Row height for one installed skin — lazer's own settings-row rhythm.</summary>
    private const float skin_row_height = 32;

    /// <summary>The remove button is square and small: it sits beside a name, not over it.</summary>
    private const float remove_button_size = 22;

    private DangerousSettingsButton clearCacheButton = null!;
    private OsuSpriteText status = null!;
    private OsuSpriteText emptyNote = null!;
    private FillFlowContainer skinRows = null!;

    /// <summary>Test hook: the button that starts a cache clear.</summary>
    internal DangerousSettingsButton ClearCacheButton => clearCacheButton;

    /// <summary>Test hook: what the section last reported.</summary>
    internal string Status => status.Text.ToString();

    private IEnumerable<SkinRow> rows => skinRows.Children.OfType<SkinRow>();

    /// <summary>Test hook: the per-skin removal buttons, in listed order.</summary>
    internal IReadOnlyList<RemoveSkinButton> SkinRemoveButtons => rows.Select(r => r.RemoveButton).ToList();

    /// <summary>Test hook: the skin names as the rows actually render them.</summary>
    internal IReadOnlyList<string> SkinNames => rows.Select(r => r.DisplayedName).ToList();

    /// <summary>Test hook: the rows themselves, for geometry.</summary>
    internal IReadOnlyList<SkinRow> SkinRows => rows.ToList();

    /// <summary>Test hook: whether the "nothing imported" line is showing.</summary>
    internal bool EmptyNoteShown => emptyNote.Alpha > 0;

    [BackgroundDependencyLoader]
    private void load()
    {
        Children = new Drawable[]
        {
            // Skins are a LIST, and a list of things reads as rows — a name you can read and a way
            // to remove it. The previous shape gave each skin a full-width danger button carrying
            // its whole name, which stacked into a wall of pink and centre-cropped anything long
            // ("e skin \"StepOsu!Mania+v3.4Reborn(DownVe"), so the one thing the row existed to
            // tell you was the first thing it lost.
            new LazerSubsection("Installed skins")
            {
                Children = new Drawable[]
                {
                    skinRows = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                    },
                    emptyNote = new OsuSpriteText
                    {
                        Text = "Nothing imported yet — drop a .osk on the window.",
                        Font = OsuFont.GetFont(size: Theme.CaptionTextSize),
                        Colour = Theme.TextTertiary,
                        Margin = new MarginPadding { Left = SettingsPanel.CONTENT_MARGINS, Vertical = 4 },
                    },
                },
            },

            // The one action that still looks dangerous, and the only one that is irreversible for
            // data the app cannot fetch again. Danger styling spent on every row made none of it
            // mean anything; spent here alone it reads as the warning it is.
            clearCacheButton = new DangerousSettingsButton
            {
                Text = "Clear beatmap cache",
                Action = confirmClearCache,
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

        if (skinLibrary != null)
        {
            // Deliberately one row per skin rather than a picker plus a single Remove: the whole
            // point of the library is that the skins are individually named, and a row that says
            // exactly which skin it deletes cannot be misread the way a "remove the selected one"
            // button can.
            foreach (var skin in skinLibrary.Skins)
                skinRows.Add(new SkinRow(skin, () => confirmRemoveSkin(skin), skin_row_height, remove_button_size));
        }

        // A bare "Installed skins" heading over nothing reads as a bug, so say what is missing and
        // how to fix it.
        emptyNote.Alpha = skinRows.Children.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// One installed skin: its name, and a way to remove it. A named type rather than an anonymous
    /// container so the row can report what it is showing — the name and the button are separate
    /// drawables, and a caller reaching into the tree for either would be guessing.
    /// </summary>
    internal partial class SkinRow : Container
    {
        private readonly TruncatingSpriteText name;

        public SkinRow(ImportedSkin skin, Action remove, float height, float buttonSize)
        {
            RelativeSizeAxes = Axes.X;
            Height = height;
            Padding = new MarginPadding { Horizontal = SettingsPanel.CONTENT_MARGINS };

            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.Both,

                    // Reserve the button's column so a long name is cut with an ellipsis rather
                    // than sliding under it. Losing the END of a name is survivable; losing its
                    // middle to a centred crop, which is what the old full-width buttons did, is
                    // not — that is the half that tells two skins apart.
                    Padding = new MarginPadding { Right = buttonSize + Theme.RowSpacing },
                    // TruncatingSpriteText, not OsuSpriteText with Truncate set — the framework
                    // throws outright on the latter ("Use TruncatingSpriteText instead"), which is
                    // its way of saying eliding needs a type that measures before it lays out.
                    Child = name = new TruncatingSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        RelativeSizeAxes = Axes.X,
                        Text = skin.Label,
                        Font = OsuFont.GetFont(size: Theme.RowTitleTextSize),
                        Colour = Theme.TextPrimary,
                    },
                },
                RemoveButton = new RemoveSkinButton(skin.Label)
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Size = new Vector2(buttonSize),
                    Action = remove,
                },
            };
        }

        public RemoveSkinButton RemoveButton { get; }

        /// <summary>What this row is actually showing.</summary>
        public string DisplayedName => name.Text.ToString();

        /// <summary>
        /// The name drawable, so a caller can check it never reaches the button. Not called
        /// <c>Name</c>: <see cref="Drawable"/> already has a <c>Name</c> (its debug label), and
        /// shadowing it would make <c>row.Name</c> mean one thing here and another everywhere else.
        /// </summary>
        internal Drawable NameDrawable => name;
    }

    /// <summary>
    /// The remove control for one skin. Its own type so a caller can find the buttons and read
    /// which skin each belongs to.
    /// </summary>
    internal partial class RemoveSkinButton : IconButton, IHasTooltip
    {
        public RemoveSkinButton(string skinLabel)
        {
            SkinLabel = skinLabel;
            Icon = FontAwesome.Solid.TrashAlt;

            // Red only on hover: a column of permanently-red icons reads as a list of errors.
            IconColour = Theme.TextSecondary;
            HoverColour = Theme.Error;
        }

        /// <summary>Which skin this removes — the tooltip, and what tests assert on.</summary>
        public string SkinLabel { get; }

        public LocalisableString TooltipText => $"Remove \"{SkinLabel}\"";
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
