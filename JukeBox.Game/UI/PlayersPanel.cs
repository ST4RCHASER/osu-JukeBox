#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The Players section of the Playback tab: the multi-replay controls (mode, knockout, sort) that
/// used to live in Settings, plus per-player overrides — cursor colour and mods, keyed by the
/// replay through <see cref="PlayerOverrideStore"/>.
///
/// <para>
/// It only appears when the difficulty on screen has several replays, since that is the only time
/// there is anyone to configure. A single "apply to" target — every player, or one of them — drives
/// the colour swatches and the mod toggles, which is what makes 47 players configurable without 47
/// rows of controls.
/// </para>
/// </summary>
public partial class PlayersPanel : CompositeDrawable
{
    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    // Nullable like BeatmapVisuals' own: a host without a replay registry (some test scenes) simply
    // has no players to configure, and the panel stays hidden rather than failing to resolve.
    [Resolved(canBeNull: true)]
    private ReplayStore? replays { get; set; }

    [Resolved(canBeNull: true)]
    private PlayerOverrideStore? overrideStore { get; set; }

    // The multi-replay preload's progress, published by the combine layer. Null in a bare test host
    // (no combine to preload), in which case the buffer bar simply never shows.
    [Resolved(canBeNull: true)]
    private PreloadProgressTracker? preloadTracker { get; set; }

    // The user's imported-skin library, so a per-player skin override can offer imported skins, not
    // just the bundled ones. Null in a bare test host (no imports to offer).
    [Resolved(canBeNull: true)]
    private JukeBox.Game.LazerPlayer.SkinLibrary? skinLibrary { get; set; }

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    /// <summary>The "apply to" targets other than a specific player: index -1 means every player.</summary>
    private const int target_all = -1;

    /// <summary>The colours offered as swatches. Spread around the wheel so any two are told apart,
    /// which is the whole point of setting one by hand.</summary>
    private static readonly Color4[] palette =
    {
        new Color4(0xff, 0x4d, 0x4d, 0xff), new Color4(0xff, 0x9f, 0x40, 0xff),
        new Color4(0xff, 0xe0, 0x3d, 0xff), new Color4(0x7c, 0xff, 0x4d, 0xff),
        new Color4(0x3d, 0xff, 0xb0, 0xff), new Color4(0x40, 0xd0, 0xff, 0xff),
        new Color4(0x4d, 0x7c, 0xff, 0xff), new Color4(0xb0, 0x5c, 0xff, 0xff),
        new Color4(0xff, 0x5c, 0xd0, 0xff), new Color4(0xff, 0xff, 0xff, 0xff),
    };

    /// <summary>The mods a player can be re-rendered under here, with the small exclusivity groups
    /// that keep the set valid (you cannot be under both DoubleTime and HalfTime).</summary>
    private static readonly (string Acronym, string Label, int Group)[] mod_choices =
    {
        ("EZ", "Easy", 1),
        ("HR", "Hard Rock", 1),
        ("HD", "Hidden", 0),
        ("FL", "Flashlight", 0),
        ("DT", "Double Time", 2),
        ("NC", "Nightcore", 2),
        ("HT", "Half Time", 2),
    };

    private SettingsEnumDropdown<MultiReplayMode> multiReplayModeDropdown = null!;
    private SettingsEnumDropdown<KnockoutMode> knockoutModeDropdown = null!;
    private SettingsEnumDropdown<KnockoutSort> knockoutSortDropdown = null!;
    private SettingsCheckbox knockoutLiveSortCheckbox = null!;
    private SettingsCheckbox removeNameCheckbox = null!;
    private SettingsDropdown<string> targetDropdown = null!;
    private SettingsDropdown<string> skinDropdown = null!;
    private FillFlowContainer swatches = null!;
    private BufferBar preloadBar = null!;

    /// <summary>The BUNDLED gameplay skins a player can be given, as (menu label, stored key). A null
    /// key is the reset — fall back to the global skin. The user's imported skins are appended to
    /// these at load and whenever the library changes (see <see cref="rebuildSkinChoices"/>), keyed by
    /// folder through LazerChartLayer.CustomSkinKey so a per-player custom skin actually renders.</summary>
    private static readonly (string Display, string? Key)[] bundled_skin_choices =
    {
        ("Default (global skin)", null),
        ("Argon", "Argon"),
        ("Argon Pro", "ArgonPro"),
        ("Triangles", "Triangles"),
        ("Classic", "Classic"),
    };

