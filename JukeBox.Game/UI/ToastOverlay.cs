#nullable enable

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Threading;
using osu.Game.Overlays;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI;

/// <summary>
/// The app's transient-message surface: a bottom-right stack of small toasts (queue additions,
/// import outcomes, playback/search errors). Replaces the single centred line of bare text that
/// used to be dropped at the TOP of the player area — that had no background at all (unreadable the
/// moment a bright storyboard or video frame sat underneath it) and no stacking, so two messages
/// arriving within the same dwell window were literally drawn on top of each other.
///
/// <para>
/// Stacking convention follows lazer's own <c>NotificationOverlay</c>: the newest notification lands
/// nearest the surface's anchored edge and pushes the older ones away from it. Lazer's panel is
/// top-anchored, so "newest first" there means newest at the top; this stack is bottom-anchored, so
/// the same rule puts the NEWEST at the BOTTOM (right where the eye already is, closest to the
/// corner) with older ones riding upward as new ones arrive.
/// </para>
///
/// <para>
/// Slots are assigned explicitly (<see cref="updateLayout"/>) rather than by parking the toasts in a
/// <see cref="FillFlowContainer"/>: every toast is the same fixed size, so "slot N counted up from
/// the bottom edge" is a one-line calculation, and owning it outright buys the two properties that
/// matter. A toast leaving from the TOP of the stack — which is the only way one ever leaves, since
/// both the dwell timer and the overflow cap take the oldest first — changes nobody else's slot, so
/// the survivors do not move by even a fraction of a pixel. A toast leaving from anywhere else does
/// change the slots below it, and those animate. A flow container can express neither cleanly: its
/// auto-size and its layout are two independently-timed animations (its own <c>LayoutDuration</c>
/// does not even read back as the value it was set to), so the container's edge and the children's
/// in-flow offsets race each other and the whole stack visibly lurches while one toast fades.
/// </para>
///
/// <para>
/// This is intentionally NOT a port of lazer's full NotificationOverlay: no persistent history, no
/// progress notifications, no per-notification click actions, no dismiss-all. It is a fire-and-forget
/// toast strip, so it borrows lazer's look (<see cref="OverlayColourProvider"/> Purple surfaces, an
/// accent bar and icon) and its stacking rule, and nothing else.
/// </para>
/// </summary>
public partial class ToastOverlay : CompositeDrawable
{
    /// <summary>
    /// How many toasts may be on screen at once. A burst (dropping a folder of .osz files imports
    /// them one by one, each with its own toast) must not paper over the player, so pushing past
    /// this immediately dismisses the OLDEST toasts to make room rather than dropping the newest:
    /// the most recent message is the one the user is waiting on, and the ones being evicted have
    /// already had their time on screen.
    /// </summary>
    public const int MaxVisible = 4;

    /// <summary>Gap between stacked toasts — the same rhythm as every other stacked row in the app.</summary>
    private const float stack_spacing = Theme.RowSpacing;

    /// <summary>How long a toast takes to glide into a slot that has been freed beneath it.</summary>
    internal const double ReflowDuration = Theme.DurationNormal;

    internal const Easing ReflowEasing = Theme.EaseEnter;

    private Container<Toast> stack = null!;

    /// <summary>Every toast still parented, oldest first, including ones already playing their exit.</summary>
    internal IReadOnlyList<Toast> AllToasts => stack.Children;

    /// <summary>Toasts that still count against <see cref="MaxVisible"/> — i.e. not already leaving.</summary>
    internal IEnumerable<Toast> LiveToasts => stack.Children.Where(t => !t.Dismissing);

    public ToastOverlay()
    {
        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChild = stack = new Container<Toast>
        {
            RelativeSizeAxes = Axes.Both,
            // Whatever area this overlay is given (MainScreen hands it the player box, so the toasts
            // can never reach under the side columns), inset by the same padding every other panel
            // in the app uses. Each toast then anchors to the bottom-right of THAT.
            Padding = new MarginPadding(Theme.PanelPadding),
        };
    }

