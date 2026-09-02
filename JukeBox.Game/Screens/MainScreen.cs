#nullable enable

using System;
using JukeBox.Game.Configuration;
using JukeBox.Game.Import;
using JukeBox.Game.Online;
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
using osu.Framework.Logging;
using osu.Framework.Screens;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace JukeBox.Game.Screens;

/// <summary>
/// Top-level screen: a single fixed three-column layout — a permanently-docked left search column,
/// the <see cref="NowPlayingScreen"/> visuals as a BOXED player panel in the centre (same rounded/
/// shadowed card language as the columns, gutters on all sides — not a full-bleed underlay) and a
/// permanently-docked right column (tabbed Playback/Chart/Settings) — driven
/// by <see cref="JukeBoxSetting.UiLayout"/>. Replaces the old Fullscreen/Split layout-toggle pair;
/// see <see cref="UiLayout"/> for the config migration story.
///
/// <para>
/// There is no bottom bar: the playback strip that used to span the window's bottom edge is gone,
/// its content folded into the right column's Playback tab (see <see cref="PlaybackPanel"/>). The
/// player box and both columns therefore run all the way to the window's bottom edge.
/// </para>
///
/// <para>
/// The left column embeds <see cref="BeatmapListingOverlay"/> in its <c>docked</c> mode (permanently
/// visible — no more show/hide overlay semantics; see that class), showing the shared search
/// engine's current results with no search box or filters of its own. Search lives in exactly one
/// place: <see cref="FullscreenListingOverlay"/>, opened either by the sidebar's search button
/// (<see cref="BeatmapListingOverlay.SearchOpenRequested"/>) or by typing a printable character
/// with no modifiers held, which seeds it via
/// <see cref="FullscreenListingOverlay.ShowWithInitialChar"/>. The right column embeds <see cref="PlaybackPanel"/>,
/// <see cref="ChartPanel"/> and <see cref="SettingsOverlay"/> (also docked) side by side behind three
/// tab buttons — all stay permanently loaded and alive, so switching tabs is a simple Alpha toggle
/// (instant, and every component's own state — scroll position, filter selections, checkbox values —
/// just sits there untouched while its tab isn't the active one).
/// </para>
///
/// <para>
/// Tab is repurposed from "toggle layout" to "focus mode": it hides both side columns (letting the
/// visuals go full-bleed) and pressing it again restores the three-column
/// layout. Ctrl+Q switches the right column to its Playback tab (kept, despite the drawer no longer
/// being independently hideable, as a quick "jump to the queue/transport" shortcut). There is no separate
/// settings shortcut/corner button any more — the Settings tab header in the right column is
/// always reachable directly (see <see cref="createTabHeader"/>). The map-ID lookup
/// (<see cref="MapIdOverlay"/>) is opened from the "#" button beside the sidebar's search button
/// (see <see cref="BeatmapListingOverlay.MapIdRequested"/>) rather than a corner button.
/// </para>
///
/// <para>
/// The boxed player never crops the scene: <see cref="visualsStack"/> renders into a fixed
/// <see cref="scene_width"/>×<see cref="scene_height"/> design canvas (<see cref="sceneContainer"/>)
/// that is uniformly scaled to CONTAIN within <see cref="playerBox"/> (aspect-preserving, letterboxed
/// on whichever axis has slack) at all times, in both layouts — recomputed every frame (see
/// <see cref="updateSceneScale"/>) from the box's own current <c>DrawWidth</c>/<c>DrawHeight</c> so
/// it tracks continuously through
/// the focus-mode transition and window resizes, never just the box's post-transition rest state.
/// A single uniform-min formula for both layouts (rather than a mode-dependent branch) is what makes
/// that tracking continuous: in settled focus mode the box is normally wider than the canvas anyway,
/// so the min already resolves to the same height-driven scale a dedicated "focus" branch would have
/// picked — but a boolean branch flips the instant <see cref="UiLayout"/> changes, before the box has
/// moved at all, so during the transition it would use the box's still-mostly-unchanged height while
/// ignoring its still-narrow, barely-animated width — scaling the content to nearly its final size on
/// the very first frame while the box (and its mask) were still small, an overflow only hidden by
/// <see cref="playerBox"/>'s masking, but visible the instant the mask caught up. The uniform-min
/// formula has no such discontinuity: it already accounts for both axes every frame, so it can only
/// ever scale the content up to what the box's *current* size actually permits.
/// </para>
/// </summary>
public partial class MainScreen : Screen
{
    private const float left_column_width = 380;
    private const float right_column_width = 340;

    private const float tab_header_height = 36;

    // The design canvas the visuals render into before being scaled to fit the box. 854 covers
    // lazer's own widescreen storyboard width (16:9 at height 480 ≈ 853.3, rounded up); the lazer
    // storyboard renderer centres itself within its parent's width regardless of whether the
    // current beatmap is a plain 4:3 storyboard or a widescreen one, so a single generic canvas
    // works for every beatmap without inspecting the current track.
    private const float scene_width = 854;
    private const float scene_height = 480;

    [Resolved]
    private Jukebox jukebox { get; set; } = null!;

    [Resolved]
    private JukeBoxConfigManager config { get; set; } = null!;

    [Resolved]
    private DroppedFileImporter fileImporter { get; set; } = null!;

    [Resolved]
    private LaunchArgumentImporter launchArguments { get; set; } = null!;