    /// <summary>The bundled skins plus every imported one, as the dropdown currently lists them.</summary>
    private List<(string Display, string? Key)> skinChoices = bundled_skin_choices.ToList();
    private readonly Dictionary<string, SettingsCheckbox> modCheckboxes = new Dictionary<string, SettingsCheckbox>();
    private readonly Dictionary<string, BindableBool> modBindables = new Dictionary<string, BindableBool>();

    private readonly Bindable<string?> selectedOsuFile = new Bindable<string?>();

    /// <summary>The cursor colours the user has picked, remembered across sessions (config-backed) and
    /// shown as reusable swatches after the fixed palette. Newest last; capped so the row stays sane.</summary>
    private readonly Bindable<string> rememberedColoursSetting = new Bindable<string>(string.Empty);
    private List<Color4> rememberedColours = new List<Color4>();

    /// <summary>How many picked colours are remembered before the oldest drops off.</summary>
    private const int max_remembered_colours = 10;

    private osu.Game.Graphics.UserInterfaceV2.OsuColourPicker colourPicker = null!;

    /// <summary>A bound copy of the imported-skin library, so the skin dropdown re-lists the moment a
    /// .osk is imported. A bound copy (unbound on disposal) rather than a direct subscription, so a
    /// dead panel does not keep answering the shared library's changes. Null in a bare test host.</summary>
    private IBindableList<JukeBox.Game.LazerPlayer.ImportedSkin>? importedSkins;

    private IReadOnlyList<ReplayAttachment> currentPlayers = Array.Empty<ReplayAttachment>();

    /// <summary>The "apply to" target: <see cref="target_all"/>, or an index into
    /// <see cref="currentPlayers"/>.</summary>
    private int target = target_all;

    /// <summary>Guards the two-way binding between a target change and the checkbox states it sets,
    /// so writing a checkbox in code does not loop back into a store write.</summary>
    private bool refreshing;

    private Dictionary<string, Mod> modsByAcronym = new Dictionary<string, Mod>(StringComparer.Ordinal);

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;

        modsByAcronym = new OsuRuleset().CreateAllMods().ToDictionary(m => m.Acronym, m => m, StringComparer.Ordinal);

        multiReplayModeDropdown = new SettingsEnumDropdown<MultiReplayMode> { LabelText = "Multiple replays" };
        knockoutModeDropdown = new SettingsEnumDropdown<KnockoutMode> { LabelText = "Knockout" };
        knockoutSortDropdown = new SettingsEnumDropdown<KnockoutSort> { LabelText = "Rank players by" };
        knockoutLiveSortCheckbox = new SettingsCheckbox { LabelText = "Re-order the board as they play" };
        removeNameCheckbox = new SettingsCheckbox { LabelText = "Remove name after knockout" };
        targetDropdown = new SettingsDropdown<string> { LabelText = "Per-player settings for" };
        skinDropdown = new SettingsDropdown<string> { LabelText = "Gameplay skin", Items = skinChoices.Select(c => c.Display) };

        var content = new List<Drawable>
        {
            preloadRow(),
            multiReplayModeDropdown,
            knockoutModeDropdown,
            knockoutSortDropdown,
            knockoutLiveSortCheckbox,
            removeNameCheckbox,
            targetDropdown,
            colourRow(),
            skinDropdown,
        };

        foreach (var (acronym, label, _) in mod_choices)
        {
            var checkbox = new SettingsCheckbox { LabelText = label };
            var bindable = new BindableBool();

            checkbox.Current = bindable;
            bindable.BindValueChanged(e => onModToggled(acronym, e.NewValue));

            modCheckboxes[acronym] = checkbox;
            modBindables[acronym] = bindable;
            content.Add(checkbox);
        }

