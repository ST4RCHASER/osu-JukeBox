#nullable enable

using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.Screens;

/// <summary>
/// Top-level screen: hosts <see cref="NowPlayingScreen"/>'s visuals behind the beatmap listing,
/// queue panel and now-playing bar, and switches between the two pre-built layout containers
/// (<see cref="FullscreenLayoutContainer"/>/<see cref="SplitLayoutContainer"/>) driven by
/// <see cref="JukeBoxSetting.UiLayout"/>. The osu!-web-style <see cref="BeatmapListingOverlay"/>
/// replaces the old dropdown search in BOTH layouts: it lives inside <c>visualsHost</c>, so it
/// covers exactly the visuals area (in Split, the left panel stays uncovered). Typing a printable
/// character (with no modifiers held, and nothing else already consuming the key — e.g. the
/// listing's own keyword box when focused) opens the listing via
/// <see cref="BeatmapListingOverlay.ShowWithInitialChar"/>; in Split, the left panel keeps a
/// <see cref="CompactSearchButton"/> that opens it too, with the queue drawer permanently docked
/// below. Tab or the corner "layout" button toggles the layout. In the fullscreen layout the
/// queue drawer is otherwise unreachable, so Ctrl+Q or the corner "queue" button toggles it there
/// — Ctrl+Q rather than a bare letter so it doesn't collide with the type-anywhere-to-search
/// behaviour above (which explicitly excludes Ctrl/Alt/Super combinations). The top-right corner
/// hosts a small translucent pill of icon buttons: gear (opens <see cref="SettingsOverlay"/>),
/// "#" (opens <see cref="MapIdOverlay"/>, for queueing a set directly by beatmapset ID instead of
/// searching) and the layout toggle.
/// </summary>
public partial class MainScreen : Screen
{
    private const float split_column_width = 340;
    private const float queue_panel_width = 320;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    private Bindable<UiLayout> uiLayout = null!;

    private Container visualsHost = null!;
    private ScreenStack visualsStack = null!;

    private Container currentQueueParent = null!;

    private BeatmapListingOverlay listing = null!;
    private Container splitQueueHost = null!;
    private QueuePanel queuePanel = null!;
    private SettingsOverlay settingsOverlay = null!;
    private MapIdOverlay mapIdOverlay = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the fullscreen-overlay
    /// layout container, to assert its Alpha without depending on layout internals.
    /// </summary>
    internal Container FullscreenLayoutContainer { get; private set; } = null!;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the split layout
    /// container, to assert its Alpha without depending on layout internals.
    /// </summary>
    internal Container SplitLayoutContainer { get; private set; } = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        listing = new BeatmapListingOverlay();
        queuePanel = new QueuePanel();
        settingsOverlay = new SettingsOverlay();
        mapIdOverlay = new MapIdOverlay();

