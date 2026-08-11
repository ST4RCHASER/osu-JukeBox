#nullable enable

using JukeBox.Game.Configuration;
using JukeBox.Game.Playback;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace JukeBox.Game.Screens;

/// <summary>
/// Top-level screen: hosts <see cref="NowPlayingScreen"/>'s visuals behind the search overlay,
/// queue panel and now-playing bar, and switches between the two pre-built layout containers
/// (<see cref="FullscreenLayoutContainer"/>/<see cref="SplitLayoutContainer"/>) driven by
/// <see cref="JukeBoxSetting.UiLayout"/>. Typing a printable character (with no modifiers held,
/// and nothing else already consuming the key — e.g. the search box itself when focused) opens
/// the search overlay via <see cref="SearchOverlay.ShowWithInitialChar"/>; Tab or the corner
/// "layout" button toggles the layout. In the fullscreen layout, the queue drawer (permanently
/// docked and always visible in Split) is otherwise unreachable, so Ctrl+Q or the corner "queue"
/// button toggles it there — Ctrl+Q rather than a bare letter so it doesn't collide with the
/// type-anywhere-to-search behaviour above (which explicitly excludes Ctrl/Alt/Super combinations).
/// </summary>
public partial class MainScreen : Screen
{
    private const float split_column_width = 360;
    private const float queue_panel_width = 320;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    private Bindable<UiLayout> uiLayout = null!;

    private Container visualsHost = null!;
    private ScreenStack visualsStack = null!;

    private Container currentChromeParent = null!;

    private SearchOverlay search = null!;
    private QueuePanel queuePanel = null!;

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

        search = new SearchOverlay { RelativeSizeAxes = Axes.Both };
        queuePanel = new QueuePanel();

        InternalChildren = new Drawable[]
        {
            visualsHost = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Child = visualsStack = new ScreenStack { RelativeSizeAxes = Axes.Both },
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
                    Position = new Vector2(-8, 44),
                    Size = new Vector2(96, 28),
                    Text = "queue",
                    Action = () => queuePanel.ToggleVisibility(),
                },
            },
            SplitLayoutContainer = new Container
            {
                RelativeSizeAxes = Axes.Y,
                Width = split_column_width,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
            },
            new NowPlayingBar(),
            new BasicButton
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new Vector2(-8, 8),
                Size = new Vector2(96, 28),
                Text = "layout",
                Action = toggleLayout,
            },
        };

        // The fullscreen layout is the default (UiLayout.FullscreenOverlay) — start both the
        // search overlay and the queue drawer parented there; applyLayout() reparents them into
        // SplitLayoutContainer only if UiLayout is actually Split when it first runs.
        currentChromeParent = FullscreenLayoutContainer;
        currentChromeParent.Add(search);
        currentChromeParent.Add(queuePanel);

        // Fire-and-forget by design: SetPicked is a synchronous event, and EnqueueAndMaybePlayAsync's
        // own failure paths already surface through jukebox.LastError (see the toast wiring in
        // LoadComplete) rather than through this call's returned Task.
        search.SetPicked += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
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
                search.ShowWithInitialChar(c.Value);
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

        var target = split ? SplitLayoutContainer : FullscreenLayoutContainer;

        if (target != currentChromeParent)
        {
            currentChromeParent.Remove(search, false);
            currentChromeParent.Remove(queuePanel, false);
            target.Add(search);
            target.Add(queuePanel);
            currentChromeParent = target;
        }

        // Docked in Split (permanently visible left column, not a dismissable modal): picking a
        // result or pressing Escape must not make it vanish. See SearchOverlay.Docked.
        search.Docked = split;

        if (split)
        {
            // Docked left column: search on top ~70%, queue permanently expanded below it —
            // overriding both drawables' own (floating-overlay-oriented) geometry from outside,
            // same technique NowPlayingBar/QueuePanel already use for their own children.
            search.Anchor = Anchor.TopLeft;
            search.Origin = Anchor.TopLeft;
            search.RelativeSizeAxes = Axes.Both;
            search.Height = 0.7f;
            search.Show();

            queuePanel.Anchor = Anchor.BottomLeft;
            queuePanel.Origin = Anchor.BottomLeft;
            queuePanel.RelativeSizeAxes = Axes.Both;
            queuePanel.Width = 1f;
            queuePanel.Height = 0.3f;
            queuePanel.SetShown(true);
        }
        else
        {
            search.Anchor = Anchor.TopLeft;
            search.Origin = Anchor.TopLeft;
            search.RelativeSizeAxes = Axes.Both;
            search.Height = 1f;

            queuePanel.Anchor = Anchor.TopRight;
            queuePanel.Origin = Anchor.TopRight;
            queuePanel.RelativeSizeAxes = Axes.Y;
            queuePanel.Width = queue_panel_width;
            // Split's branch above sets Height to a 0.3f *relative* fraction (RelativeSizeAxes
            // includes Y there). RelativeSizeAxes switching back to Y-only here does NOT reset
            // that stored Height back to full — without this, a Split -> Fullscreen round trip
            // leaves the drawer permanently stuck at 30% height.
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
            Colour = Color4.OrangeRed,
            Text = message,
            Alpha = 0,
        };

        AddInternal(text);
        text.FadeIn(200).Delay(4000).FadeOut(500).Expire();
    }
}
