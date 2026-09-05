#nullable enable

using System;
using System.Collections.Generic;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The top menu bar — an osu!lazer-editor-style strip across the very top of the window carrying
/// <b>File · Playback · Queue · Spectate · Help</b>, each opening a dropdown of items with shortcut
/// hints on the right.
///
/// <para>
/// AUTO-HIDE: the bar is out of the way by default (slid up off the top edge) and only reveals while
/// the cursor is at the top of the window; it slides back when the cursor leaves, after a short grace
/// so a slightly-off approach doesn't dismiss it mid-reach. An OPEN dropdown pins it visible until the
/// menu is closed, so a dropdown is never yanked out from under the cursor. The reveal is driven from
/// <see cref="Update"/> reading the real mouse position rather than from hover events, because a bar
/// that is slid away receives no hover of its own to come back on.
/// </para>
///
/// <para>
/// The bar knows nothing about the app: every item calls back through the injected
/// <see cref="Actions"/> record, and the two pieces of state it must REFLECT (Render greyed while
/// spectating, the Spectate toggle's Start/Stop wording) arrive as bindables on that same record.
/// Styling is the shared <see cref="Theme"/> throughout.
/// </para>
/// </summary>
public partial class MenuBar : CompositeDrawable
{
    private const float bar_height = 30;

    /// <summary>How close to the very top edge the cursor must come to summon a hidden bar.</summary>
    private const float reveal_zone = 6;

    /// <summary>How long the bar lingers after the cursor leaves, so a near-miss on the way to it
    /// doesn't dismiss it.</summary>
    private const double grace_ms = 400;

    private const float dropdown_width = 250;

    /// <summary>The callbacks and reflected state the bar drives. Injected so the bar is decoupled
    /// from the screen that owns the real behaviour.</summary>
    public MenuBarActions Actions { get; init; } = new MenuBarActions();

    private Container bar = null!;
    private FillFlowContainer headerFlow = null!;
    private Container dropdownLayer = null!;
    private CloseCatcher catcher = null!;

    private readonly List<MenuHeader> headers = new List<MenuHeader>();
    private readonly List<Container> dropdowns = new List<Container>();
    private readonly List<MenuRow> allRows = new List<MenuRow>();

    private MenuRow spectateToggleRow = null!;

    /// <summary>Which menu is open, or -1 for none.</summary>
    private int openIndex = -1;

    private bool barShown;
    private double lastRevealTime = double.MinValue;

    // ---- Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) ----------------------------

    internal bool BarShown => barShown;

    internal bool IsMenuOpen => openIndex >= 0;

    internal IReadOnlyList<MenuHeader> Headers => headers;

    internal IReadOnlyList<MenuRow> Rows => allRows;

    /// <summary>Locates a built item by its (current) label — used by tests to click it and to read
    /// its enabled state.</summary>
    internal MenuRow? FindRow(string label)
    {
        foreach (var row in allRows)
        {
            if (row.Label == label)
                return row;
        }

        return null;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        headerFlow = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.Y,
            AutoSizeAxes = Axes.X,
            Direction = FillDirection.Horizontal,
            Padding = new MarginPadding { Left = 6 },
        };

