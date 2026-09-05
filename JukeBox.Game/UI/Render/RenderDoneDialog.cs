#nullable enable

using System.IO;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// The "Render complete" modal shown once a render finishes: three buttons — <b>Open folder</b>
/// (reveals the file in the OS file manager via <see cref="GameHost.PresentFileExternally"/>),
/// <b>Open file</b> (plays it in the default video app via <see cref="GameHost.OpenFileExternally"/>)
/// and <b>Close</b>. Styled like the other render modals; Escape closes.
/// </summary>
public partial class RenderDoneDialog : FocusedOverlayContainer
{
    private const float panel_width = 440;

    [Resolved]
    private GameHost host { get; set; } = null!;

    private Container panelCard = null!;
    private SpriteText pathText = null!;
    private TextButton openFolderButton = null!;
    private TextButton openFileButton = null!;
    private TextButton closeButton = null!;

    private string outputPath = string.Empty;

    public RenderDoneDialog()
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
                                Text = "Render complete",
                            },
                            pathText = new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                Colour = Theme.TextSecondary,
                                Text = string.Empty,
                                Truncate = true,
                                RelativeSizeAxes = Axes.X,
                            },
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Horizontal,
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                Spacing = new Vector2(Theme.RowSpacing, 0),
                                Children = new Drawable[]
                                {
                                    closeButton = new TextButton("Close")
                                    {
                                        Size = new Vector2(96, 36),
                                        Action = Hide,
                                    },
                                    openFolderButton = new TextButton("Open folder")
                                    {
                                        Size = new Vector2(120, 36),
                                        Action = openFolder,
                                    },
                                    openFileButton = new TextButton("Open file")
                                    {
                                        Size = new Vector2(110, 36),
                                        IdleColour = Theme.AccentDim,
                                        HoverColour = Theme.Accent,
                                        Action = openFile,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>Sets the finished file this dialog acts on and opens it.</summary>
    public void Open(string path)
    {
        outputPath = path;
        pathText.Text = path;
        Show();
    }

    private void openFolder()
    {
        if (!string.IsNullOrEmpty(outputPath))
            host.PresentFileExternally(outputPath);
    }

    private void openFile()
    {
        if (!string.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
            host.OpenFileExternally(outputPath);
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
        if (!e.Repeat && e.Key == Key.Escape)
        {
            closeButton.TriggerClick();
            return true;
        }

        return base.OnKeyDown(e);
    }

    // ---- test seams ---------------------------------------------------------------------------

    internal TextButton OpenFolderButton => openFolderButton;
    internal TextButton OpenFileButton => openFileButton;
    internal TextButton CloseButton => closeButton;
    internal string OutputPath => outputPath;
}
