#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Everyone's replay over ONE chart, in the shape danser-go calls Knockout: the beatmap is
/// rendered once, every player's cursor is drawn on top of it in their own colour, and a live
/// scoreboard down the side re-orders itself as they overtake each other.
///
/// <para>
/// The board is the point rather than a caption on it. Its numbers come from each player's
/// recorded <see cref="Replays.ReplayTimeline"/> read at the current playback time — see
/// <see cref="ReplaySimulator"/> — so a row shows what that player HAD at this moment in the song,
/// not what they finished with. That is also what makes it survive seeking, which a running total
/// cannot.
/// </para>
///
/// <para>
/// Elimination is off by default (<see cref="Replays.KnockoutMode.Showcase"/>): every play runs to
/// the end and the board just sorts. Turning it on is what makes it a contest — players are out on
/// their first combo break, sink to the bottom of the board greyed out, and the survivors' cursors
/// grow as the field thins.
/// </para>
///
/// <para>
/// The cursors are lazer's own <c>ReplayAnalysisOverlay</c>, mounted inside the single hosted
/// ruleset's <c>PlayfieldAdjustmentContainer</c> — see
/// <see cref="LazerChartLayer.AddCursorOverlay"/>. That is what makes this affordable AND correct:
/// affordable because a cursor is a handful of drawables where a whole extra
/// <c>DrawableRuleset</c> is a beatmap conversion and a hit-object pool, and correct because
/// living inside the playfield's own container means the cursors inherit its transform instead of
/// this class re-deriving it and drifting the moment the zoom or aspect changes.
/// </para>
///
/// <para>
/// osu! only, unavoidably — the other three rulesets have no cursor to overlay. A non-osu! beatmap
/// falls back to the plain single chart, which is why <see cref="CursorsAttached"/> exists.
/// </para>
/// </summary>
public partial class MultiReplayCombine : CompositeDrawable
{
    private readonly string osuFile;
    private readonly IReadOnlyList<ReplayAttachment> replays;

    private LazerChartLayer chart = null!;
    private ReplaySimulator simulator = null!;
    private KnockoutBoard board = null!;
    private bool attached;

    /// <summary>
    /// The cursors, indexed by replay — one per player, the chart's driver included. A slot is null
    /// only when that replay carried no frames to draw.
    /// </summary>
    private readonly List<PlayerCursor?> cursors = new List<PlayerCursor?>();

    /// <summary>Test hook: cursors actually mounted, one per replay that had frames.</summary>
    internal int CursorsAttached { get; private set; }

    /// <summary>Test hook: the mounted cursors, for asserting they are distinctly coloured.</summary>
    internal IReadOnlyList<PlayerCursor?> Cursors => cursors;

    /// <summary>Test hook: the one hosted chart every cursor is drawn over.</summary>
    internal LazerChartLayer Chart => chart;

    /// <summary>Test hook: the live scoreboard.</summary>
    internal KnockoutBoard Board => board;

    /// <summary>Test hook: the off-screen playthrough feeding the board.</summary>
    internal ReplaySimulator Simulator => simulator;

    /// <summary>
    /// The knockout rules in force. Assigning reaches the board immediately, so a user changing the
    /// mode mid-song sees it apply rather than having to reload the map — the plays are already
    /// recorded, so who is out is a re-reading of data that is all there.
    /// </summary>
    public KnockoutRules Rules
    {
        get => rules;
        set
        {
            rules = value;

            if (board != null)
                board.Rules = value;
        }
    }

    private KnockoutRules rules = new KnockoutRules();

    /// <summary>Whether the chart's hit sounds play. One chart, so no flam to avoid here.</summary>
    public bool HitSoundsEnabled
    {
        set => chart.HitSoundsEnabled.Value = value;
    }

    /// <param name="osuFile">The one difficulty every replay was played on.</param>
    /// <param name="replays">The replays, in drop order.</param>
    public MultiReplayCombine(string osuFile, IReadOnlyList<ReplayAttachment> replays)
    {
        this.osuFile = osuFile;
        this.replays = replays;

        RelativeSizeAxes = Axes.Both;
    }

    /// <summary>Per-player overrides — the cursor colours are read from here, and the driving
    /// player's mod override reaches the rendered chart. Null in a bare test host.</summary>
    [Resolved(canBeNull: true)]
    private Replays.PlayerOverrideStore? overrideStore { get; set; }

    /// <summary>Where the preload progress is published for the Players panel's buffer bar to read.
    /// Null in a bare test host.</summary>
    [Resolved(canBeNull: true)]
    private Replays.PreloadProgressTracker? preloadTracker { get; set; }