        InternalChildren = new Drawable[]
        {
            // Behind everything: swallows a click that lands outside an open menu and closes it.
            // Non-present while nothing is open (see ReceivePositionalInputAt), so it never eats a
            // click meant for the app underneath.
            catcher = new CloseCatcher { OnClicked = closeMenu, IsActive = () => openIndex >= 0 },
            dropdownLayer = new Container { RelativeSizeAxes = Axes.Both },
            bar = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = bar_height,
                // Starts hidden: slid up off the top edge and faded out.
                Y = -bar_height,
                Alpha = 0,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.PanelSurface,
                    },
                    headerFlow,
                },
            },
        };

        buildMenus();
    }

    private void buildMenus()
    {
        string cmd = RuntimeInfo.IsApple ? "⌘" : "Ctrl+";

        addMenu("File", new[]
        {
            new Item { Label = "Open…", Shortcut = cmd + "O", Action = () => Actions.OpenFiles?.Invoke() },
            new Item { Label = "Render…", Action = () => Actions.OpenRender?.Invoke(), Enabled = Actions.RenderEnabled },
            Item.Separator,
            // ⌘Q is macOS's own quit and reaches the host as a close; nothing binds Ctrl+Q to quit
            // elsewhere (Ctrl+Q focuses the Playback tab), so only the Mac shows a chip.
            new Item { Label = "Quit", Shortcut = RuntimeInfo.IsApple ? "⌘Q" : null, Action = () => Actions.Quit?.Invoke() },
        });

        addMenu("Playback", new[]
        {
            new Item { Label = "Play", Shortcut = "Space", Action = () => Actions.Play?.Invoke() },
            new Item { Label = "Pause", Action = () => Actions.Pause?.Invoke() },
            new Item { Label = "Next", Action = () => Actions.Next?.Invoke() },
            new Item { Label = "Restart", Shortcut = "Home", Action = () => Actions.Restart?.Invoke() },
            Item.Separator,
            new Item { Label = "Open beatmap page", Action = () => Actions.OpenBeatmapPage?.Invoke() },
        });

        addMenu("Queue", new[]
        {
            new Item { Label = "Lookup by id…", Action = () => Actions.LookupById?.Invoke() },
            new Item { Label = "Search…", Action = () => Actions.SearchBeatmaps?.Invoke() },
        });

        addMenu("Spectate", new[]
        {
            // The one control the Spectating bindable names the current side of. Held so its label
            // can be flipped below.
            new Item { Label = "Start spectating", Action = () => Actions.ToggleSpectate?.Invoke(), SpectateToggle = true },
            new Item { Label = "Setup players…", Action = () => Actions.SetupPlayers?.Invoke() },
        });

        addMenu("Help", new[]
        {
            new Item { Label = "Show all shortcut keys", Action = () => Actions.ShowShortcuts?.Invoke() },
        });

        // Reflect spectating state onto the toggle's wording.
        Actions.Spectating.BindValueChanged(
            e => spectateToggleRow.SetLabel(e.NewValue ? "Stop spectating" : "Start spectating"), true);
    }

    private void addMenu(string title, IReadOnlyList<Item> items)
    {
        int index = headers.Count;

        var header = new MenuHeader(title)
        {
            Clicked = () => toggleMenu(index),
            HoverRequested = () => switchTo(index),
        };

        headers.Add(header);
        headerFlow.Add(header);

        var panel = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Padding = new MarginPadding { Vertical = 6 },
        };

        foreach (var item in items)
        {
            if (item.SeparatorItem)
            {
                panel.Add(new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 9,
                    Child = new Box
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 1,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Colour = Theme.TextTertiary.Opacity(0.35f),
                    },
                });
                continue;
            }

            // Close first, THEN run the item's action — so an action that opens a modal (which grabs
            // focus) doesn't fight a menu still tearing itself down.
            Action itemAction = item.Action ?? (() => { });
            var row = new MenuRow(item.Label, item.Shortcut, () =>
            {
                closeMenu();
                itemAction();
            }, item.Enabled);

            if (item.SpectateToggle)
                spectateToggleRow = row;

            allRows.Add(row);
            panel.Add(row);
        }

        var dropdown = new Container
        {
            Width = dropdown_width,
            AutoSizeAxes = Axes.Y,
            Masking = true,
            CornerRadius = Theme.CornerRadius,
            EdgeEffect = Theme.PanelShadow,
            Alpha = 0,
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.PanelSurface,
                },
                panel,
            },
        };

        dropdowns.Add(dropdown);
        dropdownLayer.Add(dropdown);
    }

    // ---- Open / close ----------------------------------------------------------------------------

    private void toggleMenu(int index)
    {
        if (openIndex == index)
            closeMenu();
        else
            openMenu(index);
    }

    /// <summary>Hovering another header while a menu is already open switches to it, matching lazer.
    /// Does nothing if no menu is open — hover alone never opens one.</summary>
    private void switchTo(int index)
    {
        if (openIndex >= 0 && openIndex != index)
            openMenu(index);
    }

    private void openMenu(int index)
    {
        if (openIndex >= 0 && openIndex != index)
            hideDropdown(openIndex);

        openIndex = index;
        headers[index].Active = true;

        var dropdown = dropdowns[index];

        // Position the dropdown flush under its header. BottomLeft is read live so it tracks the
        // bar's reveal slide (the bar sits at Y 0 whenever a menu can be opened).
        var pos = ToLocalSpace(headers[index].ScreenSpaceDrawQuad.BottomLeft);
        dropdown.Position = pos;
        dropdown.FadeIn(Theme.DurationFast, Theme.EaseEnter);
    }

    private void closeMenu()
    {
        if (openIndex < 0)
            return;

        hideDropdown(openIndex);
        openIndex = -1;
    }

    private void hideDropdown(int index)
    {
        headers[index].Active = false;
        dropdowns[index].FadeOut(Theme.DurationFast, Theme.EaseExit);
    }

    protected override void Update()
    {
        base.Update();

        var inputManager = GetContainingInputManager();

        bool nearTop = false;

        if (inputManager != null)
        {
            var local = ToLocalSpace(inputManager.CurrentState.Mouse.Position);
            bool withinX = local.X >= 0 && local.X <= DrawWidth;

            // A wider band once shown (the whole bar keeps it up); just the top edge while hidden.
            float band = barShown ? bar_height : reveal_zone;
            nearTop = withinX && local.Y >= 0 && local.Y <= band;
        }

        // An open menu pins the bar regardless of where the cursor has wandered.
        bool wantVisible = openIndex >= 0 || nearTop;

        if (wantVisible)
            lastRevealTime = Time.Current;

        bool shouldShow = wantVisible || (Time.Current - lastRevealTime) < grace_ms;

        setShown(shouldShow);
    }

    private void setShown(bool value)
    {
        if (barShown == value)
            return;

        barShown = value;

        bar.MoveToY(value ? 0 : -bar_height, Theme.DurationFast, value ? Theme.EaseEnter : Theme.EaseExit);
        bar.FadeTo(value ? 1 : 0, Theme.DurationFast);
    }

    // The bar's own strip is only live once shown; an open menu makes the whole layer live so a
    // click anywhere either lands on a row or closes the menu. While hidden and closed the layer is
    // transparent to input, so nothing under the top of the app is blocked.
    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
    {
        if (openIndex >= 0)
            return true;

        if (!barShown)
            return false;

        var local = ToLocalSpace(screenSpacePos);
        return local.X >= 0 && local.X <= DrawWidth && local.Y >= 0 && local.Y <= bar_height;
    }

    /// <summary>Declarative description of one dropdown entry, turned into a <see cref="MenuRow"/>
    /// (or a divider) by <see cref="addMenu"/>.</summary>
    private sealed class Item
    {
        public string Label = string.Empty;
        public string? Shortcut;
        public Action? Action;

        /// <summary>Optional gate — while false the item shows greyed and refuses clicks (Render
        /// while spectating).</summary>
        public IBindable<bool>? Enabled;

        public bool SeparatorItem;

        /// <summary>The single Spectate row whose label the bar rewrites from the Spectating
        /// bindable.</summary>
        public bool SpectateToggle;

        public static Item Separator => new Item { SeparatorItem = true };
    }

    /// <summary>A top-level title in the bar (File, Playback…). Clicking toggles its menu; hovering
    /// while another menu is open switches to this one. Highlights while its menu is open.</summary>
    internal partial class MenuHeader : ClickableContainer
    {
        public Action? Clicked;
        public Action? HoverRequested;

        private readonly Box background;
        private readonly SpriteText label;

        private bool active;

        /// <summary>Test-only access to the title text.</summary>
        internal string Title => label.Text.ToString();

        public bool Active
        {
            set
            {
                active = value;
                updateBackground();
            }
        }

        public MenuHeader(string text)
        {
            AutoSizeAxes = Axes.X;
            RelativeSizeAxes = Axes.Y;
            Masking = true;
            CornerRadius = 4;

            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.ElevatedSurface,
                    Alpha = 0,
                },
                label = new SpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Padding = new MarginPadding { Horizontal = 12 },
                    Text = text,
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextPrimary,
                },
            };

            Action = () => Clicked?.Invoke();
        }

        private void updateBackground() => background.FadeTo(active || IsHovered ? 1 : 0, Theme.HoverFadeDuration);

        protected override bool OnHover(HoverEvent e)
        {
            HoverRequested?.Invoke();
            updateBackground();
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            updateBackground();
            base.OnHoverLost(e);
        }
    }

    /// <summary>One dropdown item: a label, an optional shortcut chip pinned right, and a hover
    /// highlight. Its <see cref="ClickableContainer.Enabled"/> is bound to the item's gate (when it
    /// has one), so a disabled row both greys out and refuses to fire.</summary>
    internal partial class MenuRow : ClickableContainer
    {
        private const float row_height = 30;

        private readonly string? shortcut;
        private readonly IBindable<bool>? enabledSource;

        private Box background = null!;
        private SpriteText label = null!;
        private ShortcutChip? chip;

        /// <summary>The row's current label — how tests locate it and the key the Spectate toggle
        /// rewrites.</summary>
        public string Label { get; private set; }

        public MenuRow(string text, string? shortcut, Action action, IBindable<bool>? enabled)
        {
            Label = text;
            this.shortcut = shortcut;
            enabledSource = enabled;
            Action = action;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            Height = row_height;

            var content = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Horizontal = 12 },
                Children = new Drawable[]
                {
                    label = new SpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = Label,
                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                        Colour = Theme.TextPrimary,
                    },
                },
            };

            if (shortcut != null)
            {
                content.Add(chip = new ShortcutChip(shortcut)
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                });
            }

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.ElevatedSurface,
                    Alpha = 0,
                },
                content,
            };

            // Mirror the gate onto the base Enabled bindable (which TriggerClick honours) rather
            // than BindTo-ing it: the source is an IBindable<bool>, and a one-way follow is all a
            // gate the bar never writes back needs.
            if (enabledSource != null)
                enabledSource.BindValueChanged(e => Enabled.Value = e.NewValue, true);

            Enabled.BindValueChanged(e => updateEnabledVisual(e.NewValue), true);
        }

        internal void SetLabel(string text)
        {
            Label = text;
            if (label != null)
                label.Text = text;
        }

        private void updateEnabledVisual(bool enabled)
        {
            label.Colour = enabled ? Theme.TextPrimary : Theme.TextTertiary;
            if (chip != null)
                chip.Alpha = enabled ? 1 : 0.4f;
        }

        protected override bool OnHover(HoverEvent e)
        {
            if (Enabled.Value)
                background.FadeTo(1, Theme.HoverFadeDuration);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeTo(0, Theme.HoverFadeDuration);
            base.OnHoverLost(e);
        }
    }

    /// <summary>The full-screen catcher behind an open menu. Present only while a menu is open, so a
    /// click outside the dropdown closes it and every other time the app underneath is untouched.</summary>
    private partial class CloseCatcher : Drawable
    {
        public Action? OnClicked;

        /// <summary>Whether a menu is open right now — the catcher only intercepts clicks then.</summary>
        public Func<bool> IsActive = () => false;

        public CloseCatcher()
        {
            RelativeSizeAxes = Axes.Both;
        }

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => IsActive();

        protected override bool OnClick(ClickEvent e)
        {
            OnClicked?.Invoke();
            return true;
        }
    }
}

/// <summary>
/// A small rounded key chip ("⌘S", "Space") like lazer's shortcut hints — an elevated-surface pill
/// with tertiary text. Shared by the <see cref="MenuBar"/> dropdowns and the
/// <see cref="ShortcutsOverlay"/> so the two render keys identically.
/// </summary>
internal partial class ShortcutChip : CompositeDrawable
{
    public ShortcutChip(string text)
    {
        AutoSizeAxes = Axes.Both;
        Masking = true;
        CornerRadius = 4;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ElevatedSurface,
            },
            new SpriteText
            {
                Padding = new MarginPadding { Horizontal = 6, Vertical = 2 },
                Text = text,
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextSecondary,
            },
        };
    }
}
