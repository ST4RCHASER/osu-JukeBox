#nullable enable

using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.Screens;

/// <summary>
/// Top-level screen: a single fixed three-column layout — a permanently-docked left search column,
/// the <see cref="NowPlayingScreen"/> visuals filling the centre, a permanently-docked right column
/// (tabbed Queue/Settings) and the full-width <see cref="NowPlayingBar"/> along the bottom — driven
/// by <see cref="JukeBoxSetting.UiLayout"/>. Replaces the old Fullscreen/Split layout-toggle pair;
/// see <see cref="UiLayout"/> for the config migration story.
///
/// <para>
/// The left column embeds <see cref="BeatmapListingOverlay"/> in its <c>docked</c> mode (permanently
/// visible — no more show/hide overlay semantics; see that class). Typing a printable character
/// (with no modifiers held) focuses+seeds it via <see cref="BeatmapListingOverlay.ShowWithInitialChar"/>,
/// same entry point as before, just without a pop-in. The right column embeds <see cref="QueuePanel"/>
/// and <see cref="SettingsOverlay"/> (also docked) side by side behind two tab buttons — both stay
/// permanently loaded and alive, so switching tabs is a simple Alpha toggle (instant, and every
/// component's own state — scroll position, filter selections, checkbox values — just sits there
/// untouched while its tab isn't the active one).
/// </para>
///
/// <para>
/// Tab is repurposed from "toggle layout" to "focus mode": it hides both side columns (letting the
/// visuals go full-bleed; the bottom bar stays) and pressing it again restores the three-column
/// layout. Ctrl+Q switches the right column to its Queue tab (kept, despite the drawer no longer
/// being independently hideable, as a quick "jump to queue" shortcut); the corner gear button now
/// switches to the Settings tab instead of popping a modal (that overlay no longer floats — its
/// content lives inline in the tab); the corner "#" button is unchanged, still opening
/// <see cref="MapIdOverlay"/> to queue a set directly by beatmapset ID.
/// </para>
/// </summary>
public partial class MainScreen : Screen
{
    private const float left_column_width = 380;
    private const float right_column_width = 340;
    private const float tab_header_height = 36;

    // Mirrors NowPlayingBar's own (private) bar_height constant — kept in sync manually since the
    // columns need to know how much room to leave above the bar without depending on that class's
    // internals.
    private const float bottom_bar_height = 88;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    private Bindable<UiLayout> uiLayout = null!;

    private Container visualsHost = null!;
    private ScreenStack visualsStack = null!;

    private BeatmapListingOverlay listing = null!;
    private QueuePanel queuePanel = null!;
    private SettingsOverlay settingsBody = null!;
    private MapIdOverlay mapIdOverlay = null!;

    private RightPanelTabButton queueTabButton = null!;
    private RightPanelTabButton settingsTabButton = null!;

    private RightPanelTab currentTab = RightPanelTab.Queue;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the left column, to assert
    /// its Alpha (focus mode) without depending on layout internals.
    /// </summary>
    internal Container LeftColumn { get; private set; } = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the right column, to assert
    /// its Alpha (focus mode) without depending on layout internals.
    /// </summary>
    internal Container RightColumn { get; private set; } = null!;

    private enum RightPanelTab
    {
        Queue,
        Settings,
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        listing = new BeatmapListingOverlay(docked: true) { RelativeSizeAxes = Axes.Both };
        queuePanel = new QueuePanel(docked: true);
        settingsBody = new SettingsOverlay(docked: true) { RelativeSizeAxes = Axes.Both };
        mapIdOverlay = new MapIdOverlay();