    /// <summary>
    /// A bound COPY of <see cref="Jukebox.LastError"/> rather than a subscription straight onto
    /// the jukebox's own bindable. The jukebox long outlives any one screen, so a direct
    /// subscription keeps a dead screen's <see cref="showToast"/> callback alive on it — and that
    /// callback reaches into the screen's own drawable tree (<see cref="toastOverlay"/>), mutating
    /// it while the async disposal queue is walking it on another thread. A bindable FIELD is
    /// unbound automatically when this drawable is disposed, so the callback can't outlive the screen.
    /// </summary>
    private readonly Bindable<string?> lastError = new Bindable<string?>();

    /// <summary>Drag-and-drop import outcomes, bound the same way (and for the same lifetime
    /// reasons) as <see cref="lastError"/>.</summary>
    private readonly Bindable<DropNotification?> dropNotification = new Bindable<DropNotification?>();

    /// <summary>Command-line argument outcomes, bound the same way (and for the same lifetime
    /// reasons) as <see cref="lastError"/>.</summary>
    private readonly Bindable<DropNotification?> argumentNotification = new Bindable<DropNotification?>();

    /// <summary>Why the official search backend last fell back to a mirror, bound the same way (and
    /// for the same lifetime reasons) as <see cref="lastError"/>.</summary>
    private readonly Bindable<string?> searchError = new Bindable<string?>();

    /// <summary>The current beatmap's video being unplayable, bound the same way as
    /// <see cref="lastError"/> — see <see cref="videoNotifier"/>.</summary>
    private readonly Bindable<string?> videoNotice = new Bindable<string?>();

    private Bindable<UiLayout> uiLayout = null!;
    private Bindable<double> playfieldZoom = null!;
    private Bindable<bool> detachPlayer = null!;
    private Bindable<bool> detachPlayOnMain = null!;
    private Bindable<bool> removeChartMask = null!;
    private Bindable<bool> removeStoryboardMask = null!;

    private const float tab_slide_offset = 20;

    /// <summary>
    /// The toast strip's depth. Lower is nearer the viewer in osu!framework, and every other child
    /// of this screen leaves its depth at 0 — so a single negative value states "always in front"
    /// outright, rather than leaving it to depend on the order of a list somebody may reorder.
    /// </summary>
    private const float toast_depth = -1;

    /// <summary>See showTabBody: the departing body needs to lose its opacity fast, which is ease-OUT
    /// rather than <see cref="Theme.EaseExit"/>'s ease-IN.</summary>
    private const Easing tab_exit_easing = Easing.OutQuint;

    private Container visualsHost = null!;
    private Container playerBox = null!;
    private Container boxFrame = null!;

    // The two columns' own painted surfaces, held so the "everything released" look can make them
    // slightly see-through without touching a single control on top of them (see
    // updateReleasedChrome).
    private Box leftColumnSurface = null!;
    private Box rightColumnSurface = null!;
    private ToastOverlay toastOverlay = null!;
    private Container sceneContainer = null!;
    private FillFlowContainer detachedPlaceholder = null!;
    private ScreenStack visualsStack = null!;

    // playerBox's own live pixel DrawSize, cached for any descendant (see BeatmapVisuals'
    // resolved use of this) that needs the box's REAL current aspect rather than the fixed
    // scene_width×scene_height design canvas sceneContainer contains itself within — kept up
    // to date every frame in updateSceneScale (see its own remarks on why that's driven off
    // playerBox.OnUpdate rather than this screen's Update()).
    [Cached]
    private readonly Bindable<Vector2> playerBoxSize = new();

    /// <summary>
    /// The box's live corner radius, cached alongside <see cref="playerBoxSize"/> and for the same
    /// consumer: while a "Remove ... mask" setting has <see cref="playerBox"/> not masking, the
    /// per-layer clips inside <see cref="BeatmapVisuals"/> stand in for it, and they have to round
    /// their corners the same way the box would have — including mid-animation, since the radius
    /// tweens to zero on the way into focus mode (see <see cref="applyLayout"/>).
    /// </summary>
    [Cached(name: player_box_corner_radius)]
    private readonly Bindable<float> playerBoxCornerRadius = new(Theme.CornerRadius);

    /// <summary>DI name for <see cref="playerBoxCornerRadius"/> — a bare <c>Bindable&lt;float&gt;</c>
    /// is far too generic a type to cache unnamed.</summary>
    internal const string player_box_corner_radius = "player box corner radius";

    /// <summary>
    /// Cached HERE rather than app-wide on purpose: only the master window has a
    /// <see cref="MainScreen"/>, so the detached viewer's own visual stack resolves nothing and
    /// stays silent, and the notice appears in the window the user is interacting with. See
    /// <see cref="VideoNotifier"/>.
    /// </summary>
    [Cached]
    private readonly VideoNotifier videoNotifier = new VideoNotifier();

    private BeatmapListingOverlay listing = null!;
    private FullscreenListingOverlay fullscreenListing = null!;
    private PlaybackPanel playbackPanel = null!;
    private ChartPanel chartPanel = null!;
    private SettingsOverlay settingsBody = null!;
    private MapIdOverlay mapIdOverlay = null!;
    private FileImportOverlay fileImportOverlay = null!;

    /// <summary>
    /// The one search engine both listing presentations render — hosted HERE (not inside the
    /// docked listing, its default owner) so it keeps ticking (debounce, scheduled result applies)
    /// regardless of which of the two views happens to be on screen, and so its results survive
    /// the fullscreen listing closing.
    /// </summary>
    private BeatmapSearchEngine searchEngine = null!;

