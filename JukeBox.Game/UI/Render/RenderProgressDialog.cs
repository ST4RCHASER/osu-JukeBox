#nullable enable

using System;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays;
using osu.Game.Overlays.Dialog;
using osuTK;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// The modal shown while a render runs: a progress bar (frames done / total, with an ETA), a live
/// freeze-frame thumbnail of the frame currently being encoded, and a Cancel button.
///
/// <para>
/// Cancel doesn't abort immediately — it pushes a <b>hold-to-confirm</b> confirmation through the
/// app's <see cref="osu.Game.Overlays.IDialogOverlay"/> (lazer's <see cref="DangerousActionDialog"/>,
/// the same press-and-hold pattern the cache-clear and skin-remove actions use), and only a
/// completed hold fires <see cref="CancelConfirmed"/> — where the driver aborts ffmpeg and deletes
/// the partial file. Escape is Cancel too.
/// </para>
/// </summary>
public partial class RenderProgressDialog : FocusedOverlayContainer
{
    private const float panel_width = 460;

    /// <summary>Fired once the user has held the cancel confirmation to completion — the driver
    /// aborts the encode and cleans up the partial output.</summary>
    public Action? CancelConfirmed;

    [Resolved(canBeNull: true)]
    private IDialogOverlay? dialogOverlay { get; set; }

    private Container panelCard = null!;
    private Sprite thumbnail = null!;
    private Box thumbnailPlaceholder = null!;
    private Container progressFill = null!;
    private SpriteText progressText = null!;
    private SpriteText etaText = null!;

    private double startClockMs;
    private bool started;

    public RenderProgressDialog()
    {
        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ModalScrim,
            },
            panelCard = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = panel_width,
                AutoSizeAxes = Axes.Y,
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
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding(Theme.PanelPadding),
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, Theme.SectionSpacing),
                        Children = new Drawable[]
                        {
                            new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                                Colour = Theme.TextPrimary,
                                Text = "Rendering…",
                            },
                            // The live freeze-frame. A placeholder box sits behind it so the panel
                            // keeps its height before the first frame arrives.
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 200,
                                Masking = true,
                                CornerRadius = Theme.CornerRadius,
                                Children = new Drawable[]
                                {
                                    thumbnailPlaceholder = new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Colour = Theme.ElevatedSurface,
                                    },
                                    thumbnail = new Sprite
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        FillMode = FillMode.Fit,
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                        Alpha = 0,
                                    },
                                },
                            },
                            // Progress track + fill.
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 6,
                                Masking = true,
                                CornerRadius = 3,
                                Children = new Drawable[]
                                {
                                    new Box
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Colour = Theme.ElevatedSurface,
                                    },
                                    progressFill = new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Width = 0,
                                        Child = new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Colour = Theme.Accent,
                                        },
                                    },
                                },
                            },
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Children = new Drawable[]
                                {
                                    progressText = new SpriteText
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                        Colour = Theme.TextSecondary,
                                        Text = "Preparing…",
                                    },
                                    etaText = new SpriteText
                                    {
                                        Anchor = Anchor.CentreRight,
                                        Origin = Anchor.CentreRight,
                                        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                        Colour = Theme.TextTertiary,
                                        Text = string.Empty,
                                    },
                                },
                            },
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Horizontal,
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                Child = new TextButton("Cancel")
                                {
                                    Size = new Vector2(96, 36),
                                    Action = requestCancel,
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Updates the bar, the "N / M frames" line and the ETA. Call from the render loop as frames land
    /// (on the update thread — the driver Schedules it).
    /// </summary>
    public void UpdateProgress(int framesDone, int totalFrames)
    {
        if (!started)
        {
            started = true;
            startClockMs = Clock.CurrentTime;
        }

        float fraction = totalFrames <= 0 ? 0 : Math.Clamp((float)framesDone / totalFrames, 0, 1);
        progressFill.ResizeWidthTo(fraction, Theme.HoverFadeDuration, Easing.OutQuad);

        progressText.Text = $"{framesDone} / {totalFrames} frames  ({fraction * 100:0}%)";

        double elapsed = Clock.CurrentTime - startClockMs;
        if (framesDone > 0 && fraction is > 0 and < 1)
        {
            double remainingMs = elapsed / framesDone * (totalFrames - framesDone);
            etaText.Text = $"about {RenderValidation.FormatTimecode(remainingMs)} left";
        }
        else
        {
            etaText.Text = fraction >= 1 ? "finishing…" : string.Empty;
        }
    }

    /// <summary>Shows the newly-rendered frame as the live thumbnail. The driver owns the texture.</summary>
    public void UpdateThumbnail(Texture texture)
    {
        thumbnail.Texture = texture;
        thumbnail.Alpha = 1;
        thumbnailPlaceholder.Alpha = 0;
    }

    private void requestCancel()
    {
        if (dialogOverlay == null)
        {
            // No confirmation host (a bare test) — treat Cancel as immediate, never as "carry on".
            CancelConfirmed?.Invoke();
            Hide();
            return;
        }

        dialogOverlay.Push(new CancelRenderDialog(() =>
        {
            CancelConfirmed?.Invoke();
            Hide();
        }));
    }

    protected override void PopIn()
    {
        this.FadeIn(Theme.DurationNormal, Theme.EaseEnter);
        panelCard.ScaleTo(Theme.PopScale).Then().ScaleTo(1f, Theme.DurationNormal, Theme.EaseEnter);
    }

    protected override void PopOut()
    {
        this.FadeOut(Theme.DurationFast, Theme.EaseExit);
        panelCard.ScaleTo(Theme.PopScale, Theme.DurationFast, Theme.EaseExit);
    }

    protected override bool OnKeyDown(osu.Framework.Input.Events.KeyDownEvent e)
    {
        if (!e.Repeat && e.Key == osuTK.Input.Key.Escape)
        {
            requestCancel();
            return true;
        }

        return base.OnKeyDown(e);
    }

    // ---- test seams ---------------------------------------------------------------------------

    internal Container ProgressFill => progressFill;
    internal SpriteText ProgressText => progressText;
    internal SpriteText EtaText => etaText;
    internal Sprite Thumbnail => thumbnail;

    /// <summary>
    /// lazer's press-and-hold caution dialog, worded for cancelling a render. Subclassing
    /// <see cref="DangerousActionDialog"/> is what supplies the hold-to-confirm dangerous button and
    /// the cancel button — none of that is written here (mirrors MaintenanceSection's dialogs).
    /// </summary>
    internal partial class CancelRenderDialog : DangerousActionDialog
    {
        public CancelRenderDialog(Action cancelRender)
        {
            HeaderText = "Cancel this render?";
            BodyText = "The half-written video file will be deleted.";
            DangerousAction = cancelRender;
        }
    }
}
