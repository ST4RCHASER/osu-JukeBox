#nullable enable

using System;
using System.IO;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// The render dialog's IN-APP save-location browser — the one Browse… behaviour on every platform,
/// in the app's own look, instead of three different OS dialogs. Styled like
/// <see cref="FileImportOverlay"/> (dim scrim, rounded panel, Escape cancels): lazer's own
/// <see cref="OsuDirectorySelector"/> (breadcrumbs, directory list) to pick WHERE, and an editable
/// file-name field seeded from the dialog's current save path for WHAT. "Save here" (or Enter)
/// raises <see cref="PathChosen"/> with the combined path — the caller lands it through the same
/// validation as typing — and Cancel/Escape changes nothing.
/// </summary>
public partial class SaveLocationOverlay : FocusedOverlayContainer
{
    private const float panel_width = 640;
    private const float panel_height = 520;

    /// <summary>Fired with the full chosen path (directory + file name). The overlay closes itself
    /// immediately after; the caller owns what happens to the path.</summary>
    public event Action<string>? PathChosen;

    // The DI lazer's file-selection components expect for their accent/background colours — the
    // same scheme the rest of this app's lazer-derived surfaces use.
    [Cached]
    private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

    private Container panelCard = null!;
    private Container selectorHost = null!;
    private OsuDirectorySelector directories = null!;
    private AccentTextBox nameBox = null!;
    private TextButton saveButton = null!;
    private TextButton cancelButton = null!;

    /// <summary>Test seams (JukeBox.Game.Tests has InternalsVisibleTo): the directory list's own
    /// bindable (what a click writes), the name field, and the two buttons.</summary>
    internal OsuDirectorySelector Directories => directories;

    internal AccentTextBox NameBox => nameBox;

    internal TextButton SaveButton => saveButton;

    internal TextButton CancelButton => cancelButton;

    [BackgroundDependencyLoader]
    private void load()
    {
        RelativeSizeAxes = Axes.Both;

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
                                new Dimension(GridSizeMode.AutoSize),
                            },
                            Content = new[]
                            {
                                new Drawable[] { createHeader() },
                                new Drawable[]
                                {
                                    selectorHost = new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Padding = new MarginPadding { Vertical = Theme.SectionSpacing },
                                    },
                                },
                                new Drawable[] { createNameRow() },
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
                Text = "Choose where to save",
            },
            new SpriteText
            {
                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                Colour = Theme.TextSecondary,
                Text = "Pick a folder, name the file, and Save here.",
            },
        },
    };

    private Drawable createNameRow() => new Container
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Padding = new MarginPadding { Bottom = Theme.SectionSpacing },
        Children = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 36,
                Padding = new MarginPadding { Left = 90 },
                Child = nameBox = new AccentTextBox
                {
                    RelativeSizeAxes = Axes.Both,
                    PlaceholderText = "file name",
                },
            },
            new SpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                Colour = Theme.TextSecondary,
                Text = "File name",
            },
        },
    };

    private Drawable createFooter() => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Horizontal,
        Anchor = Anchor.TopRight,
        Origin = Anchor.TopRight,
        Spacing = new Vector2(Theme.RowSpacing, 0),
        Children = new Drawable[]
        {
            cancelButton = new TextButton("Cancel")
            {
                Size = new Vector2(88, 34),
                Action = Hide,
            },
            saveButton = new TextButton("Save here")
            {
                Size = new Vector2(110, 34),
                IdleColour = Theme.AccentDim,
                HoverColour = Theme.Accent,
                Action = confirm,
            },
        },
    };

    protected override void LoadComplete()
    {
        base.LoadComplete();

        // Enter in the name field is Save: same path as the button, so the two can't drift.
        nameBox.OnCommit += (_, _) => confirm();
    }

    /// <summary>
    /// Opens the browser seeded from <paramref name="currentPath"/>: its folder when that exists
    /// (falling back like the import picker — last-known real place, never a dead path), its file
    /// name in the editable field.
    /// </summary>
    public void Open(string? currentPath)
    {
        string? seedDirectory = null;
        string seedName = string.Empty;

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            try
            {
                seedDirectory = Path.GetDirectoryName(currentPath.Trim());
                seedName = Path.GetFileName(currentPath.Trim());
            }
            catch (ArgumentException)
            {
                // Half-typed text in the field must not break Browse — open somewhere real instead.
            }
        }

        // A FRESH selector per open, built directly on the seed folder: an overlay's children only
        // load once it first shows, and a still-loading selector would apply its constructor path
        // over anything set on its bindable beforehand — constructing it with the right path is the
        // race-free way to start where the user's current save path points.
        selectorHost.Child = directories = new OsuDirectorySelector(FileImportOverlay.ResolveInitialPath(seedDirectory))
        {
            RelativeSizeAxes = Axes.Both,
        };

        nameBox.Text = seedName;

        Show();
    }

    private void confirm()
    {
        string directory = directories.CurrentPath.Value?.FullName ?? string.Empty;
        string name = (nameBox.Text ?? string.Empty).Trim();

        // No name means nothing to save AS — stay open so the user can type one (the field's
        // placeholder says what is missing), rather than closing on a path validation will reject.
        if (directory.Length == 0 || name.Length == 0)
            return;

        PathChosen?.Invoke(Path.Combine(directory, name));
        Hide();
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
        if (e.Repeat)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            // Escape is Cancel: same path as the button, so neither can drift from the other. It is
            // handled HERE so it can never fall through and close the render dialog underneath too.
            case Key.Escape:
                cancelButton.TriggerClick();
                return true;

            case Key.Enter or Key.KeypadEnter:
                confirm();
                return true;
        }

        return base.OnKeyDown(e);
    }
}