    /// <summary>Hosts all three tab bodies at once. Held onto so <see cref="showTabBody"/> can put
    /// the incoming one in FRONT — see its own remarks.</summary>
    private Container tabBodies = null!;

    private RightPanelTabButton playbackTabButton = null!;
    private RightPanelTabButton chartTabButton = null!;
    private RightPanelTabButton settingsTabButton = null!;

    private RightPanelTab currentTab = RightPanelTab.Playback;

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

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the boxed player panel —
    /// asserts it actually masks its content (so the visuals stack can never render outside its
    /// own bounds, however it tries to overflow internally) without depending on layout internals.
    /// </summary>
    internal Container PlayerBox => playerBox;

    /// <summary>
    /// Test-only access to the card frame behind <see cref="PlayerBox"/> — the rounded, shadowed
    /// black bed that keeps the box looking like a card even while the box itself has stopped
    /// masking for a "Remove ... mask" setting.
    /// </summary>
    internal Container BoxFrame => boxFrame;

    /// <summary>Test hooks: the painted surface inside each side column — the only part of a column
    /// the "everything released" look touches (see <see cref="updateReleasedChrome"/>).</summary>
    internal Box LeftColumnSurface => leftColumnSurface;

    internal Box RightColumnSurface => rightColumnSurface;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the toast stack, to assert
    /// where its toasts land relative to the player area and the side columns.
    /// </summary>
    internal ToastOverlay Toasts => toastOverlay;

    /// <summary>Test hook: the notifier the per-song visual stack reports unplayable videos to.</summary>
    internal VideoNotifier VideoNotifier => videoNotifier;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the visuals stack, to
    /// assert it's parented inside <see cref="PlayerBox"/> (the masked box) rather than some
    /// other, unmasked part of the hierarchy.
    /// </summary>
    internal ScreenStack VisualsStack => visualsStack;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to <see cref="sceneContainer"/>'s
    /// current scale — the auto-fit "contain in playerBox" factor (<see cref="updateSceneScale"/>)
    /// multiplied by the live <see cref="JukeBoxSetting.PlayfieldZoom"/> factor.
    /// </summary>
    internal Vector2 SceneScale => sceneContainer.Scale;

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the padding that insets
    /// <see cref="PlayerBox"/> away from the columns and the window edges — non-zero on every side
    /// in ThreeColumn mode is what makes it a gutter-framed box rather than a full-bleed underlay.
    /// </summary>
    internal MarginPadding VisualsHostPadding => visualsHost.Padding;

    /// <summary>Test-only: the visuals canvas's current alpha (0 while the player is detached
    /// without <see cref="JukeBoxSetting.DetachPlayOnMain"/>).</summary>
    internal float SceneAlpha => sceneContainer.Alpha;

    /// <summary>Test-only: the "playing in detached window" placeholder's current alpha.</summary>
    internal float PlaceholderAlpha => detachedPlaceholder.Alpha;

    /// <summary>
    /// Declaration order IS the on-screen order of the tab strip (see <see cref="createTabHeader"/>):
    /// Playback | Chart | Settings.
    /// </summary>
    private enum RightPanelTab
    {
        Playback,
        Chart,
        Settings,
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        // The engine is created and hosted here (see the searchEngine field) and shared by both
        // presentations: the docked sidebar renders its results in the left column, and the
        // fullscreen listing — a whole-window modal (see its hosting spot at the top level of
        // InternalChildren below) — is where the same query/filters are actually driven.
        searchEngine = new BeatmapSearchEngine();
        listing = new BeatmapListingOverlay(docked: true, engine: searchEngine) { RelativeSizeAxes = Axes.Both };
        fullscreenListing = new FullscreenListingOverlay(searchEngine) { RelativeSizeAxes = Axes.Both };
        playbackPanel = new PlaybackPanel();
        chartPanel = new ChartPanel();
        // The engine goes in so the Radio section's filter rows follow the same per-backend
        // capability signal the listing's own rows do (see SettingsOverlay's constructor).
        settingsBody = new SettingsOverlay(docked: true, searchEngine: searchEngine) { RelativeSizeAxes = Axes.Both };
        mapIdOverlay = new MapIdOverlay();
        fileImportOverlay = new FileImportOverlay();