        InternalChildren = new Drawable[]
        {
            visualsHost = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Child = visualsStack = new ScreenStack { RelativeSizeAxes = Axes.Both },
            },
            LeftColumn = new Container
            {
                RelativeSizeAxes = Axes.Y,
                Width = left_column_width,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                // Stops above the bottom bar rather than running full height underneath it — the
                // bar is the one element allowed to span the full width/overlay the visuals.
                Margin = new MarginPadding { Bottom = bottom_bar_height },
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                EdgeEffect = Theme.PanelShadow,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.PanelSurface,
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding(Theme.PanelPadding),
                        Child = listing,
                    },
                },
            },
            RightColumn = new Container
            {
                RelativeSizeAxes = Axes.Y,
                Width = right_column_width,
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Margin = new MarginPadding { Bottom = bottom_bar_height },
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                EdgeEffect = Theme.PanelShadow,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Theme.PanelSurface,
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding(Theme.PanelPadding),
                        Child = new GridContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            RowDimensions = new[]
                            {
                                new Dimension(GridSizeMode.AutoSize),
                                new Dimension(),
                            },
                            Content = new[]
                            {
                                new Drawable[] { createTabHeader() },
                                new Drawable[]
                                {
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Padding = new MarginPadding { Top = Theme.SectionSpacing },
                                        // Both tabs' content stay alive simultaneously — switching
                                        // tabs (selectTab) only toggles Alpha, so every component's
                                        // own state (queue rows, filter/dropdown selections, scroll
                                        // position) just persists untouched while its tab isn't
                                        // showing, and switching back is instant.
                                        Children = new Drawable[] { queuePanel, settingsBody },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            new NowPlayingBar(),
            mapIdOverlay,
            createCornerPill(),
        };

        // Fire-and-forget by design: SetPicked is a synchronous event, and EnqueueAndMaybePlayAsync's
        // own failure paths already surface through jukebox.LastError (see the toast wiring in
        // LoadComplete) rather than through this call's returned Task.
        listing.SetPicked += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
        mapIdOverlay.SetResolved += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        visualsStack.Push(new NowPlayingScreen());

        selectTab(RightPanelTab.Queue);

        uiLayout = config.GetBindable<UiLayout>(JukeBoxSetting.UiLayout);
        uiLayout.BindValueChanged(e => applyLayout(e.NewValue), true);

        jukebox.Start();
        jukebox.LastError.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                showToast(e.NewValue);
        });
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Repeat)
            return base.OnKeyDown(e);

        if (e.Key == Key.Tab)
        {
            toggleFocusMode();
            return true;
        }

        if (e.ControlPressed && e.Key == Key.Q && !e.AltPressed && !e.SuperPressed)
        {
            selectTab(RightPanelTab.Queue);
            return true;
        }

        // Type-anywhere-to-search only makes sense while the left column is actually reachable —
        // in focus mode it (and its search box) is hidden, so let the keypress fall through instead
        // of silently seeding/focusing an invisible box.
        if (uiLayout.Value == UiLayout.ThreeColumn && !e.ControlPressed && !e.AltPressed && !e.SuperPressed)
        {
            char? c = keyToChar(e.Key);

            if (c != null)
            {
                listing.ShowWithInitialChar(c.Value);
                return true;
            }
        }

        return base.OnKeyDown(e);
    }

    private void toggleFocusMode()
        => uiLayout.Value = uiLayout.Value == UiLayout.ThreeColumn ? UiLayout.Focus : UiLayout.ThreeColumn;

    private void applyLayout(UiLayout layout)
    {
        bool focus = layout == UiLayout.Focus;

        LeftColumn.Alpha = focus ? 0 : 1;
        RightColumn.Alpha = focus ? 0 : 1;

        visualsHost.Padding = new MarginPadding
        {
            Left = focus ? 0 : left_column_width,
            Right = focus ? 0 : right_column_width,
        };

        // Defensive: drop keyboard focus before it ends up parked on a search box (or any other
        // input-consuming child) inside a column that just went Alpha 0 / non-present.
        if (focus)
            GetContainingFocusManager()?.ChangeFocus(null);
    }

    private void selectTab(RightPanelTab tab)
    {
        currentTab = tab;

        queuePanel.Alpha = tab == RightPanelTab.Queue ? 1 : 0;
        settingsBody.Alpha = tab == RightPanelTab.Settings ? 1 : 0;

        queueTabButton.Active.Value = tab == RightPanelTab.Queue;
        settingsTabButton.Active.Value = tab == RightPanelTab.Settings;
    }

    private Drawable createTabHeader()
    {
        return new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = tab_header_height,
            ColumnDimensions = new[] { new Dimension(), new Dimension() },
            Content = new[]
            {
                new Drawable[]
                {
                    queueTabButton = new RightPanelTabButton("Queue")
                    {
                        RelativeSizeAxes = Axes.Both,
                        Margin = new MarginPadding { Right = 3 },
                        Action = () => selectTab(RightPanelTab.Queue),
                    },
                    settingsTabButton = new RightPanelTabButton("Settings")
                    {
                        RelativeSizeAxes = Axes.Both,
                        Margin = new MarginPadding { Left = 3 },
                        Action = () => selectTab(RightPanelTab.Settings),
                    },
                },
            },
        };
    }

    private Drawable createCornerPill() => new Container
    {
        Anchor = Anchor.TopRight,
        Origin = Anchor.TopRight,
        Position = new Vector2(-8, 8),
        AutoSizeAxes = Axes.Both,
        Masking = true,
        CornerRadius = Theme.CornerRadius,
        Children = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.PanelSurface,
            },
            new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Padding = new MarginPadding(4),
                Spacing = new Vector2(4, 0),
                Children = new Drawable[]
                {
                    // Settings no longer floats as its own modal — the gear is now a shortcut
                    // straight to the right column's Settings tab.
                    new IconButton
                    {
                        Size = new Vector2(28),
                        Icon = FontAwesome.Solid.Cog,
                        Action = () => selectTab(RightPanelTab.Settings),
                    },
                    new IconButton
                    {
                        Size = new Vector2(28),
                        Icon = FontAwesome.Solid.Hashtag,
                        Action = () => mapIdOverlay.ToggleVisibility(),
                    },
                }
            }
        }
    };

    private static char? keyToChar(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return (char)('a' + (key - Key.A));

        if (key >= Key.Number0 && key <= Key.Number9)
            return (char)('0' + (key - Key.Number0));

        if (key == Key.Space)
            return ' ';

        return null;
    }

    private void showToast(string message)
    {
        var text = new SpriteText
        {
            Anchor = Anchor.TopCentre,
            Origin = Anchor.TopCentre,
            Y = 16,
            Font = FontUsage.Default.With(size: 20),
            Colour = Theme.Error,
            Text = message,
            Alpha = 0,
        };

        AddInternal(text);
        text.FadeIn(200).Delay(4000).FadeOut(500).Expire();
    }

    /// <summary>
    /// One tab button in the right column's Queue/Settings strip — a rounded, flat button that
    /// fills accent-adjacent while <see cref="Active"/> with a thin accent underline, matching the
    /// design system's chip/button language elsewhere (<see cref="FilterChip"/>, <see cref="IconButton"/>)
    /// without pulling in the framework's generic (unthemed) TabControl.
    /// </summary>
    private partial class RightPanelTabButton : ClickableContainer
    {
        public readonly BindableBool Active = new();

        private readonly Box background;
        private readonly Box underline;

        // Transforms (FadeColour/FadeTo) must only run after LoadComplete — see IconButton's
        // `ready` field for the same guard and reasoning.
        private bool ready;

        public RightPanelTabButton(string text)
        {
            Masking = true;
            CornerRadius = Theme.CornerRadius;

            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.ElevatedSurface.Opacity(0.5f),
                },
                new SpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Text = text,
                    Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                    Colour = Theme.TextPrimary,
                },
                underline = new Box
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 2,
                    Colour = Theme.Accent,
                    Alpha = 0,
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Active.BindValueChanged(e => updateActive(e.NewValue), true);
            ready = true;
        }

        private void updateActive(bool active)
        {
            var backgroundColour = active ? Theme.ElevatedSurface : Theme.ElevatedSurface.Opacity(0.5f);

            if (ready)
            {
                background.FadeColour(backgroundColour, Theme.HoverFadeDuration);
                underline.FadeTo(active ? 1 : 0, Theme.HoverFadeDuration);
            }
            else
            {
                background.Colour = backgroundColour;
                underline.Alpha = active ? 1 : 0;
            }
        }
    }
}