    /// <summary>
    /// Shows <paramref name="message"/>. Safe to call from any thread: the whole stack (adding,
    /// evicting, the transforms) is drawable state, so the work is scheduled onto the update thread
    /// rather than done inline — several of the callers (import results, mirror failures) originate
    /// on background tasks.
    /// </summary>
    /// <param name="message">The line to show. Displayed verbatim on one truncating line — a toast
    /// is a glance, not a report, so callers keep it to a phrase.</param>
    /// <param name="accent">Bar/icon colour. Defaults to <see cref="Theme.Error"/> so a failure is
    /// unmistakably red without every call site having to remember to say so; informational toasts
    /// pass <see cref="Theme.Accent"/>.</param>
    public void Push(string message, Color4? accent = null) => Schedule(() => push(message, accent ?? Theme.Error));

    private void push(string message, Color4 accent)
    {
        // Evict oldest-first until adding one more lands exactly at the cap. A loop rather than a
        // single eviction because nothing guarantees the stack was at the cap to begin with (a
        // burst can push several times inside one frame's scheduler run).
        var live = LiveToasts.ToList();

        for (int i = 0; i <= live.Count - MaxVisible; i++)
            live[i].Dismiss();

        stack.Add(new Toast(message, accent));
    }

    /// <summary>
    /// Slots are re-derived every frame rather than only on push/removal. A toast reclaims its slot
    /// by expiring, which the parent container handles on its own schedule (and which can't be
    /// hooked from the toast itself: a drawable that has faded to Alpha 0 is no longer present, so
    /// its own scheduler stops running and could never announce that its exit had finished). Doing
    /// it unconditionally costs nothing, because <see cref="Toast.MoveToSlot"/> ignores a slot that
    /// hasn't changed — which is every toast, on almost every frame.
    /// </summary>
    protected override void Update()
    {
        base.Update();
        updateLayout();
    }

    /// <summary>
    /// Hands every toast its slot, counted up from the bottom edge so index 0 (the newest, last in
    /// the child list) sits in the corner. A toast still playing its exit keeps its slot, so nothing
    /// shuffles underneath a message the user can still read.
    /// </summary>
    private void updateLayout()
    {
        var toasts = stack.Children;

        for (int i = 0; i < toasts.Count; i++)
            toasts[i].MoveToSlot(-(toasts.Count - 1 - i) * (Toast.FixedHeight + stack_spacing));
    }

    /// <summary>
    /// One message: a fixed-size rounded surface anchored to the bottom-right of the stack, whose Y
    /// is owned by <see cref="ToastOverlay.updateLayout"/> and whose X/alpha/scale carry its own
    /// enter and exit.
    /// </summary>
    public partial class Toast : Container
    {
        /// <summary>
        /// The fixed footprint every toast takes, which <see cref="ToastOverlay.updateLayout"/>
        /// counts slots in. Named apart from <see cref="Drawable.Width"/>/<see cref="Drawable.Height"/>
        /// rather than shadowing them: as plain <c>Width</c>/<c>Height</c> these hid the base
        /// properties, which made <c>someToast.Width</c> a compile error (a const cannot be reached
        /// through an instance, and the derived name wins the lookup) and left two same-named things
        /// on one type meaning different things. The constructor assigns them to the real
        /// <see cref="Drawable.Size"/>, so the drawable's own size has always agreed with these —
        /// the collision was a readability trap, not a behavioural one.
        /// </summary>
        public const float FixedWidth = 360;

        public const float FixedHeight = 46;

        /// <summary>How long a toast sits at rest before dismissing itself.</summary>
        public const double Dwell = 4000;

        /// <summary>
        /// Deliberately NOT <see cref="Theme.EaseExit"/> (InQuint): an ease-IN curve barely moves at
        /// the start, so a short fade using it still reads as fully opaque for most of its length and
        /// the toast looks like it hangs around after its time is up. Anything LEAVING gets an
        /// ease-OUT shape so it drops out of sight immediately and the stack feels responsive.
        /// </summary>
        public const Easing ExitEasing = Easing.OutQuint;

        /// <summary>Horizontal travel of the enter/exit slide — toasts come in from, and leave
        /// toward, the right edge they are anchored to.</summary>
        private const float slide_offset = 28;

        private static readonly OverlayColourProvider colour_provider = new OverlayColourProvider(OverlayColourScheme.Purple);

        public string Message { get; }

        /// <summary>The bar/icon colour, which is what distinguishes an error toast from an
        /// informational one now that the message text itself is always plain white-on-surface.</summary>
        public Color4 AccentColour { get; }