        InternalChildren = new Drawable[]
        {
            searchEngine,
            // The app's own background, sitting behind absolutely everything — the columns each
            // paint their own opaque PanelSurface, and playerBox is masked to its
            // own gutter-inset bounds (see below), so this is only ever actually visible in the
            // thin SectionSpacing gutters between them. Without it those gutters fell through to
            // the raw GL clear colour instead of the intended Theme background.
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.Background,
            },
            // The player is a BOXED panel (same card language as the columns: rounded, shadowed,
            // masked), not a full-bleed underlay the panels float over. visualsHost's padding
            // carves out the centre cell (applyLayout); the box inside it holds the actual player.
            visualsHost = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    // The card the player sits on, and BEHIND the player rather than around it:
                    // rounded, shadowed, and holding the black bed (the player's letterbox ground,
                    // and the empty-state fill while nothing is playing yet). It is a sibling of
                    // playerBox, not its parent or its child, for two reasons that pull in opposite
                    // directions — an edge effect needs its own container to be masking (the
                    // framework refuses the combination outright), while a masking playerBox would
                    // clip a child's outward shadow away entirely. As a sibling drawn first it
                    // always masks itself, always casts its shadow, and never depends on whether
                    // playerBox is clipping this frame.
                    boxFrame = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        CornerRadius = Theme.CornerRadius,
                        EdgeEffect = Theme.PanelShadow,
                        Child = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.Black,
                        },
                    },
                    playerBox = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        // The content clip, and the one thing here that is ever switched off: the
                        // two "Remove ... mask" settings release the scene past the box's edges by
                        // turning this off (see updatePlayerBoxMasking), after which
                        // BeatmapVisuals' own per-layer clips — sized to exactly this box — keep
                        // clipping whatever the user did NOT release.
                        Masking = true,
                        CornerRadius = Theme.CornerRadius,
                        Children = new Drawable[]
                        {
                            // Fixed design-size canvas, scaled uniformly to fit playerBox (see
                            // updateSceneScale) instead of visualsStack stretching RelativeSizeAxes
                            // straight to the box — that's what let the scene overflow (and get
                            // masked/cropped) whenever the box got narrower than the design aspect.
                            sceneContainer = new Container
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Size = new Vector2(scene_width, scene_height),
                                Child = visualsStack = new ScreenStack { RelativeSizeAxes = Axes.Both },
                            },
                            // Shown instead of the scene while DetachPlayer has the visuals living
                            // in their own window (see Detach.DetachedViewerManager). The scene
                            // itself stays loaded underneath at Alpha 0 — draw cost is skipped, but
                            // re-attaching is instant with no reload gap.
                            detachedPlaceholder = new FillFlowContainer
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 12),
                                Alpha = 0,
                                Children = new Drawable[]
                                {
                                    new SpriteIcon
                                    {
                                        Anchor = Anchor.TopCentre,
                                        Origin = Anchor.TopCentre,
                                        Icon = FontAwesome.Solid.ExternalLinkAlt,
                                        Size = new Vector2(28),
                                        Colour = Colour4.White.Opacity(0.4f),
                                    },
                                    new SpriteText
                                    {
                                        Anchor = Anchor.TopCentre,
                                        Origin = Anchor.TopCentre,
                                        Text = "Playing in detached window",
                                        Colour = Colour4.White.Opacity(0.6f),
                                    },
                                },
                            },
                        },
                    },
                },
            },
            LeftColumn = new Container
            {
                // Full window height, edge to edge — nothing sits underneath the columns.
                RelativeSizeAxes = Axes.Y,
                Width = left_column_width,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                EdgeEffect = Theme.PanelShadow,
                Children = new Drawable[]
                {
                    leftColumnSurface = new Box
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
                // Full window height, same as LeftColumn.
                RelativeSizeAxes = Axes.Y,
                Width = right_column_width,
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Masking = true,
                CornerRadius = Theme.CornerRadius,
                EdgeEffect = Theme.PanelShadow,
                Children = new Drawable[]
                {
                    rightColumnSurface = new Box
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
                                    tabBodies = new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Padding = new MarginPadding { Top = Theme.SectionSpacing },
                                        // All three tabs' content stay alive simultaneously —
                                        // switching tabs (selectTab) only toggles Alpha, so every
                                        // component's own state (queue rows, filter/dropdown
                                        // selections, scroll position) just persists untouched while
                                        // its tab isn't showing, and switching back is instant.
                                        Children = new Drawable[] { playbackPanel, chartPanel, settingsBody },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            // Top level, ABOVE the columns: the big listing is a true whole-window modal (dim
            // scrim + centred sliding panel — see FullscreenListingOverlay), not a
            // player-box-area overlay. The map-ID modal stays above it so "#" lookup keeps
            // working with the listing open.
            fullscreenListing,
            mapIdOverlay,
            fileImportOverlay,
            // Toasts are the LAST word in the draw order, and deliberately at the screen's top
            // level rather than inside playerBox (user request). Inside the box they anchored to
            // the PLAYER's bottom-right, which moves with the sidebars, focus mode and
            // PlayfieldZoom, and they were painted under the fullscreen listing — a notification
            // hidden by the thing you are using tells you nothing. At this level they anchor to
            // the WINDOW, and the negative Depth puts them in front of every overlay including the
            // two modals: a transient message in the corner never obstructs a modal's own controls,
            // and being hidden behind one is the failure this is fixing.
            toastOverlay = new ToastOverlay { Depth = toast_depth },
        };

        // Fire-and-forget by design: SetPicked is a synchronous event, and EnqueueAndMaybePlayAsync's
        // own failure paths already surface through jukebox.LastError (see the toast wiring in
        // LoadComplete) rather than through this call's returned Task.
        listing.SetPicked += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
        fullscreenListing.SetPicked += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
        mapIdOverlay.SetResolved += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);

        // The map-ID button sits beside the sidebar's search button (see
        // BeatmapListingOverlay.MapIdRequested) rather than in a corner.
        listing.MapIdRequested += () => mapIdOverlay.ToggleVisibility();

        // The folder button opens the in-app picker, and whatever it yields goes through the
        // importer the window's drag-and-drop handler already uses — so a picked file and a
        // dropped one are the same import, with the same toasts (bound in LoadComplete) and the
        // same failure reporting. Fire-and-forget like the drop path: Import reports its own
        // outcomes through fileImporter.Notification rather than through the returned Task.
        listing.FileImportRequested += () => fileImportOverlay.ToggleVisibility();
        fileImportOverlay.FileSelected += path => _ = fileImporter.Import(path);

        // The sidebar's search button is the mouse-driven way into the one search surface. Gated
        // on the layout for the same reason type-anywhere is: focus mode has no sidebar on screen
        // to have clicked, and nothing should be able to pop a modal over the full-bleed visuals.
        listing.SearchOpenRequested += () =>
        {
            if (uiLayout.Value == UiLayout.ThreeColumn)
                fullscreenListing.ShowSearch();
        };

        // See updateSceneScale's own doc comment for why this is hung off playerBox's OnUpdate
        // rather than this screen's Update() — it's what keeps the scale reading playerBox's
        // current-frame (not previous-frame) size during the focus-mode transition.
        playerBox.OnUpdate += _ => updateSceneScale();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        visualsStack.Push(new NowPlayingScreen());

        selectTab(RightPanelTab.Playback, animate: false);

        uiLayout = config.GetBindable<UiLayout>(JukeBoxSetting.UiLayout);

        // The initial call lands the resting state instantly; live changes animate.
        applyLayout(uiLayout.Value, animate: false);

        uiLayout.BindValueChanged(e => applyLayout(e.NewValue, animate: true));

        // No BindValueChanged needed: updateSceneScale already re-reads this every frame (see its
        // own remarks on why it's driven off playerBox.OnUpdate rather than a one-shot), so a
        // config change here is picked up on the very next frame for free.
        playfieldZoom = config.GetBindable<double>(JukeBoxSetting.PlayfieldZoom);

        // Alpha (not unloading) on both sides of the swap — see detachedPlaceholder's remarks.
        detachPlayer = config.GetBindable<bool>(JukeBoxSetting.DetachPlayer);
        detachPlayOnMain = config.GetBindable<bool>(JukeBoxSetting.DetachPlayOnMain);
        detachPlayer.BindValueChanged(_ => updatePlayerBoxPresentation());
        detachPlayOnMain.BindValueChanged(_ => updatePlayerBoxPresentation());
        updatePlayerBoxPresentation();

        removeChartMask = config.GetBindable<bool>(JukeBoxSetting.RemoveChartMask);
        removeStoryboardMask = config.GetBindable<bool>(JukeBoxSetting.RemoveStoryboardMask);
        removeChartMask.BindValueChanged(_ => releasesChanged());
        removeStoryboardMask.BindValueChanged(_ => releasesChanged());
        updatePlayerBoxMasking();
        // The resting state lands instantly; only live toggles animate (same rule as applyLayout).
        updateReleasedChrome(animate: false);

        jukebox.Start();

        lastError.BindTo(jukebox.LastError);
        lastError.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                showToast(e.NewValue);
        });

        jukebox.Enqueued += onEnqueued;

        // The official search backend falling back to a mirror is silent in the results themselves
        // (there are still cards) — the listing's own status line says why, but a user who picked
        // that backend in settings and typed a query is looking at the cards, not the status line.
        searchError.BindTo(searchEngine.LastError);
        searchError.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                showToast(e.NewValue);
        });

        // A beatmap whose video can't play falls back to the background (see BeatmapVisuals), which
        // on its own is indistinguishable from a map that simply has no video — so say it once.
        videoNotice.BindTo(videoNotifier.Notice);
        videoNotice.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                showToast(e.NewValue);
        });

        dropNotification.BindTo(fileImporter.Notification);
        dropNotification.BindValueChanged(e =>
        {
            if (e.NewValue is { } drop)
                showToast(drop.Message, drop.IsError ? Theme.Error : Theme.Accent);
        });

        // Command-line arguments report the same way drops do — a bad argument is named in its own
        // toast and the rest of the batch carries on. Its own bindable rather than the importer's
        // so an argument that never reached a file import isn't reported as a failed file import.
        argumentNotification.BindTo(launchArguments.Notification);
        argumentNotification.BindValueChanged(e =>
        {
            if (e.NewValue is { } outcome)
                showToast(outcome.Message, outcome.IsError ? Theme.Error : Theme.Accent);
        });
    }

    private void onEnqueued(BeatmapSetInfo set) => showToast($"Added to queue: {set.DisplayTitle}", Theme.Accent);

    /// <summary>
    /// <see cref="Jukebox.Enqueued"/> is a plain event on an object that outlives any one screen,
    /// so unlike a bindable field (see <see cref="lastError"/>) nothing detaches it automatically
    /// — and a dead screen's handler still reaching <see cref="showToast"/> means pushing into a
    /// drawable tree the async disposal queue is already walking.
    /// </summary>
    protected override void Dispose(bool isDisposing)
    {
        // Resolved dependencies are still null if this screen never finished loading.
        if (jukebox != null)
            jukebox.Enqueued -= onEnqueued;

        base.Dispose(isDisposing);
    }

    /// <summary>
    /// Keeps <see cref="sceneContainer"/>'s scale matched to the current box size every frame — the
    /// box itself is continuously resizing during the focus-mode transition (padding/corner-radius
    /// animate in <see cref="applyLayout"/>), so a one-shot computation on layout change would lag
    /// a frame behind. Uniform-min "contain" scale in both layouts (see the class summary for why a
    /// mode-dependent branch here previously caused the content to jump to near its final size on
    /// the very first frame of entering focus mode), so the whole design canvas always stays inside
    /// the box's *current* size (letterboxed on whichever axis has slack) — never cropped, never
    /// distorted, whether settled or mid-transition — MULTIPLIED by the live
    /// <see cref="JukeBoxSetting.PlayfieldZoom"/> factor (1.0 = no-op, matching the pre-zoom
    /// behaviour exactly). Since <see cref="sceneContainer"/> hosts <see cref="visualsStack"/> (and
    /// therefore the entire per-beatmap visuals — background, storyboard/video, chart — all
    /// together, see <see cref="BeatmapVisuals"/>), this single multiply zooms the whole stack as
    /// one unit; <see cref="playerBox"/>'s own <c>Masking</c> (set once, unconditionally true, at
    /// construction) is what clips it at the box's edges whenever the zoomed-up scene overflows —
    /// nothing here needs to change that.
    ///
    /// <para>
    /// Driven off <see cref="playerBox"/>'s own <see cref="Drawable.OnUpdate"/> rather than this
    /// screen's own <c>Update()</c>: <see cref="playerBox"/> is a descendant of this screen (via
    /// <see cref="visualsHost"/>), and a parent's own <c>Update()</c> runs before its children's
    /// transforms are applied for that frame — reading <c>playerBox.DrawWidth</c> from this
    /// screen's <c>Update()</c> would therefore always be exactly one frame stale relative to
    /// <see cref="visualsHost"/>'s animating <c>Padding</c>. By the time <see cref="playerBox"/>'s
    /// own <c>OnUpdate</c> fires, its geometry for the current frame is already fully resolved, so
    /// the scale computed here never lags behind what's actually on screen that frame.
    /// </para>
    /// </summary>
    // The placeholder only replaces the scene when the player is detached AND the user hasn't
    // asked to keep the main window rendering too (JukeBoxSetting.DetachPlayOnMain). Logged
    // because "why is/isn't my main window playing" is exactly the kind of state a bug report
    // needs pinned down.
    private void updatePlayerBoxPresentation()
    {
        bool placeholderShown = detachPlayer.Value && !detachPlayOnMain.Value;

        sceneContainer.Alpha = placeholderShown ? 0 : 1;
        detachedPlaceholder.Alpha = placeholderShown ? 1 : 0;

        Logger.Log($"Player box presentation: detach={detachPlayer.Value} playOnMain={detachPlayOnMain.Value} → {(placeholderShown ? "placeholder" : "scene")}");
    }

    /// <summary>
    /// Releasing EITHER layer means <see cref="playerBox"/> stops clipping its content — a child
    /// can never escape an ancestor's mask, so there is nowhere else the release can happen. The
    /// scene doesn't simply spill wholesale as a result: <see cref="BeatmapVisuals"/> gives every
    /// layer its own clip sized to this exact box (see its <c>updateLayerClips</c>), and those take
    /// over for everything the user did not release. The released layer still draws behind the side
    /// columns and every overlay, all of which are later children of this screen than
    /// <see cref="visualsHost"/>.
    ///
    /// <para>
    /// Logged for the same reason the detach presentation is: "why is my storyboard spilling over
    /// the gutter" wants the answer pinned down in a report.
    /// </para>
    /// </summary>
    /// <summary>Either release changing moves two things: what the box clips, and how the chrome
    /// around it looks.</summary>
    private void releasesChanged()
    {
        updatePlayerBoxMasking();
        updateReleasedChrome(animate: true);
    }

    private void updatePlayerBoxMasking()
    {
        bool released = removeChartMask.Value || removeStoryboardMask.Value;

        playerBox.Masking = !released;

        Logger.Log($"Player box masking: removeChartMask={removeChartMask.Value} removeStoryboardMask={removeStoryboardMask.Value} → box {(released ? "releases its content (per-layer clips take over)" : "clips its content")}");
    }

    /// <summary>
    /// How opaque a column's surface is while everything is released. Chosen by eye in the real
    /// window against a bright storyboard: <see cref="Theme.PanelSurface"/> is itself 92% opaque, so
    /// this lands at roughly two thirds coverage — enough that the spilled scene reads as continuing
    /// behind the columns, not so little that the panel text loses its ground. The text and controls
    /// are untouched: only the surface underneath them fades.
    /// </summary>
    internal const float released_surface_alpha = 0.85f;

    /// <summary>
    /// The "everything is released" look: with BOTH mask settings on, the player's card frame
    /// (rounded corners, drop shadow and the black letterbox bed it paints) would be a rectangle
    /// drawn ON TOP of content that is deliberately spilling past it, so it goes away entirely, and
    /// the side columns turn slightly see-through so that content reads as continuing behind them.
    ///
    /// <para>
    /// Deliberately BOTH releases rather than either: with one layer still clipped to the box, the
    /// box is still the frame the user is looking at, and removing its card would leave that layer
    /// cropped against nothing. This is also why the card is not tied to
    /// <see cref="playerBox"/>'s masking, which follows either release on its own.
    /// </para>
    ///
    /// <para>
    /// Independent of focus mode: that fades the whole columns (and animates the card's radius)
    /// without touching either of these, so the two compose — entering focus mode with everything
    /// released takes columns that are already faded the rest of the way out.
    /// </para>
    /// </summary>
    private void updateReleasedChrome(bool animate)
    {
        bool allReleased = removeChartMask.Value && removeStoryboardMask.Value;
        double duration = animate ? Theme.DurationNormal : 0;

        boxFrame.FadeTo(allReleased ? 0 : 1, duration, Theme.EaseEnter);
        leftColumnSurface.FadeTo(allReleased ? released_surface_alpha : 1, duration, Theme.EaseEnter);
        rightColumnSurface.FadeTo(allReleased ? released_surface_alpha : 1, duration, Theme.EaseEnter);
    }

    private void updateSceneScale()
    {
        playerBoxSize.Value = playerBox.DrawSize;
        playerBoxCornerRadius.Value = playerBox.CornerRadius;

        if (playerBox.DrawWidth <= 0 || playerBox.DrawHeight <= 0)
            return;

        float scale = Math.Min(playerBox.DrawWidth / scene_width, playerBox.DrawHeight / scene_height);

        // playfieldZoom is only assigned once LoadComplete runs (after config resolves) — playerBox's
        // OnUpdate is wired up earlier, in load() — so guard the narrow window before that assignment
        // rather than assume it's always bound by the time this first fires.
        float zoom = (float)(playfieldZoom?.Value ?? 1.0);

        sceneContainer.Scale = new Vector2(scale * zoom);
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
            selectTab(RightPanelTab.Playback);
            return true;
        }

        // Type-anywhere-to-search is deliberately inert in focus mode: that mode is pure
        // full-bleed visuals, and a keypress must not pop a modal listing over them.
        if (uiLayout.Value == UiLayout.ThreeColumn && !e.ControlPressed && !e.AltPressed && !e.SuperPressed)
        {
            char? c = keyToChar(e.Key);

            if (c != null)
            {
                // One search surface, one entry point. The sidebar shows the same engine's
                // results, so it follows along without being involved here.
                fullscreenListing.ShowWithInitialChar(c.Value);
                return true;
            }
        }

        return base.OnKeyDown(e);
    }

    private void toggleFocusMode()
        => uiLayout.Value = uiLayout.Value == UiLayout.ThreeColumn ? UiLayout.Focus : UiLayout.ThreeColumn;

    /// <summary>
    /// One orchestrated transition, reversed for either direction: both side columns slide out
    /// past their own edge (+ fade) and the player box simultaneously expands to full-bleed
    /// (padding + rounding both animate away) — so focus mode reads as pure fullscreen visuals
    /// with nothing left overlaying them, not just the side columns disappearing. Restoring plays
    /// every part of that back in reverse.
    /// </summary>
    /// <summary>The centre cell's insets in ThreeColumn mode.</summary>
    private static MarginPadding threeColumnPadding() => new MarginPadding
    {
        Left = left_column_width + Theme.SectionSpacing,
        Right = right_column_width + Theme.SectionSpacing,
        Top = Theme.SectionSpacing,
        Bottom = Theme.SectionSpacing,
    };

    private void applyLayout(UiLayout layout, bool animate)
    {
        bool focus = layout == UiLayout.Focus;

        // User preference: sidebar open/close should feel immediate — fast linear slide,
        // not the slower eased glide used elsewhere in the app.
        double duration = animate ? Theme.DurationFast : 0;
        const Easing easing = Easing.None;

        LeftColumn.MoveToX(focus ? -(left_column_width + Theme.SectionSpacing) : 0, duration, easing);
        LeftColumn.FadeTo(focus ? 0 : 1, duration, easing);

        RightColumn.MoveToX(focus ? right_column_width + Theme.SectionSpacing : 0, duration, easing);
        RightColumn.FadeTo(focus ? 0 : 1, duration, easing);

        // ThreeColumn: the player sits in its own box between the columns, with a visible gutter
        // on every side. Focus: the box dissolves to full-bleed (padding + rounding animate to
        // zero) while the columns slide away in parallel.
        var targetPadding = focus ? new MarginPadding() : threeColumnPadding();

        visualsHost.TransformTo(nameof(visualsHost.Padding), targetPadding, duration, easing);

        // Both, in lockstep: playerBox rounds the CONTENT (whenever it is masking at all) and
        // boxFrame rounds the card itself. A radius left behind on either would show as a square
        // corner over a rounded one for the length of the transition.
        float targetRadius = focus ? 0f : Theme.CornerRadius;
        playerBox.TransformTo(nameof(playerBox.CornerRadius), targetRadius, duration, easing);
        boxFrame.TransformTo(nameof(boxFrame.CornerRadius), targetRadius, duration, easing);

        // Defensive: drop keyboard focus before it ends up parked on a search box (or any other
        // input-consuming child) inside a column that just went Alpha 0 / non-present. The big
        // listing also closes — focus mode is pure fullscreen visuals with nothing overlaying.
        if (focus)
        {
            fullscreenListing.Hide();
            GetContainingFocusManager()?.ChangeFocus(null);
        }
    }

    /// <summary>
    /// Switches the right column's active tab body. <paramref name="animate"/> is false only for
    /// the one-time initial call at <see cref="LoadComplete"/> (both bodies must land in their
    /// correct state instantly, not mid-crossfade, before the first frame renders).
    /// </summary>
    private void selectTab(RightPanelTab tab, bool animate = true)
    {
        // No-op re-selection (e.g. clicking an already-active tab, or Ctrl+Q while already on
        // Playback) shouldn't restart the crossfade — but the one-time initial call must still apply
        // regardless, since both bodies start at their construction-time default Alpha (1).
        if (animate && tab == currentTab)
            return;

        currentTab = tab;

        // A true crossfade rather than out-then-in: the two motions overlap, so the column is never
        // momentarily empty. The exit is the FASTER of the two (DurationFast against the entry's
        // DurationNormal) so the outgoing body is gone well before the incoming one settles, instead
        // of the two sitting on top of each other for the whole transition.
        double inDuration = animate ? Theme.DurationNormal : 0;
        double outDuration = animate ? Theme.DurationFast : 0;

        showTabBody(playbackPanel, tab == RightPanelTab.Playback, inDuration, outDuration);
        showTabBody(chartPanel, tab == RightPanelTab.Chart, inDuration, outDuration);
        showTabBody(settingsBody, tab == RightPanelTab.Settings, inDuration, outDuration);

        playbackTabButton.Active.Value = tab == RightPanelTab.Playback;
        chartTabButton.Active.Value = tab == RightPanelTab.Chart;
        settingsTabButton.Active.Value = tab == RightPanelTab.Settings;
    }

    /// <summary>
    /// Crossfades <paramref name="body"/> to <paramref name="active"/> as ONE panel replacing
    /// another: the incoming slides in from the right (+<see cref="tab_slide_offset"/> → 0) while
    /// the outgoing leaves to the left (0 → -<see cref="tab_slide_offset"/>) and fades. Both move
    /// the same way, so the transition reads as the content travelling leftward rather than a new
    /// panel arriving on top of one that never left.
    ///
    /// <para>
    /// The incoming body is also brought to the FRONT. All three bodies live in one container, and
    /// without this they are drawn in declaration order — so switching to a tab declared earlier
    /// (Settings → Playback) put the arriving body UNDERNEATH the departing one, which is what made
    /// the two look stacked mid-transition however they were faded. Depth 0 for the incoming and 1
    /// for the rest is enough: exactly one body is ever incoming.
    /// </para>
    /// </summary>
    private void showTabBody(Drawable body, bool active, double inDuration, double outDuration)
    {
        tabBodies.ChangeChildDepth(body, active ? 0 : 1);

        if (active)
        {
            if (inDuration > 0)
                body.X = tab_slide_offset;

            body.FadeIn(inDuration, Theme.EaseEnter);
            body.MoveToX(0, inDuration, Theme.EaseEnter);
        }
        else
        {
            // OutQuint, NOT Theme.EaseExit. EaseExit is InQuint, which is right for something that
            // should linger and then accelerate away (a popover scaling out) and exactly wrong for
            // the departing half of a crossfade: quintic ease-IN barely moves at the start, so 60ms
            // into a 150ms fade the outgoing body was still ~99% opaque. Measured in the real window
            // — that, more than the draw order, is what made the old tab look like it was still
            // sitting there. Quintic ease-OUT drops it almost immediately and lets it tail off.
            body.FadeOut(outDuration, tab_exit_easing);
            body.MoveToX(-tab_slide_offset, outDuration, tab_exit_easing);
        }
    }

    private Drawable createTabHeader()
    {
        return new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = tab_header_height,
            // Five columns, not three: the buttons take the three flexible ones and the gaps between
            // them are real absolute-width columns. Spacing a fill-width row this way is lazer's own
            // idiom (see its RotationPresetButtons and MultiplayerMatchFooter), and here it is the
            // only thing that works — Margin cannot do it. Margin does not shrink a drawable with
            // RelativeSizeAxes; the child still fills its whole cell, and only Left/Top shift it. So
            // the 3px margins this used to carry rendered as a 3px gap on Chart's left, NO gap on its
            // right, and a Settings button hanging 3px past the column's padding — three different
            // spacings where the code read as symmetric.
            //
            // The gap is RowSpacing, the spacing between sibling controls (the transport strip uses
            // the same). It is deliberately narrower than the outer inset, which is not set here at
            // all: that comes from the right column's own PanelPadding, wrapping the strip and every
            // tab body below it alike, so the buttons stay flush with the content they switch
            // between. Carving an extra inset here would break that alignment.
            ColumnDimensions = new[]
            {
                new Dimension(),
                new Dimension(GridSizeMode.Absolute, Theme.RowSpacing),
                new Dimension(),
                new Dimension(GridSizeMode.Absolute, Theme.RowSpacing),
                new Dimension(),
            },
            Content = new[]
            {
                new Drawable?[]
                {
                    playbackTabButton = new RightPanelTabButton("Playback")
                    {
                        RelativeSizeAxes = Axes.Both,
                        Action = () => selectTab(RightPanelTab.Playback),
                    },
                    null,
                    chartTabButton = new RightPanelTabButton("Chart")
                    {
                        RelativeSizeAxes = Axes.Both,
                        Action = () => selectTab(RightPanelTab.Chart),
                    },
                    null,
                    settingsTabButton = new RightPanelTabButton("Settings")
                    {
                        RelativeSizeAxes = Axes.Both,
                        Action = () => selectTab(RightPanelTab.Settings),
                    },
                },
            },
        };
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

    /// <summary>
    /// Hands a message to the bottom-right toast stack (<see cref="ToastOverlay"/>), which owns the
    /// surface, the stacking and the animation. <paramref name="colour"/> defaults to
    /// <see cref="Theme.Error"/> so failures stay unmistakably red; informational toasts (the
    /// enqueue notification) pass the accent instead.
    /// </summary>
    private void showToast(string message, Color4? colour = null) => toastOverlay.Push(message, colour);

    /// <summary>
    /// One tab button in the right column's Playback/Settings strip — a rounded, flat button that
    /// fills accent-adjacent while <see cref="Active"/> with a thin accent underline, matching the
    /// design system's button language elsewhere (<see cref="TextButton"/>, <see cref="IconButton"/>)
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
