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
/// button toggles the layout.
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
            FullscreenLayoutContainer = new Container { RelativeSizeAxes = Axes.Both },
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

        search.SetPicked += set => jukebox.EnqueueAndMaybePlayAsync(set);
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