        InternalChildren = new Drawable[]
        {
            visualsHost = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    visualsStack = new ScreenStack { RelativeSizeAxes = Axes.Both },
                    // Inside visualsHost (whose Padding tracks the layout) so the listing covers
                    // exactly the visuals area in both layouts, never the Split left panel.
                    listing,
                },
            },
            FullscreenLayoutContainer = new Container
            {
                RelativeSizeAxes = Axes.Both,
                // Lives directly in FullscreenLayoutContainer (not reparented on layout switch
                // like search/queuePanel) so it's automatically hidden and non-interactive
                // whenever Split is active, via this container's own Alpha toggle in applyLayout.
                Child = new BasicButton
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Position = new Vector2(-8, 52),
                    Size = new Vector2(96, 28),
                    Text = "queue",
                    BackgroundColour = Theme.ElevatedSurface,
                    Action = () => queuePanel.ToggleVisibility(),
                },
            },
            // Split's left column: a single rounded, shadowed panel-surface housing the compact
            // search button (which opens the listing overlay) on top and the permanently-docked
            // queue drawer below it — one continuous "left panel" rather than two separate boxes.
            SplitLayoutContainer = new Container
            {
                RelativeSizeAxes = Axes.Y,
                Width = split_column_width,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
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
                        Children = new Drawable[]
                        {
                            new CompactSearchButton
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 40,
                                Action = () => listing.ShowAndFocus(),
                            },
                            splitQueueHost = new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Top = 40 + Theme.SectionSpacing },
                            },
                        },
                    },
                },
            },
            new NowPlayingBar(),
            settingsOverlay,
            mapIdOverlay,
            new Container
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
                            new IconButton
                            {
                                Size = new Vector2(28),
                                Icon = FontAwesome.Solid.Cog,
                                Action = () => settingsOverlay.ToggleVisibility(),
                            },
                            new IconButton
                            {
                                Size = new Vector2(28),
                                Icon = FontAwesome.Solid.Hashtag,
                                Action = () => mapIdOverlay.ToggleVisibility(),
                            },
                            new IconButton
                            {
                                Size = new Vector2(28),
                                Icon = FontAwesome.Solid.Th,
                                Action = toggleLayout,
                            },
                        }
                    }
                }
            },
        };

        // The fullscreen layout is the default (UiLayout.FullscreenOverlay) — start the queue
        // drawer parented there; applyLayout() reparents it into the Split panel's queue host
        // only if UiLayout is actually Split when it first runs. (The listing overlay lives in
        // visualsHost permanently — it never reparents.)
        currentQueueParent = FullscreenLayoutContainer;
        currentQueueParent.Add(queuePanel);

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
            toggleLayout();
            return true;
        }

        if (e.ControlPressed && e.Key == Key.Q && !e.AltPressed && !e.SuperPressed)
        {
            // Only meaningful in the fullscreen layout — Split keeps the drawer permanently
            // docked/expanded via applyLayout's own SetShown(true), and toggling it here as well
            // would fight that (and slide it using panel_width, which doesn't match its relative
            // Split geometry).
            if (uiLayout.Value == UiLayout.FullscreenOverlay)
                queuePanel.ToggleVisibility();
            return true;
        }

        if (!e.ControlPressed && !e.AltPressed && !e.SuperPressed)
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

    private void toggleLayout()
        => uiLayout.Value = uiLayout.Value == UiLayout.FullscreenOverlay ? UiLayout.Split : UiLayout.FullscreenOverlay;

    private void applyLayout(UiLayout layout)
    {
        bool split = layout == UiLayout.Split;

        FullscreenLayoutContainer.Alpha = split ? 0 : 1;
        SplitLayoutContainer.Alpha = split ? 1 : 0;

        visualsHost.Padding = new MarginPadding { Left = split ? split_column_width : 0 };

        var target = split ? splitQueueHost : FullscreenLayoutContainer;

        if (target != currentQueueParent)
        {
            currentQueueParent.Remove(queuePanel, false);
            target.Add(queuePanel);
            currentQueueParent = target;
        }

        if (split)
        {
            // Docked left column: the queue fills the host below the compact search button —
            // overriding the drawable's own (floating-drawer-oriented) geometry from outside,
            // same technique NowPlayingBar/QueuePanel already use for their own children.
            queuePanel.Anchor = Anchor.TopLeft;
            queuePanel.Origin = Anchor.TopLeft;
            queuePanel.RelativeSizeAxes = Axes.Both;
            queuePanel.Width = 1f;
            queuePanel.Height = 1f;
            queuePanel.SetShown(true);
        }
        else
        {
            queuePanel.Anchor = Anchor.TopRight;
            queuePanel.Origin = Anchor.TopRight;
            // Split's branch above switches RelativeSizeAxes to Both with relative Width/Height.
            // Switching back to Y-only does NOT reset the stored Width — restore the absolute
            // drawer width (and full relative height) explicitly, or a Split -> Fullscreen round
            // trip leaves the drawer 1px wide.
            queuePanel.RelativeSizeAxes = Axes.Y;
            queuePanel.Width = queue_panel_width;
            queuePanel.Height = 1f;
            queuePanel.SetShown(false);
        }
    }

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
}
