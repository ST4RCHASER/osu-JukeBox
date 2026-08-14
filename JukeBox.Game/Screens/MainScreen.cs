#nullable enable

using System;
using JukeBox.Game.Configuration;
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
using osuTK.Input;

namespace JukeBox.Game.Screens;

/// <summary>
/// Top-level screen: a single fixed three-column layout — a permanently-docked left search column,
/// the <see cref="NowPlayingScreen"/> visuals as a BOXED player panel in the centre (same rounded/
/// shadowed card language as the columns, gutters on all sides — not a full-bleed underlay), a
/// permanently-docked right column
/// (tabbed Queue/Settings) and the <see cref="NowPlayingBar"/> along the bottom of the CENTRE
/// strip (inset to the same left/right bounds as the player box — the columns own their full
/// height, the bar never runs underneath them) — driven
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
/// being independently hideable, as a quick "jump to queue" shortcut). There is no separate
/// settings shortcut/corner button any more — the Settings tab header in the right column is
/// always reachable directly (see <see cref="createTabHeader"/>). The map-ID lookup
/// (<see cref="MapIdOverlay"/>) is opened from an icon button docked inline at the right edge of
/// the left column's search box (see <see cref="BeatmapListingOverlay.MapIdRequested"/>) rather
/// than a corner button.
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

    /// <summary>The left column's collapsed width under the fullscreen search style — a slim rail
    /// hosting only the search-opener icon (see <see cref="applySearchStyle"/>).</summary>
    private const float search_rail_width = 64;

    private const float tab_header_height = 36;

    // Mirrors NowPlayingBar's own (private) bar_height constant — kept in sync manually since the
    // columns need to know how much room to leave above the bar without depending on that class's
    // internals.
    private const float bottom_bar_height = 88;

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

    private Bindable<UiLayout> uiLayout = null!;
    private Bindable<double> playfieldZoom = null!;
    private Bindable<bool> detachPlayer = null!;
    private Bindable<bool> detachPlayOnMain = null!;

    private const float tab_slide_offset = 20;

    private Container visualsHost = null!;
    private Container playerBox = null!;
    private Container sceneContainer = null!;
    private FillFlowContainer detachedPlaceholder = null!;
    private ScreenStack visualsStack = null!;
    private NowPlayingBar bottomBar = null!;

    // playerBox's own live pixel DrawSize, cached for any descendant (see BeatmapVisuals'
    // resolved use of this) that needs the box's REAL current aspect rather than the fixed
    // scene_width×scene_height design canvas sceneContainer contains itself within — kept up
    // to date every frame in updateSceneScale (see its own remarks on why that's driven off
    // playerBox.OnUpdate rather than this screen's Update()).
    [Cached]
    private readonly Bindable<Vector2> playerBoxSize = new();

    private BeatmapListingOverlay listing = null!;
    private FullscreenListingOverlay fullscreenListing = null!;
    private QueuePanel queuePanel = null!;
    private SettingsOverlay settingsBody = null!;
    private MapIdOverlay mapIdOverlay = null!;

    /// <summary>
    /// The one search engine both listing presentations render — hosted HERE (not inside the
    /// docked listing, its default owner) so it keeps ticking (debounce, scheduled result
    /// applies) even while the fullscreen style's icon rail hides the docked listing entirely.
    /// </summary>
    private BeatmapSearchEngine searchEngine = null!;

    /// <summary>The left column's normal body — the docked listing (crossfaded away while the
    /// fullscreen style collapses the column to the icon rail).</summary>
    private Container listingBody = null!;

    /// <summary>The left column's fullscreen-style body — only the search-opener icon.</summary>
    private Container searchRail = null!;

    /// <summary>Hosts <see cref="bottomBar"/>, its padding keeping the bar tiled between the
    /// columns — animated alongside the column width on style switches.</summary>
    private Container barHost = null!;

    private Bindable<SearchStyle> searchStyle = null!;

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

    /// <summary>
    /// Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the boxed player panel —
    /// asserts it actually masks its content (so the visuals stack can never render outside its
    /// own bounds, however it tries to overflow internally) without depending on layout internals.
    /// </summary>
    internal Container PlayerBox => playerBox;

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
    /// <see cref="PlayerBox"/> away from the columns/bottom bar — non-zero on every side in
    /// ThreeColumn mode is what makes it a gutter-framed box rather than a full-bleed underlay.
    /// </summary>
    internal MarginPadding VisualsHostPadding => visualsHost.Padding;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the left column's
    /// docked-listing body, to assert the fullscreen style crossfades it away.</summary>
    internal Container ListingBody => listingBody;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the icon rail, to
    /// assert the fullscreen style crossfades it in.</summary>
    internal Container SearchRail => searchRail;

    /// <summary>Test-only: the visuals canvas's current alpha (0 while the player is detached
    /// without <see cref="JukeBoxSetting.DetachPlayOnMain"/>).</summary>
    internal float SceneAlpha => sceneContainer.Alpha;

    /// <summary>Test-only: the "playing in detached window" placeholder's current alpha.</summary>
    internal float PlaceholderAlpha => detachedPlaceholder.Alpha;

    private enum RightPanelTab
    {
        Queue,
        Settings,
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        // The engine is created and hosted here (see the searchEngine field) and shared by both
        // presentations: the docked listing renders it in the left column, and the fullscreen
        // style's big listing — a whole-window modal (see its hosting spot at the top level of
        // InternalChildren below) — renders the same query/filters/results.
        searchEngine = new BeatmapSearchEngine();
        listing = new BeatmapListingOverlay(docked: true, engine: searchEngine) { RelativeSizeAxes = Axes.Both };
        fullscreenListing = new FullscreenListingOverlay(searchEngine) { RelativeSizeAxes = Axes.Both };
        queuePanel = new QueuePanel(docked: true);
        settingsBody = new SettingsOverlay(docked: true) { RelativeSizeAxes = Axes.Both };
        mapIdOverlay = new MapIdOverlay();

        InternalChildren = new Drawable[]
        {
            searchEngine,
            // The app's own background, sitting behind absolutely everything — the columns and
            // bottom bar each paint their own opaque PanelSurface, and playerBox is masked to its
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
                Child = playerBox = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = Theme.CornerRadius,
                    EdgeEffect = Theme.PanelShadow,
                    Children = new Drawable[]
                    {
                        // Black bed: the player's letterbox ground, and the empty-state fill
                        // while nothing is playing yet.
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.Black,
                        },
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
            LeftColumn = new Container
            {
                // Full window height — the bottom bar no longer runs underneath the columns (it
                // sits only in the centre strip between them, see the bar's host below).
                RelativeSizeAxes = Axes.Y,
                Width = left_column_width,
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
                    listingBody = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding(Theme.PanelPadding),
                        Child = listing,
                    },
                    // The fullscreen style's icon rail (see applySearchStyle): the column
                    // collapses to search_rail_width and only this opener remains.
                    searchRail = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Alpha = 0,
                        Child = new SearchRailButton
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                            Margin = new MarginPadding { Top = Theme.PanelPadding },
                            Size = new Vector2(40),
                            Icon = FontAwesome.Solid.Search,
                            Action = () =>
                            {
                                if (uiLayout.Value == UiLayout.ThreeColumn)
                                    fullscreenListing.ShowSearch();
                            },
                        },
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
            // The bar sits only in the CENTRE strip between the (now full-height) columns — the
            // same left/right insets as the player box above it, so the three centre cells (box,
            // gutter, bar) read as one column of cards framed by the side panels. The padded host
            // (rather than margins on the bar itself) is what actually narrows it: the bar is
            // RelativeSizeAxes.X, and relative sizing resolves against the host's padded content
            // area, not its own margins. applyLayout keeps animating the BAR (not the host), so
            // the focus-mode slide/fade behaviour is unchanged (the bar is hidden there anyway).
            barHost = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Left = left_column_width + Theme.SectionSpacing,
                    Right = right_column_width + Theme.SectionSpacing,
                },
                Child = bottomBar = new NowPlayingBar(),
            },
            // Top level, ABOVE the columns and the bottom bar: the fullscreen search style's big
            // listing is a true whole-window modal (dim scrim + centred sliding panel — see
            // FullscreenListingOverlay), not a player-box-area overlay. The map-ID modal stays
            // above it so "#" lookup keeps working from either presentation.
            fullscreenListing,
            mapIdOverlay,
        };

        // Fire-and-forget by design: SetPicked is a synchronous event, and EnqueueAndMaybePlayAsync's
        // own failure paths already surface through jukebox.LastError (see the toast wiring in
        // LoadComplete) rather than through this call's returned Task.
        listing.SetPicked += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
        fullscreenListing.SetPicked += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);
        mapIdOverlay.SetResolved += set => _ = jukebox.EnqueueAndMaybePlayAsync(set);

        // The map-ID button now lives inline in the docked search box (see
        // BeatmapListingOverlay.MapIdRequested) rather than a corner button.
        listing.MapIdRequested += () => mapIdOverlay.ToggleVisibility();

        // "Opening search" under the fullscreen style covers focusing the docked search box too
        // (not just type-anywhere) — the big listing takes over from there.
        listing.SearchBoxFocused += () =>
        {
            if (searchStyle.Value == SearchStyle.Fullscreen && uiLayout.Value == UiLayout.ThreeColumn)
                fullscreenListing.ShowSearch();
        };

        // The left column's dedicated search-opener icon: under the fullscreen style it presents
        // the big listing; under compact it just focuses the docked keyword box (same as clicking
        // the box itself).
        listing.SearchOpenRequested += () =>
        {
            if (searchStyle.Value == SearchStyle.Fullscreen && uiLayout.Value == UiLayout.ThreeColumn)
                fullscreenListing.ShowSearch();
            else
                listing.FocusSearch();
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

        selectTab(RightPanelTab.Queue, animate: false);

        // Both bindables are assigned BEFORE either apply runs: applyLayout's insets read the
        // current style (rail vs full column width), and applySearchStyle's guard reads the
        // current layout — the initial calls land the resting state instantly, then live changes
        // animate.
        uiLayout = config.GetBindable<UiLayout>(JukeBoxSetting.UiLayout);
        searchStyle = config.GetBindable<SearchStyle>(JukeBoxSetting.SearchStyle);

        applySearchStyle(searchStyle.Value, animate: false);
        applyLayout(uiLayout.Value, animate: false);

        uiLayout.BindValueChanged(e => applyLayout(e.NewValue, animate: true));
        searchStyle.BindValueChanged(e => applySearchStyle(e.NewValue, animate: true));

        // No BindValueChanged needed: updateSceneScale already re-reads this every frame (see its
        // own remarks on why it's driven off playerBox.OnUpdate rather than a one-shot), so a
        // config change here is picked up on the very next frame for free.
        playfieldZoom = config.GetBindable<double>(JukeBoxSetting.PlayfieldZoom);

        // Alpha (not unloading) on both sides of the swap — see detachedPlaceholder's remarks.
        // (searchStyle wiring lives above via applySearchStyle — the branch's older duplicate
        // handler was dropped in this merge resolution.)
        detachPlayer = config.GetBindable<bool>(JukeBoxSetting.DetachPlayer);
        detachPlayOnMain = config.GetBindable<bool>(JukeBoxSetting.DetachPlayOnMain);
        detachPlayer.BindValueChanged(_ => updatePlayerBoxPresentation());
        detachPlayOnMain.BindValueChanged(_ => updatePlayerBoxPresentation());
        updatePlayerBoxPresentation();

        jukebox.Start();
        jukebox.LastError.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                showToast(e.NewValue);
        });
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

    private void updateSceneScale()
    {
        playerBoxSize.Value = playerBox.DrawSize;

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
                // The style setting picks which presentation "opening search" means — both views
                // share one search engine, so the seeded query lands in both either way.
                if (searchStyle.Value == SearchStyle.Fullscreen)
                    fullscreenListing.ShowWithInitialChar(c.Value);
                else
                    listing.ShowWithInitialChar(c.Value);
                return true;
            }
        }

        return base.OnKeyDown(e);
    }

    private void toggleFocusMode()
        => uiLayout.Value = uiLayout.Value == UiLayout.ThreeColumn ? UiLayout.Focus : UiLayout.ThreeColumn;

    /// <summary>
    /// One orchestrated transition, reversed for either direction: both side columns slide out
    /// past their own edge (+ fade), the bottom bar slides down off-screen (+ fade) and the
    /// player box simultaneously expands to full-bleed (padding + rounding both animate away) —
    /// so focus mode reads as pure fullscreen visuals with nothing left overlaying them, not just
    /// the side columns disappearing. Restoring plays every part of that back in reverse.
    /// </summary>
    /// <summary>The left column's width under the CURRENT search style: the slim icon rail while
    /// the fullscreen style is active, the full listing column otherwise. Every left inset (player
    /// box, bar host, focus-mode slide distance) derives from this rather than the raw constant.</summary>
    private float currentLeftColumnWidth => searchStyle.Value == SearchStyle.Fullscreen ? search_rail_width : left_column_width;

    /// <summary>The centre cell's insets in ThreeColumn mode — shared by <see cref="applyLayout"/>
    /// and <see cref="applySearchStyle"/> so both transitions target the same geometry.</summary>
    private MarginPadding threeColumnPadding() => new MarginPadding
    {
        Left = currentLeftColumnWidth + Theme.SectionSpacing,
        Right = right_column_width + Theme.SectionSpacing,
        Top = Theme.SectionSpacing,
        Bottom = bottom_bar_height + Theme.SectionSpacing,
    };

    private MarginPadding barHostPadding() => new MarginPadding
    {
        Left = currentLeftColumnWidth + Theme.SectionSpacing,
        Right = right_column_width + Theme.SectionSpacing,
    };

    private void applyLayout(UiLayout layout, bool animate)
    {
        bool focus = layout == UiLayout.Focus;

        // User preference: sidebar open/close should feel immediate — fast linear slide,
        // not the slower eased glide used elsewhere in the app.
        double duration = animate ? Theme.DurationFast : 0;
        const Easing easing = Easing.None;

        LeftColumn.MoveToX(focus ? -(currentLeftColumnWidth + Theme.SectionSpacing) : 0, duration, easing);
        LeftColumn.FadeTo(focus ? 0 : 1, duration, easing);

        RightColumn.MoveToX(focus ? right_column_width + Theme.SectionSpacing : 0, duration, easing);
        RightColumn.FadeTo(focus ? 0 : 1, duration, easing);

        // Anchored BottomLeft: a positive Y pushes it straight down past the bottom edge.
        bottomBar.MoveToY(focus ? bottom_bar_height + Theme.SectionSpacing : 0, duration, easing);
        bottomBar.FadeTo(focus ? 0 : 1, duration, easing);

        // ThreeColumn: the player sits in its own box between the columns and above the bottom
        // bar, with a visible gutter on every side that leaves room for the bar without ever
        // being covered by it. Focus: the box dissolves to full-bleed (padding + rounding
        // animate to zero; the bottom bar is sliding away in parallel above, not just sitting
        // over the top of it).
        var targetPadding = focus ? new MarginPadding() : threeColumnPadding();

        visualsHost.TransformTo(nameof(visualsHost.Padding), targetPadding, duration, easing);
        playerBox.TransformTo(nameof(playerBox.CornerRadius), focus ? 0f : Theme.CornerRadius, duration, easing);

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
    /// Applies the search style's left-column presentation: the fullscreen style collapses the
    /// column to the slim icon rail (crossfading the docked listing out and the search-opener
    /// icon in) and slides the player box's/bar's left insets along with it; compact restores the
    /// full listing column. One orchestrated animation on live switches (Theme timing), instant
    /// for the initial call at load.
    /// </summary>
    private void applySearchStyle(SearchStyle style, bool animate)
    {
        bool fullscreen = style == SearchStyle.Fullscreen;

        double duration = animate ? Theme.DurationNormal : 0;
        var easing = fullscreen ? Theme.EaseExit : Theme.EaseEnter;

        // Flipping to Compact retires the big listing immediately (search goes back to living
        // entirely in the left column); flipping to Fullscreen simply arms it for the next open.
        if (!fullscreen)
            fullscreenListing.Hide();

        LeftColumn.ResizeWidthTo(currentLeftColumnWidth, duration, Theme.EaseEnter);
        listingBody.FadeTo(fullscreen ? 0 : 1, duration, easing);
        searchRail.FadeTo(fullscreen ? 1 : 0, duration, easing);

        // The centre cells (player box + bar) follow the column's new width so the tiling holds
        // through the transition. Focus mode's zero padding must not be disturbed — its own
        // restore recomputes from the then-current style (applyLayout → threeColumnPadding).
        if (uiLayout.Value == UiLayout.ThreeColumn)
        {
            visualsHost.TransformTo(nameof(visualsHost.Padding), threeColumnPadding(), duration, Theme.EaseEnter);
            barHost.TransformTo(nameof(barHost.Padding), barHostPadding(), duration, Theme.EaseEnter);
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
        // Queue) shouldn't restart the crossfade — but the one-time initial call must still apply
        // regardless, since both bodies start at their construction-time default Alpha (1).
        if (animate && tab == currentTab)
            return;

        currentTab = tab;

        double duration = animate ? Theme.DurationNormal : 0;

        showTabBody(queuePanel, tab == RightPanelTab.Queue, duration);
        showTabBody(settingsBody, tab == RightPanelTab.Settings, duration);

        queueTabButton.Active.Value = tab == RightPanelTab.Queue;
        settingsTabButton.Active.Value = tab == RightPanelTab.Settings;
    }

    /// <summary>
    /// Crossfades <paramref name="body"/> to <paramref name="active"/>; the incoming body also
    /// slides in ~20px from the right so a tab switch reads as one directional motion rather than
    /// a flat alpha cut.
    /// </summary>
    private static void showTabBody(Drawable body, bool active, double duration)
    {
        if (active)
        {
            if (duration > 0)
                body.X = tab_slide_offset;

            body.FadeIn(duration, Theme.EaseEnter);
            body.MoveToX(0, duration, Theme.EaseEnter);
        }
        else
        {
            body.FadeOut(duration, Theme.EaseExit);
        }
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
            Scale = new Vector2(Theme.PopScale),
        };

        AddInternal(text);

        // Two parallel sequences (fade, scale) rather than one chain — both need to pop in
        // together, sit for the same 4s dwell, then pop back out together before expiring.
        text.FadeIn(Theme.DurationNormal, Theme.EaseEnter).Delay(4000).FadeOut(Theme.DurationSlow, Theme.EaseExit).Expire();
        text.ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter).Delay(4000).ScaleTo(Theme.PopScale, Theme.DurationSlow, Theme.EaseExit);
    }

    /// <summary>The icon rail's search-opener — a dedicated type (rather than a bare
    /// <see cref="IconButton"/>) so tests can tell it apart from the docked listing's own
    /// search-opener icon, which stays in the tree (hidden) under the fullscreen style.</summary>
    private partial class SearchRailButton : IconButton
    {
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
