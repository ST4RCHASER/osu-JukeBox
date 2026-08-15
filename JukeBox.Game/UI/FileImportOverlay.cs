#nullable enable

using System;
using System.IO;
using System.Linq;
using JukeBox.Game.Configuration;
using JukeBox.Game.Import;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// The in-app file picker behind the sidebar's folder button — the click-to-import counterpart to
/// dragging a file onto the window. Choosing a file raises <see cref="FileSelected"/>, and
/// <see cref="Screens.MainScreen"/> feeds that straight into
/// <see cref="DroppedFileImporter.Import"/>: the SAME path a drop takes, so both routes produce
/// the same imports, the same toasts and the same failures with no logic duplicated between them.
///
/// The browser itself is lazer's own <see cref="OsuFileSelector"/> (breadcrumbs, directory list,
/// hidden-file handling), restricted to <see cref="SupportedExtensions"/> so the picker can never
/// offer a file the importer would only reject. It is deliberately an IN-APP selector rather than
/// an OS dialog — the framework's <c>FileSelector</c> would delegate to a system one if the host
/// provided it, and no host here does, so this stays consistent across platforms and testable.
///
/// Escape and the Cancel button both close without importing.
/// </summary>
public partial class FileImportOverlay : FocusedOverlayContainer
{
    private const float panel_width = 640;
    private const float panel_height = 520;

    /// <summary>
    /// What the picker will show — exactly the extensions <see cref="DroppedFile.Classify"/>
    /// recognises. Asserted against that classifier in the tests, so adding a format there without
    /// widening the picker (or the reverse) is a test failure rather than a silent gap.
    /// </summary>
    public static readonly string[] SupportedExtensions = { ".osz", ".osk", ".osr" };

    /// <summary>Fired with the full path of the chosen file. The overlay closes itself
    /// immediately after; the caller owns what importing means.</summary>
    public event Action<string>? FileSelected;

    // The DI lazer's file-selection components expect for their accent/background colours — the
    // same scheme the rest of this app's lazer-derived surfaces use.
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    private Container panelCard = null!;
    private OsuFileSelector selector = null!;
    private TextButton cancelButton = null!;

    private Bindable<string> lastDirectory = null!;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the selector, so a
    /// test can drive a real selection through <c>CurrentFile</c> — the same bindable a click in
    /// the list writes — rather than simulating one.</summary>
    internal OsuFileSelector Selector => selector;

    /// <summary>Test-only access (JukeBox.Game.Tests has InternalsVisibleTo) to the Cancel button.</summary>
    internal TextButton CancelButton => cancelButton;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

        lastDirectory = config?.GetBindable<string>(JukeBoxSetting.LastImportDirectory)
                        ?? new Bindable<string>(string.Empty);

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
                Size = new Vector2(panel_width, panel_height),
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
                                new Dimension(GridSizeMode.AutoSize),
                            },
                            Content = new[]
                            {
                                new Drawable[] { createHeader() },
                                new Drawable[]
                                {
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Padding = new MarginPadding { Vertical = Theme.SectionSpacing },
                                        Child = selector = new OsuFileSelector(ResolveInitialPath(lastDirectory.Value), SupportedExtensions)
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                        },
                                    },
                                },
                                new Drawable[] { createFooter() },
                            },
                        },
                    },
                },
            },
        };
    }

    private Drawable createHeader() => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new Vector2(0, Theme.RowSpacing / 2),
        Children = new Drawable[]
        {
            new SpriteText
            {
                Font = FontUsage.Default.With(size: Theme.HeaderTextSize),
                Colour = Theme.TextPrimary,
                Text = "Import a file",
            },
            new SpriteText
            {
                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                Colour = Theme.TextSecondary,
                Text = $"Pick a {string.Join(", ", SupportedExtensions)} file — the same as dragging it onto the window.",
            },
        },
    };

    private Drawable createFooter() => new Container
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Child = cancelButton = new TextButton("Cancel")
        {
            Anchor = Anchor.CentreRight,
            Origin = Anchor.CentreRight,
            Size = new Vector2(88, 34),
            Action = Hide,
        },
    };

    protected override void LoadComplete()
    {
        base.LoadComplete();

        selector.CurrentFile.BindValueChanged(e =>
        {
            if (e.NewValue == null)
                return;

            FileSelected?.Invoke(e.NewValue.FullName);
            Hide();
        });

        // Remembered on every directory change rather than only on a successful pick, so a browse
        // that ends in Cancel still leaves the picker where the user was looking.
        selector.CurrentPath.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                lastDirectory.Value = e.NewValue.FullName;
        });
    }

    /// <summary>
    /// Where the picker opens: the directory it was last left in, falling back to <c>~/Downloads</c>
    /// (where a browser drops a freshly downloaded .osz) and then to the home directory. Each
    /// candidate is existence-checked — a remembered directory that has since been deleted, or a
    /// machine with no Downloads folder, must land somewhere real rather than on a dead path.
    /// </summary>
    internal static string ResolveInitialPath(string? remembered)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new[]
        {
            remembered,
            Path.Combine(home, "Downloads"),
            home,
        };

        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && Directory.Exists(c)) ?? home;
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

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!e.Repeat && e.Key == Key.Escape)
        {
            // Escape is Cancel: same path as the button, so neither can drift from the other.
            cancelButton.TriggerClick();
            return true;
        }

        return base.OnKeyDown(e);
    }
}