        /// <summary>True once this toast has started leaving; it no longer counts against
        /// <see cref="MaxVisible"/> and further <see cref="Dismiss"/> calls are no-ops.</summary>
        public bool Dismissing { get; private set; }

        /// <summary>NaN until the first slot assignment, which lands instantly (a brand new toast
        /// must not glide in from wherever Y happened to start) — every later one animates.</summary>
        private float slot = float.NaN;

        private ScheduledDelegate? autoDismiss;

        public Toast(string message, Color4 accent)
        {
            Message = message;
            AccentColour = accent;

            Anchor = Anchor.BottomRight;
            Origin = Anchor.BottomRight;
            Size = new Vector2(FixedWidth, FixedHeight);

            Alpha = 0;
            Masking = true;
            CornerRadius = Theme.CornerRadius;
            // A real surface, not bare text: an opaque-ish lazer overlay background, a hairline edge
            // and a shadow, so the message stays legible over a white storyboard frame or a bright
            // video just as well as over the black letterbox bed.
            BorderThickness = 1;
            BorderColour = accent.Opacity(0.35f);
            EdgeEffect = Theme.PanelShadow;

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colour_provider.Background4,
                    Alpha = 0.97f,
                },
                // lazer's notification "light" — the accent stripe down the leading edge that tells
                // you at a glance whether something succeeded or failed.
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 4,
                    Colour = accent,
                },
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    X = 16,
                    Size = new Vector2(15),
                    Icon = accent.Equals(Theme.Error) ? FontAwesome.Solid.ExclamationCircle : FontAwesome.Solid.CheckCircle,
                    Colour = accent,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = 41, Right = 14 },
                    Child = new SpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        RelativeSizeAxes = Axes.X,
                        // Truncated rather than wrapped: every toast is then exactly Height tall,
                        // which is what lets the stack's slot maths be a single multiplication, and
                        // a beatmap title long enough to wrap is long enough that its tail says
                        // nothing.
                        Truncate = true,
                        Font = FontUsage.Default.With(size: Theme.RowTitleTextSize),
                        Colour = Theme.TextPrimary,
                        Text = message,
                    },
                },
            };
        }

        /// <summary>Takes the slot <see cref="ToastOverlay.updateLayout"/> has assigned. Repeated
        /// calls with an unchanged slot do nothing at all, so a toast whose position is unaffected by
        /// another one leaving is never handed a transform to run.</summary>
        internal void MoveToSlot(float y)
        {
            if (slot.Equals(y))
                return;

            bool firstPlacement = float.IsNaN(slot);
            slot = y;

            if (firstPlacement)
                Y = y;
            else
                this.MoveToY(y, ReflowDuration, ReflowEasing);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // A burst big enough to blow the cap can evict a toast in the very frame it was pushed,
            // before it has finished loading and therefore before it could be animated at all. Such
            // a toast must go straight out (it starts at Alpha 0, so it is never seen) rather than
            // play an entrance it has already lost the right to.
            if (Dismissing)
            {
                animateOut();
                return;
            }

            this.MoveToX(slide_offset).MoveToX(0, Theme.DurationNormal, Theme.EaseEnter);
            this.FadeInFromZero(Theme.DurationNormal, Theme.EaseEnter);
            this.ScaleTo(Theme.PopScale).ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter);

            autoDismiss = Scheduler.AddDelayed(Dismiss, Dwell);
        }

        /// <summary>
        /// Starts the exit. Called by the dwell timer, and early by <see cref="ToastOverlay"/> when
        /// this toast is evicted to keep the stack at <see cref="MaxVisible"/>.
        /// </summary>
        public void Dismiss()
        {
            if (Dismissing)
                return;

            Dismissing = true;
            autoDismiss?.Cancel();

            if (IsLoaded)
                animateOut();
        }

        private void animateOut()
        {
            this.FadeOut(Theme.DurationFast, ExitEasing);
            this.MoveToX(slide_offset, Theme.DurationFast, ExitEasing);
            this.ScaleTo(Theme.PopScale, Theme.DurationFast, ExitEasing);

            // Expiry (rather than a delayed callback) is what frees the slot: the exit fade ends at
            // Alpha 0, and a drawable that isn't present stops having its scheduler run at all, so
            // anything self-timed here would simply never fire. Lifetime is the parent's business,
            // and the parent is always present.
            LifetimeEnd = Time.Current + Theme.DurationFast;
        }
    }
}