    /// <summary>The small "Simulating N%" indicator, low in the bottom-right corner so it says the
    /// numbers are still settling without sitting over the rail or the play.</summary>
    private OsuSpriteText progressNote = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        // The FIRST replay drives the rendered chart — its hits are the ones the map reacts to —
        // and everyone else rides along as a cursor. Someone has to be the one the chart follows.
        var driver = replays.FirstOrDefault();

        chart = new LazerChartLayer(new FlatWorkingBeatmap(osuFile), osuFile, driver?.Score)
        {
            AlwaysPresent = true,

            // Under the driving player's OWN recorded mods, to match how their score is computed.
            // Letting the shared Chart-tab selection edit this chart would put the rendered play
            // and the numbers beside it under different mods.
            UseRecordedReplayModsOnly = true,

            // Their per-player mod override, if any, so the rendered chart matches the re-scored
            // numbers on their rail row.
            OverrideMods = driver != null ? overrideStore?.Peek(driver)?.Mods : null,
            OverrideSkinKey = driver != null ? overrideStore?.Peek(driver)?.SkinKey : null,

            // No per-hit judgement popups on the combine playfield — it should read as cursors only.
            // The recent 100/50/miss is shown on the rail's judgement column instead.
            AlwaysHiddenElements = new[] { PlayfieldElement.Judgements },
        };

        simulator = new ReplaySimulator(osuFile, replays);

