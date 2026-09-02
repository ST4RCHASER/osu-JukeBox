#nullable enable

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Overlays;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// The readout a keyboard shortcut puts on screen while it changes something continuous — playback
/// speed, playfield zoom — so the key press has a visible answer instead of a silent one.
///
/// <para>
/// One shared surface rather than one per setting: they can never be needed at the same instant (a
/// key press is a single event), and two independently-timed panels fading over each other looked
/// like a bug. Each <see cref="Display"/> replaces whatever the last one said and restarts the
/// dwell, exactly as holding a key to ramp a value should behave.
/// </para>
///
/// <para>
/// Volume deliberately does NOT come through here: that is lazer's own <see cref="VolumeOverlay"/>,
/// hosted as-is (see <c>MainScreen</c>), which brings its own meters, its own master/effect/music
/// selection and its own timing. Reimplementing it here would have been a worse copy of something
/// already available.
/// </para>
/// </summary>
public partial class TransientValueOverlay : CompositeDrawable
{
    /// <summary>How long the readout sits at full opacity before fading. Long enough to read a
    /// number, short enough that repeated presses feel like one continuous adjustment.</summary>
    public const double Dwell = 900;

    private const double fade_in = 60;
    private const double fade_out = 300;

    private const float bar_width = 180;
    private const float bar_height = 4;

    private static readonly OverlayColourProvider colour_provider = new OverlayColourProvider(OverlayColourScheme.Purple);

    private SpriteText label = null!;
    private SpriteText value = null!;
    private Container barTrack = null!;
    private Box barFill = null!;

    /// <summary>Test-only (JukeBox.Game.Tests has InternalsVisibleTo): what the readout currently
    /// says, so a test can assert the shortcut's feedback rather than only its side effect.</summary>
    internal string LabelText => label.Text.ToString();

    internal string ValueText => value.Text.ToString();

    /// <summary>Test-only: the filled fraction of the bar, or 0 when the bar is hidden.</summary>
    internal float BarFraction => barTrack.Alpha == 0 ? 0 : barFill.Width;

    public TransientValueOverlay()
    {
        // Bottom-centre: clear of the toast stack (bottom-RIGHT) and of lazer's volume meters
        // (which anchor to the left edge), so all three can be on screen at once without overlap.
        Anchor = Anchor.BottomCentre;
        Origin = Anchor.BottomCentre;
        AutoSizeAxes = Axes.Both;
        Margin = new MarginPadding { Bottom = 48 };

        Alpha = 0;

        Masking = true;
        CornerRadius = Theme.CornerRadius;
        EdgeEffect = Theme.PanelShadow;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            // Opaque enough to stay legible over a bright storyboard frame, which is the case that
            // made a bare text readout unusable.
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = colour_provider.Background4,
                Alpha = 0.97f,
            },
            new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 6),
                Padding = new MarginPadding { Horizontal = 20, Vertical = 12 },
                Children = new Drawable[]
                {
                    label = new SpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                        Colour = Theme.TextTertiary,
                    },
                    value = new SpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = FontUsage.Default.With(size: 24),
                        Colour = Theme.TextPrimary,
                    },
                    barTrack = new Container
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Size = new Vector2(bar_width, bar_height),
                        Masking = true,
                        CornerRadius = bar_height / 2,
                        Margin = new MarginPadding { Top = 2 },
                        Children = new Drawable[]
                        {
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = Theme.ElevatedSurface,
                            },
                            barFill = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Width = 0,
                                Colour = Theme.Accent,
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Puts <paramref name="value"/> on screen under <paramref name="label"/> and restarts the dwell.
    /// </summary>
    /// <param name="label">What is being changed ("Speed", "Playfield zoom").</param>
    /// <param name="value">The new value, already formatted for reading ("1.25×", "110%").</param>
    /// <param name="fraction">Where the value sits in its range, 0-1, for the bar — or null for a
    /// value with no meaningful range to draw.</param>
    public void Display(string label, string value, float? fraction = null)
    {
        this.label.Text = label;
        this.value.Text = value;

        barTrack.Alpha = fraction is float f ? 1 : 0;

        if (fraction is float fill)
            barFill.Width = Math.Clamp(fill, 0, 1);

        // Driven entirely by transforms rather than a scheduled "now hide" callback: a drawable at
        // Alpha 0 is not present, and a non-present drawable's scheduler does not run — so the
        // callback that was meant to end the previous display would never fire, and the overlay
        // would stick. Transforms keep being processed either way.
        ClearTransforms();

        this.FadeIn(fade_in, Easing.OutQuint)
            .Delay(Dwell)
            .FadeOut(fade_out, Easing.OutQuint);
    }
}