        InternalChild = new LazerSection("Players", FontAwesome.Solid.Users)
        {
            Children = content.ToArray(),
        };
    }

    /// <summary>
    /// The preload row: a caption and a YouTube-style buffer bar showing how much of the replays'
    /// timelines have been recorded. The whole row shows only while a preload is running (the combine
    /// publishes progress through <see cref="PreloadProgressTracker"/>) and fades out once every
    /// timeline is complete, so it does not sit empty during ordinary playback.
    /// </summary>
    private Drawable preloadRow()
    {
        return new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Horizontal = SettingsPanel.CONTENT_MARGINS, Vertical = 6 },
            Child = preloadBar = new BufferBar(),
        };
    }

    private Drawable colourRow()
    {
        return new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Horizontal = SettingsPanel.CONTENT_MARGINS, Vertical = 6 },
            Child = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Children = new Drawable[]
                {
                    new SpriteText { Text = "Cursor colour", Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize) },
                    // The palette, the reset chip, and every colour the user has picked so far.
                    swatches = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Full,
                        Spacing = new Vector2(6, 6),
                    },
                    // A real colour picker (HSV + hex) for any colour beyond the palette. Applying a
                    // picked colour also REMEMBERS it as a new swatch, so the user builds up their own
                    // reusable set — see applyPickedColour.
                    colourPicker = new osu.Game.Graphics.UserInterfaceV2.OsuColourPicker
                    {
                        RelativeSizeAxes = Axes.X,
                    },
                    new osu.Game.Graphics.UserInterfaceV2.RoundedButton
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 32,
                        Text = "Apply picked colour",
                        Action = applyPickedColour,
                    },
                },
            },
        };
    }

    /// <summary>Applies the colour picker's current colour to the target and REMEMBERS it as a new
    /// swatch, so picked colours accumulate as a reusable set.</summary>
    private void applyPickedColour()
    {
        var picked = colourPicker.Current.Value;
        var colour = new Color4(picked.R, picked.G, picked.B, 1f);

        rememberColour(colour);
        applyColour(colour);
    }

    /// <summary>Adds a picked colour to the remembered set (newest last, de-duplicated, capped) and
    /// persists it to config, which rebuilds the swatches.</summary>
    private void rememberColour(Color4 colour)
    {
        var updated = rememberedColours.Where(c => !sameColour(c, colour)).ToList();
        updated.Add(colour);

        if (updated.Count > max_remembered_colours)
            updated.RemoveRange(0, updated.Count - max_remembered_colours);

        rememberedColoursSetting.Value = string.Join(",", updated.Select(toHex));
    }

    private static bool sameColour(Color4 a, Color4 b)
        => toHex(a) == toHex(b);

    private static string toHex(Color4 c)
        => $"#{(int)Math.Round(c.R * 255):X2}{(int)Math.Round(c.G * 255):X2}{(int)Math.Round(c.B * 255):X2}";

    private static Color4? fromHex(string hex)
    {
        hex = hex.Trim().TrimStart('#');

        if (hex.Length != 6
            || !int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int r)
            || !int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g)
            || !int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            return null;
        }

        return new Color4(r / 255f, g / 255f, b / 255f, 1f);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        multiReplayModeDropdown.Current = config.GetBindable<MultiReplayMode>(JukeBoxSetting.MultiReplayMode);
        knockoutModeDropdown.Current = config.GetBindable<KnockoutMode>(JukeBoxSetting.KnockoutMode);
        knockoutSortDropdown.Current = config.GetBindable<KnockoutSort>(JukeBoxSetting.KnockoutSortBy);
        knockoutLiveSortCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.KnockoutLiveSort);
        removeNameCheckbox.Current = config.GetBindable<bool>(JukeBoxSetting.RemoveNameAfterKnockout);

        // The remembered picked colours, parsed from config and re-parsed (rebuilding the swatches)
        // whenever they change — including a pick made just now, which persists through here.
        config.BindWith(JukeBoxSetting.RememberedCursorColours, rememberedColoursSetting);
        rememberedColoursSetting.BindValueChanged(e =>
        {
            rememberedColours = e.NewValue
                                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(fromHex)
                                 .Where(c => c.HasValue)
                                 .Select(c => c!.Value)
                                 .ToList();
            buildSwatches();
        }, true);

        targetDropdown.Current.BindValueChanged(e => onTargetChanged(e.NewValue));
        skinDropdown.Current.BindValueChanged(e => onSkinChanged(e.NewValue));

        // Watch the selected difficulty only, through a bound copy that is unbound on disposal (see
        // Dispose). The set-level Current change is not watched separately: PlaybackController points
        // SelectedOsuFile at the set's preferred difficulty whenever a set loads, so this fires then
        // too — and a second subscription on the shared Current would be one more thing to leak, which
        // is exactly what fired refreshPlayers on already-disposed panels and NRE'd an unrelated test.
        selectedOsuFile.BindTo(playback.SelectedOsuFile);
        selectedOsuFile.BindValueChanged(_ => refreshPlayers());

        // Re-list the skin dropdown from the library now and whenever it changes (a .osk imported
        // while the panel is open). runOnceImmediately builds the initial list; with no library
        // (bare test host) the dropdown keeps just the bundled skins.
        importedSkins = skinLibrary?.Skins.GetBoundCopy();
        importedSkins?.BindCollectionChanged((_, __) => rebuildSkinChoices(), true);

        refreshPlayers();
    }

    protected override void Dispose(bool isDisposing)
    {
        // A rebuilt screen disposes this panel but the shared SelectedOsuFile lives on; without this
        // the dead panel keeps answering its changes and touches disposed drawables.
        selectedOsuFile.UnbindAll();
        importedSkins?.UnbindAll();
        base.Dispose(isDisposing);
    }

    /// <summary>The difficulty whose replays are on screen: the explicitly selected one, or the set's
    /// preferred difficulty when nothing is.</summary>
    private string? currentOsuFile => playback.SelectedOsuFile.Value ?? playback.Current.Value?.PreferredOsuFile;

    /// <summary>Re-reads the current difficulty's replays and reshapes the target list around them.
    /// The whole section hides when there are fewer than two — there is nobody to compare.</summary>
    private void refreshPlayers()
    {
        currentPlayers = replays?.AllForOsuFile(currentOsuFile) ?? Array.Empty<ReplayAttachment>();

        Alpha = currentPlayers.Count >= 2 ? 1 : 0;

        var items = new List<string> { "All players" };
        items.AddRange(currentPlayers.Select((r, i) => $"{i + 1}. {playerName(r)}"));

        targetDropdown.Items = items;

        // Keep the target if it still exists; otherwise fall back to All.
        if (target >= currentPlayers.Count)
            target = target_all;

        refreshing = true;
        targetDropdown.Current.Value = target == target_all ? items[0] : items[target + 1];
        refreshing = false;

        refreshTargetControls();
    }

    private static string playerName(ReplayAttachment replay)
        => replay.PlayerName.Length > 0 ? replay.PlayerName : "unknown";

    private void onTargetChanged(string? item)
    {
        if (refreshing || item == null)
            return;

        int index = targetDropdown.Items.ToList().IndexOf(item);
        target = index <= 0 ? target_all : index - 1;

        refreshTargetControls();
    }

    /// <summary>Points the colour highlight and the mod checkboxes at whatever the current target
    /// already has. For "all players" the mods start unchecked — there is no single shared truth to
    /// show — and a toggle applies to everyone from there.</summary>
    private void refreshTargetControls()
    {
        refreshing = true;

        var mods = target == target_all
            ? Array.Empty<string>()
            : (overrideStore?.Peek(currentPlayers[target])?.Mods?.Select(m => m.Acronym).ToArray() ?? Array.Empty<string>());

        foreach (var (acronym, _, _) in mod_choices)
            modBindables[acronym].Value = mods.Contains(acronym);

        string? skinKey = target == target_all ? null : overrideStore?.Peek(currentPlayers[target])?.SkinKey;
        skinDropdown.Current.Value = skinChoices.FirstOrDefault(c => c.Key == skinKey).Display ?? skinChoices[0].Display;

        refreshing = false;

        highlightSwatch();
    }

    private void onSkinChanged(string? display)
    {
        if (refreshing || display == null)
            return;

        string? key = skinChoices.FirstOrDefault(c => c.Display == display).Key;

        foreach (var replay in targetReplays())
            overrideStore?.SetSkin(replay, key);
    }

    /// <summary>
    /// Rebuilds the skin dropdown from the bundled skins plus every imported one, preserving the
    /// current target's selection across the rebuild. Called at load and whenever the library changes,
    /// so a .osk imported while the panel is open shows up as a per-player choice immediately.
    /// </summary>
    private void rebuildSkinChoices()
    {
        var imported = skinLibrary?.Skins ?? (IEnumerable<JukeBox.Game.LazerPlayer.ImportedSkin>)Array.Empty<JukeBox.Game.LazerPlayer.ImportedSkin>();

        skinChoices = bundled_skin_choices
                      .Concat(imported.Select(s => (Display: s.Label, Key: (string?)JukeBox.Game.LazerPlayer.LazerChartLayer.CustomSkinKey(s.Folder))))
                      .ToList();

        skinDropdown.Items = skinChoices.Select(c => c.Display);

        // Re-point the dropdown at the current target's stored skin — setting Items above resets it.
        refreshPlayers();
    }

    private void buildSwatches()
    {
        swatches.Clear();

        foreach (var colour in palette)
        {
            var captured = colour;
            swatches.Add(new ColourSwatch(colour, () => applyColour(captured)));
        }

        // The user's own remembered picks, after the fixed palette so a colour used once is a click
        // away next time.
        foreach (var colour in rememberedColours)
        {
            var captured = colour;
            swatches.Add(new ColourSwatch(colour, () => applyColour(captured)));
        }

        // The reset chip: hands the player back their hue-spread default.
        swatches.Add(new ColourSwatch(null, () => applyColour(null)));

        highlightSwatch();
    }

    private void highlightSwatch()
    {
        Color4? current = target == target_all ? null : overrideStore?.Peek(currentPlayers[target])?.CursorColour;

        foreach (var swatch in swatches.OfType<ColourSwatch>())
            swatch.SetSelected(current.HasValue && swatch.Colour is { } c && c.Equals(current.Value));
    }

    private void applyColour(Color4? colour)
    {
        foreach (var replay in targetReplays())
            overrideStore?.SetCursorColour(replay, colour);

        highlightSwatch();
    }

    private void onModToggled(string acronym, bool on)
    {
        if (refreshing)
            return;

        // Turning one on turns off the others in its exclusivity group, so the set stays valid.
        if (on)
        {
            int group = mod_choices.First(m => m.Acronym == acronym).Group;

            if (group != 0)
            {
                refreshing = true;
                foreach (var (other, _, otherGroup) in mod_choices)
                {
                    if (other != acronym && otherGroup == group)
                        modBindables[other].Value = false;
                }
                refreshing = false;
            }
        }

        applyMods();
    }

    private void applyMods()
    {
        var chosen = mod_choices
                     .Where(m => modBindables[m.Acronym].Value && modsByAcronym.ContainsKey(m.Acronym))
                     .Select(m => modsByAcronym[m.Acronym])
                     .ToList();

        foreach (var replay in targetReplays())
        {
            // Fresh instances per player — mods are stateful, so one shared instance across several
            // renders would be a bug. Empty means "no override": fall back to what they recorded.
            var mods = chosen.Count == 0
                ? null
                : (IReadOnlyList<Mod>)chosen.Select(cloneMod).ToList();

            overrideStore?.SetMods(replay, mods);
        }
    }

    private static Mod cloneMod(Mod mod) => (Mod)Activator.CreateInstance(mod.GetType())!;

    private IEnumerable<ReplayAttachment> targetReplays()
        => target == target_all ? currentPlayers : new[] { currentPlayers[target] };

    // ---- test hooks (JukeBox.Game.Tests has InternalsVisibleTo) ----

    internal SettingsEnumDropdown<MultiReplayMode> MultiReplayModeDropdown => multiReplayModeDropdown;
    internal SettingsEnumDropdown<KnockoutMode> KnockoutModeDropdown => knockoutModeDropdown;
    internal SettingsEnumDropdown<KnockoutSort> KnockoutSortDropdown => knockoutSortDropdown;
    internal SettingsCheckbox KnockoutLiveSortCheckbox => knockoutLiveSortCheckbox;

    internal SettingsCheckbox RemoveNameCheckbox => removeNameCheckbox;

    internal IReadOnlyList<ReplayAttachment> CurrentPlayers => currentPlayers;
    internal bool IsShowing => Alpha > 0;

    /// <summary>Test hook: choose the apply-to target (-1 for all players, else a player index).</summary>
    internal void SelectTarget(int index)
    {
        target = index;
        refreshTargetControls();
    }

    /// <summary>Test hook: pick a swatch colour (null = reset to default) for the current target.</summary>
    internal void PickColour(Color4? colour) => applyColour(colour);

    /// <summary>Test hook: toggle a mod for the current target by acronym.</summary>
    internal void SetMod(string acronym, bool on) => modBindables[acronym].Value = on;

    /// <summary>Test hook: pick a gameplay skin for the current target by its stored key (null =
    /// default/global).</summary>
    internal void SelectSkinKey(string? key)
        => skinDropdown.Current.Value = skinChoices.First(c => c.Key == key).Display;

    /// <summary>Test hook: the stored keys the skin dropdown currently offers, imported skins included
    /// (a null entry is the "default/global" reset).</summary>
    internal IEnumerable<string?> SkinChoiceKeys => skinChoices.Select(c => c.Key);

    /// <summary>Test hooks for the cursor colour picker and its remembered swatches.</summary>
    internal osu.Game.Graphics.UserInterfaceV2.OsuColourPicker ColourPicker => colourPicker;

    internal void ApplyPickedColour() => applyPickedColour();

    internal int SwatchCount => swatches.Count;

    /// <summary>One clickable colour chip. A null colour is the reset chip, drawn as an outlined
    /// ring rather than a filled dot.</summary>
    private partial class ColourSwatch : CompositeDrawable
    {
        public Color4? Colour { get; }

        private readonly Action onClick;
        private readonly Circle fill;
        private readonly Container ring;

        public ColourSwatch(Color4? colour, Action onClick)
        {
            Colour = colour;
            this.onClick = onClick;

            Size = new Vector2(24);

            InternalChildren = new Drawable[]
            {
                ring = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    BorderThickness = 2,
                    BorderColour = Color4.White,
                    Alpha = 0,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Transparent, AlwaysPresent = true },
                },
                fill = new Circle
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colour ?? new Color4(0.2f, 0.2f, 0.2f, 1f),
                    BorderThickness = colour == null ? 2 : 0,
                    BorderColour = Color4.White.Opacity(0.6f),
                    Masking = true,
                },
            };
        }

        public void SetSelected(bool selected) => ring.Alpha = selected ? 1 : 0;

        protected override bool OnClick(ClickEvent e)
        {
            onClick();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            fill.ScaleTo(1.15f, Theme.HoverFadeDuration, Easing.OutQuint);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
            => fill.ScaleTo(1f, Theme.HoverFadeDuration, Easing.OutQuint);
    }

    /// <summary>
    /// A YouTube-style buffer bar: a caption over a thin track whose grey fill grows to show how much
    /// of the replays' timelines have been preloaded. It reads its fraction from the shared
    /// <see cref="PreloadProgressTracker"/> and shows only while a preload is running — the combine
    /// marks the tracker inactive once every timeline is recorded, and the whole row fades away.
    /// </summary>
    internal partial class BufferBar : CompositeDrawable
    {
        [Resolved(canBeNull: true)]
        private PreloadProgressTracker? tracker { get; set; }

        private readonly Bindable<double> progress = new Bindable<double>(1);
        private readonly BindableBool active = new BindableBool();

        private readonly Box fill;
        private readonly SpriteText caption;

        private static readonly Color4 track_colour = new Color4(0.22f, 0.22f, 0.26f, 1f);
        private static readonly Color4 buffer_colour = new Color4(0.72f, 0.72f, 0.78f, 1f);

        public BufferBar()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Alpha = 0;

            InternalChild = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Children = new Drawable[]
                {
                    caption = new SpriteText
                    {
                        Text = "Preloading replays",
                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 6,
                        Masking = true,
                        CornerRadius = 3,
                        Children = new Drawable[]
                        {
                            // The whole track — the not-yet-buffered ground, always drawn.
                            new Box { RelativeSizeAxes = Axes.Both, Colour = track_colour },
                            // The buffered fill, its width the fraction preloaded.
                            fill = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Width = 0,
                                Colour = buffer_colour,
                            },
                        },
                    },
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (tracker != null)
            {
                progress.BindTo(tracker.Progress);
                active.BindTo(tracker.Active);
            }

            progress.BindValueChanged(e =>
            {
                float f = (float)Math.Clamp(e.NewValue, 0, 1);
                fill.Width = f;
                caption.Text = $"Preloading replays… {(int)(f * 100)}%";
            }, true);

            active.BindValueChanged(e => this.FadeTo(e.NewValue ? 1 : 0, 200, Easing.OutQuint), true);
        }

        /// <summary>Test hook: the buffered fraction the fill currently shows, 0 to 1.</summary>
        internal float FillFraction => fill.Width;

        /// <summary>Test hook: whether the bar is currently shown (a preload is running).</summary>
        internal bool Showing => Alpha > 0.5f;
    }

    /// <summary>Test hook: the preload buffer bar.</summary>
    internal BufferBar PreloadBar => preloadBar;
}