        InternalChildren = new Drawable[]
        {
            chart,
            simulator,

            // Unobtrusive, bottom-right: a small caption that the plays are still being recorded, so a
            // fresh load reads as "working" rather than as a rail full of zeros. Hidden the moment the
            // preload is done. The full buffered picture is the buffer bar over in the Playback tab.
            progressNote = new OsuSpriteText
            {
                Anchor = Anchor.BottomRight,
                Origin = Anchor.BottomRight,
                Margin = new MarginPadding { Right = 8, Bottom = 8 },
                Colour = Color4.White.Opacity(0.7f),
                Font = OsuFont.Torus.With(size: 12, weight: FontWeight.SemiBold),
                Shadow = true,
                Alpha = 0,
            },
        };
    }

    /// <summary>
    /// Builds the board once the simulator has its timelines, which it creates when IT loads.
    /// Deliberately not done in this class's LoadComplete: a parent's LoadComplete is not
    /// guaranteed to run after its children's, and reading the timelines a frame too early is an
    /// index out of range rather than something that degrades quietly.
    /// </summary>
    private void buildBoardWhenReady()
    {
        if (board != null || simulator.Timelines.Count < replays.Count)
            return;

        var entrants = replays
                       .Select((replay, index) => new KnockoutBoard.Entrant(
                           // The bare name, with the mods handed over SEPARATELY: the board draws
                           // the name in the player's colour and the mods in white, so the two
                           // cannot be one string.
                           replay.PlayerName.Length > 0 ? replay.PlayerName : "unknown",
                           effectiveColour(index),
                           simulator.Timelines[index],
                           railMods(replay),
                           replay.Score?.Replay?.Frames))
                       .ToList();

        AddInternal(board = new KnockoutBoard(entrants)
        {
            Anchor = Anchor.TopLeft,
            Origin = Anchor.TopLeft,
            Margin = new MarginPadding(6),
            Rules = rules,

            // Hovering a rail row focuses that player's cursor. The value is only stashed here; the
            // fade is issued from updateCursors so it runs once per change instead of every frame,
            // and so a hover that arrives before the cursors are mounted still takes effect once
            // they are.
            PlayerFocused = index => focusedPlayer = index,
        });
    }

    /// <summary>The player whose rail row is currently hovered, or null when none is. A focused
    /// player's cursor stays at full strength while every other fades to a whisper.</summary>
    private int? focusedPlayer;

    /// <summary>The focus last pushed to the cursors, so the fade is issued only when it changes.
    /// Never written while the cursors are unattached, so a hover during load is applied once they
    /// exist rather than being swallowed.</summary>
    private int? appliedFocus;

    protected override void Update()
    {
        base.Update();

        buildBoardWhenReady();
        attachCursors();
        updateCursors();

        // The preload progress: a small bottom-right caption here, and the buffered-bar in the
        // Playback tab through the shared tracker. Both say the same thing — the plays are still
        // being recorded and the numbers are not final — until every timeline is complete.
        double progress = simulator.Progress;
        bool loading = progress < 0.999;

        progressNote.Alpha = loading ? 1 : 0;
        if (loading)
            progressNote.Text = $"Simulating replays… {(int)(progress * 100)}%";

        preloadTracker?.Report(progress);

        if (board != null)
        {
            // The rail looks up its grade textures in the SAME skin the chart renders under, so a
            // grade shows the active skin's own "ranking-X-small" graphic (small, not the giant badge)
            // and matches the play on screen. Null until the chart's skin is built.
            board.GradeSkin = chart.GradeSkin;
        }
    }

    /// <summary>
    /// Mounts a cursor for EVERY player onto the one chart. The ruleset only exists once the chart
    /// has finished its own async load, so this cannot happen in load(). Done once, then never again.
    ///
    /// <para>
    /// Every player including the one driving the chart, which is the change from what shipped. The
    /// driver used to rely on the playfield's own cursor, which is white and cannot be tinted, so
    /// even once the others worked one player would still have been the odd one out. The playfield
    /// cursor is switched off and all N are drawn the same way.
    /// </para>
    /// </summary>
    private void attachCursors()
    {
        if (attached || chart.DrawableRuleset == null)
            return;

        attached = true;
        chart.HidePlayfieldCursor();

        for (int i = 0; i < replays.Count; i++)
        {
            var replay = replays[i].Score?.Replay;

            if (replay == null)
            {
                cursors.Add(null);
                continue;
            }

            // The bare player name, NOT the "+mods" display name — the break/death cue on the
            // playfield shows who, not what they played, and "name +CL" reads as clutter.
            string cursorName = replays[i].PlayerName.Length > 0 ? replays[i].PlayerName : "unknown";
            var cursor = new PlayerCursor(cursorName, replay.Frames, effectiveColour(i));

            cursors.Add(chart.AddPlayerCursor(cursor) ? cursor : null);

            if (cursors[i] != null)
                CursorsAttached++;
        }
    }

    /// <summary>
    /// Grows the surviving cursors as the field thins and fades out the eliminated. In showcase
    /// mode nobody is ever eliminated, so this settles at the smallest size and stops mattering —
    /// which is the intent, since a showcase is about seeing all of the plays at once.
    /// </summary>
    private void updateCursors()
    {
        if (!attached || board == null)
            return;

        double time = Clock.CurrentTime;
        var timelines = simulator.Timelines;

        if (timelines.Count == 0)
            return;

        int alive = rules.AliveCount(timelines, time);
        float scale = KnockoutRules.CursorScale(alive, timelines.Count);

        for (int i = 0; i < cursors.Count && i < timelines.Count; i++)
        {
            if (cursors[i] is not { } cursor)
                continue;

            // The field-aware check, so the survivor floor is honoured here as well as on the board.
            bool stillIn = rules.AliveAt(timelines, i, time);

            cursor.Scale = new Vector2(stillIn ? scale : 1);
            cursor.Alpha = stillIn ? 1 : 0;
        }

        // No red combo-break flash in combine — that red cursor-dot-and-name cue was the wrong one
        // here (a sea of red when a hard section drops many players at once). Combine's ONE dropped
        // name is the knockout death name below, in the player's colour at the missed note.
        updateDeaths(time);
        applyFocus();
    }

    /// <summary>The death name currently shown for each eliminated player, keyed by index — created
    /// while the playhead is inside their death window and removed outside it.</summary>
    private readonly Dictionary<int, PlayerDeathName> deaths = new Dictionary<int, PlayerDeathName>();

    /// <summary>
    /// The knockout death animation, driven straight from the timeline so it is SEEK-CORRECT: for
    /// each ELIMINATED player (floor-aware — a player spared by the survivor floor never dies), while
    /// the playhead is within the death window after their knockout, a falling name is shown at the
    /// spot their cursor was AT the knockout, with the combo they broke on under it (combo-break
    /// mode) — name only in imperfection mode. Its fall and fade are recomputed from the age each
    /// frame, so seeking into the window lands it mid-fall and seeking past it removes it entirely.
    /// </summary>
    private void updateDeaths(double time)
    {
        if (rules.Mode == KnockoutMode.Showcase)
        {
            clearDeaths();
            return;
        }

        var timelines = simulator.Timelines;
        bool showCombo = rules.Mode == KnockoutMode.ComboBreak;

        for (int i = 0; i < cursors.Count && i < timelines.Count; i++)
        {
            double? knockout = rules.KnockedOutAt(timelines[i]);

            // Never broke, or spared by the survivor floor (still alive now) — no death.
            if (knockout is not { } ko || rules.AliveAt(timelines, i, time))
            {
                removeDeath(i);
                continue;
            }

            double elapsed = time - ko;

            if (elapsed < 0 || elapsed >= PlayerDeathName.Duration)
            {
                removeDeath(i);
                continue;
            }

            if (!deaths.TryGetValue(i, out var death))
            {
                if (cursors[i] is not { } cursor)
                    continue;

                var breakPoint = timelines[i].At(ko);

                // At the NOTE they broke on, not at their cursor — the object that killed them is the
                // interesting spot, and where the eye already is. Through the cursor's own playfield
                // transform so the note lands exactly where it is drawn. Falls back to the cursor
                // position for a play that recorded no note position (a non-osu oracle run).
                Vector2? screen = breakPoint.Position != Vector2.Zero
                    ? cursor.ScreenSpaceOf(breakPoint.Position)
                    : cursor.ScreenPositionAt(ko);

                if (screen is not { } screenPos)
                    continue;

                string name = replays[i].PlayerName.Length > 0 ? replays[i].PlayerName : "unknown";

                // The combo they BROKE on — the combo held going into the break, not their peak.
                int combo = breakPoint.ComboLost;

                death = new PlayerDeathName(name, combo, effectiveColour(i), showCombo)
                {
                    BasePosition = ToLocalSpace(screenPos),
                };

                AddInternal(death);
                deaths[i] = death;
                deathNames++;
            }

            death.SetProgress(elapsed);
        }
    }

    private void removeDeath(int player)
    {
        if (deaths.Remove(player, out var death))
            death.Expire();
    }

    private void clearDeaths()
    {
        foreach (var death in deaths.Values)
            death.Expire();

        deaths.Clear();
    }

    /// <summary>Test hook: how many death names have been created (a re-seek into a window makes a
    /// fresh one, so this counts creations, not distinct players).</summary>
    internal int DeathNamesShown => deathNames;

    private int deathNames;

    /// <summary>
    /// Fades the focused player's cursor to full and every other to a whisper, or all of them back
    /// to full when nothing is hovered. Issued only when the focus has actually changed, so the
    /// fade plays once rather than being restarted every frame — and driven off the cursors' own
    /// focus channel, which multiplies over the alive/eliminated alpha rather than overwriting it.
    /// </summary>
    private void applyFocus()
    {
        if (focusedPlayer.Equals(appliedFocus))
            return;

        for (int i = 0; i < cursors.Count; i++)
        {
            if (cursors[i] is not { } cursor)
                continue;

            float target = focusedPlayer is not { } focused ? 1f : (i == focused ? 1f : 0.1f);
            cursor.SetFocusAlpha(target);
        }

        appliedFocus = focusedPlayer;
    }

    /// <summary>
    /// This player's colour, spread evenly around the hue circle so neighbouring cursors are as
    /// distinguishable as the count allows. Full saturation on purpose — these are read against
    /// moving gameplay, not on a page.
    /// </summary>
    internal static Color4 ColourFor(int index, int count)
        => Color4.FromHsv(new Vector4((float)index / Math.Max(count, 1), 0.85f, 1, 1));

    /// <summary>This player's rail mod string ("+HDDT"), keeping CL/TD that the general mod display
    /// drops — worth seeing when comparing plays side by side. Empty for a no-mod play.</summary>
    private static string railMods(ReplayAttachment replay)
    {
        var acronyms = Replays.ReplayMods.RailAcronyms(
            replay.Score?.ScoreInfo.Mods ?? System.Array.Empty<osu.Game.Rulesets.Mods.Mod>());

        return acronyms.Count > 0 ? "+" + string.Join(string.Empty, acronyms) : string.Empty;
    }

    /// <summary>This player's colour as drawn: their per-player override if set, otherwise the
    /// hue-spread default for their slot.</summary>
    private Color4 effectiveColour(int index)
    {
        var fallback = ColourFor(index, replays.Count);
        return overrideStore?.EffectiveCursorColour(replays[index], fallback) ?? fallback;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (overrideStore != null)
            overrideStore.Changed += onOverrideChanged;
    }

    /// <summary>
    /// Applies a live colour change to the one player it names — cursor, trail and rail name all
    /// re-tint where they stand. Mod and skin changes need a rebuilt render and are handled a level
    /// up in the visuals stack, so they are ignored here.
    /// </summary>
    private void onOverrideChanged(ReplayAttachment replay, Replays.PlayerOverrideKind kind)
    {
        if (kind != Replays.PlayerOverrideKind.Colour)
            return;

        for (int i = 0; i < replays.Count; i++)
        {
            if (!ReferenceEquals(replays[i], replay))
                continue;

            var colour = effectiveColour(i);

            if (i < cursors.Count)
                cursors[i]?.SetColour(colour);

            board?.SetPlayerColour(i, colour);
            break;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        if (overrideStore != null)
            overrideStore.Changed -= onOverrideChanged;

        // The preload belongs to this combine; with it gone there is nothing left to buffer, so the
        // Playback tab's bar must not linger showing a stale fraction.
        preloadTracker?.Clear();

        base.Dispose(isDisposing);
    }

    /// <summary>The play's grade letter, or nothing when the replay never decoded.</summary>
    internal static string Rank(ReplayAttachment replay)
        => replay.Score == null ? string.Empty : replay.Score.ScoreInfo.Rank.ToString();
}
