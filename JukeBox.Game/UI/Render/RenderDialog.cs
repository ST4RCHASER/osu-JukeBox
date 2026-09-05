#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using JukeBox.Game.UI;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK;
using osuTK.Input;

namespace JukeBox.Game.UI.Render;

/// <summary>
/// The "File &gt; Render…" dialog — a centred modal (styled exactly like <see cref="MapIdOverlay"/>:
/// dim <see cref="Theme.ModalScrim"/> behind a rounded panel card, Escape cancels) that collects the
/// parameters of an offline video render of whatever is currently in playback.
///
/// <para>
/// Every field is validated live by the pure <see cref="RenderValidation"/>: an inline message in
/// soft red appears under any field that doesn't parse or is out of range, and <b>Render</b> stays
/// disabled until they ALL pass AND <see cref="RenderEnabled"/> is true (the lead binds that to the
/// inverse of "spectating", so Render greys out while spectating). A <b>Preset</b> dropdown
/// (YouTube / Facebook / TikTok / Custom) fills the fields via <see cref="RenderPreset.ApplyTo"/>;
/// the fields stay editable and the dropdown snaps to whichever preset the current values match — to
/// Custom the instant one differs — via <see cref="RenderPreset.Match"/>.
/// </para>
///
/// <para>
/// The lead opens it with <see cref="Open"/> (passing the current song length so End can be bounded
/// and defaulted) and handles the produced <see cref="RenderRequest"/> through
/// <see cref="RenderRequested"/>, driving the actual encode with the current set / replays it
/// already holds.
/// </para>
/// </summary>
public partial class RenderDialog : FocusedOverlayContainer
{
    private const float panel_width = 480;

    /// <summary>Fired with a fully-validated request when Render is pressed. The lead runs the
    /// encode (it has the current set and replays); the dialog's job ends here.</summary>
    public Action<RenderRequest>? RenderRequested;

    /// <summary>Gates the Render button on top of field validity — the lead binds this to "not
    /// spectating" so Render greys out during a spectate session. Settable/bindable input.</summary>
    public Bindable<bool> RenderEnabled { get; } = new BindableBool(true);

    /// <summary>The current song's length in milliseconds, set by <see cref="Open"/>. End time must
    /// fall within it; 0 means "unknown" and skips that bound (a bare test host).</summary>
    public double SongLengthMs { get; private set; }

    [Resolved]
    private GameHost host { get; set; } = null!;

    private Container panelCard = null!;

    private BasicDropdown<RenderPreset> presetDropdown = null!;
    private BasicDropdown<string> formatDropdown = null!;
    private AccentTextBox resolutionBox = null!;
    private AccentTextBox fpsBox = null!;
    private AccentTextBox pathBox = null!;
    private AccentTextBox startBox = null!;
    private AccentTextBox endBox = null!;
    private AccentTextBox audioBox = null!;
    private TextButton browseButton = null!;
    private TextButton renderButton = null!;
    private TextButton cancelButton = null!;

    private readonly Dictionary<RenderField, SpriteText> errorTexts = new Dictionary<RenderField, SpriteText>();

    // Set while the code (not the user) is rewriting fields — suppresses the field→preset snap and
    // the preset→field fill from re-entering each other.
    private bool updating;

    private ISystemFileSelector? fileSelector;

    public RenderDialog()
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
                                Text = "Render video",
                            },
                            new SpriteText
                            {
                                Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
                                Colour = Theme.TextSecondary,
                                Text = "Export exactly what's playing now to a video file.",
                            },
                            labelledPreset(),
                            labelledFormat(),
                            labelledField(RenderField.Resolution, "Resolution", resolutionBox = timerBox("1920x1080")),
                            labelledField(RenderField.Fps, "Frame rate (fps)", fpsBox = timerBox("60")),
                            savePathRow(),
                            twoUp(
                                labelledField(RenderField.StartTime, "Start (hh:mm:ss)", startBox = timerBox("0:00:00")),
                                labelledField(RenderField.EndTime, "End (hh:mm:ss)", endBox = timerBox("0:00:00"))),
                            labelledField(RenderField.AudioBitrate, "Audio bitrate (kbps)", audioBox = timerBox("192")),
                            buttonRow(),
                        },
                    },
                },
            },
        };

        wireField(resolutionBox);
        wireField(fpsBox);
        wireField(pathBox);
        wireField(startBox);
        wireField(endBox);
        wireField(audioBox);

        formatDropdown.Current.BindValueChanged(_ => onUserEdit());

        presetDropdown.Current.BindValueChanged(preset =>
        {
            if (updating)
                return;

            applyPreset(preset.NewValue);
        });

        RenderEnabled.BindValueChanged(_ => revalidate());
    }

    /// <summary>
    /// Configures the dialog for the current song and opens it: sets the length End is bounded by,
    /// defaults End to the whole song and the save path to <paramref name="defaultDirectory"/> /
    /// <paramref name="defaultFileStem"/>, and applies the YouTube preset as the starting point.
    /// </summary>
    public void Open(double songLengthMs, string defaultDirectory, string defaultFileStem)
    {
        SongLengthMs = songLengthMs;

        updating = true;

        // Start from a sensible platform preset, then set the fields a preset doesn't own.
        formatDropdown.Current.Value = RenderPreset.YouTube.Format;
        resolutionBox.Text = $"{RenderPreset.YouTube.Width}x{RenderPreset.YouTube.Height}";
        fpsBox.Text = RenderPreset.YouTube.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        audioBox.Text = RenderPreset.YouTube.AudioBitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        presetDropdown.Current.Value = RenderPreset.YouTube;

        startBox.Text = "0:00:00";
        endBox.Text = RenderValidation.FormatTimecode(songLengthMs);

        string stem = string.IsNullOrWhiteSpace(defaultFileStem) ? "render" : defaultFileStem;
        string ext = FfmpegEncoder.ExtensionFor(RenderPreset.YouTube.Format);
        pathBox.Text = Path.Combine(defaultDirectory, $"{stem}.{ext}");

        updating = false;

        revalidate();
        Show();
    }

    private void applyPreset(RenderPreset preset)
    {
        if (!preset.HasValues)
        {
            // Custom: change nothing, just revalidate so the button state is right.
            revalidate();
            return;
        }

        var filled = preset.ApplyTo(currentValues());

        updating = true;
        formatDropdown.Current.Value = filled.Format;
        resolutionBox.Text = filled.Resolution;
        fpsBox.Text = filled.Fps;
        audioBox.Text = filled.AudioBitrate;
        updating = false;

        revalidate();
    }

    private void onUserEdit()
    {
        if (updating)
            return;

        // Snap the preset dropdown to whatever the current values match — a named preset while they
        // still match it, Custom the instant one differs.
        var match = RenderPreset.Match(
            tryInt(resolutionWidth()),
            tryInt(resolutionHeight()),
            tryInt(fpsBox.Text),
            formatDropdown.Current.Value ?? string.Empty,
            tryInt(audioBox.Text));

        updating = true;
        presetDropdown.Current.Value = match;
        updating = false;

        revalidate();
    }

    private void revalidate()
    {
        var result = RenderValidation.Validate(currentValues(), SongLengthMs);

        foreach (var (field, text) in errorTexts)
            text.Text = result.Errors.TryGetValue(field, out string? message) ? message : string.Empty;

        bool enabled = result.IsValid && RenderEnabled.Value;
        renderButton.Enabled.Value = enabled;
        renderButton.IdleColour = enabled ? Theme.AccentDim : Theme.ElevatedSurface.Opacity(0.4f);
    }

    private RenderFormValues currentValues() => new RenderFormValues(
        formatDropdown.Current.Value ?? string.Empty,
        resolutionBox.Text,
        fpsBox.Text,
        pathBox.Text,
        startBox.Text,
        endBox.Text,
        audioBox.Text);

    private void onRender()
    {
        var result = RenderValidation.Validate(currentValues(), SongLengthMs);

        if (!result.IsValid || !RenderEnabled.Value || result.Request == null)
        {
            // The button should have been disabled, but never fire a half-valid request.
            revalidate();
            return;
        }

        RenderRequested?.Invoke(result.Request);
        Hide();
    }

    private void browse()
    {
        // A real SAVE panel on macOS, driven through osascript exactly like File → Open…'s picker
        // (see NativeSaveDialog): the framework's system file selector never presents a panel on
        // macOS, which is what left this button dead. Seeded with the current field's folder and
        // file name so the panel opens where the user already is.
        if (Import.NativeSaveDialog.IsAvailable)
        {
            string current = (pathBox.Text ?? string.Empty).Trim();
            string? directory = safeDirectoryName(current);
            string? fileName = current.Length == 0 ? null : Path.GetFileName(current);

            _ = browseNativeAsync(directory, fileName);
            return;
        }

        // Elsewhere the framework's open-file dialog stands in for a save picker: the user
        // names/points at the location, and its full path becomes the save path. Where the platform
        // has no native selector either, the text field is the way to set it.
        if (fileSelector == null)
        {
            fileSelector = host.CreateSystemFileSelector(new[] { ".mp4", ".webm", ".mov" });

            // Subscribed once, on creation — a handler added per click would apply a pick as many
            // times as Browse… had ever been pressed.
            if (fileSelector != null)
                fileSelector.Selected += file => Schedule(() => ApplyBrowsedPath(file.FullName));
        }

        if (fileSelector == null)
        {
            Logger.Log("No native file selector on this platform — type the save path instead.");
            return;
        }

        fileSelector.Present();
    }

    private async System.Threading.Tasks.Task browseNativeAsync(string? directory, string? fileName)
    {
        string? chosen = await Import.NativeSaveDialog.PickSaveAsync(directory, fileName).ConfigureAwait(false);
        Schedule(() => ApplyBrowsedPath(chosen));
    }

    /// <summary>
    /// Lands a browsed path in the save-location field and revalidates, exactly as typing it would
    /// have. A null/blank path (the user cancelled the panel) changes nothing. Internal for the
    /// tests (JukeBox.Game.Tests has InternalsVisibleTo).
    /// </summary>
    internal void ApplyBrowsedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        pathBox.Text = path.Trim();
        onUserEdit();
    }

    /// <summary>The directory portion of the field's current text, or null when it has none (or is
    /// not a well-formed path at all — half-typed text must not break the Browse button).</summary>
    private static string? safeDirectoryName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            string? directory = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory) ? null : directory;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---- field plumbing -------------------------------------------------------------------------

    private void wireField(AccentTextBox box) => box.Current.BindValueChanged(_ => onUserEdit());

    private static AccentTextBox timerBox(string placeholder) => new AccentTextBox
    {
        RelativeSizeAxes = Axes.X,
        Height = 36,
        PlaceholderText = placeholder,
    };

    private Drawable labelledField(RenderField field, string label, AccentTextBox box)
    {
        var error = new SpriteText
        {
            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
            Colour = Theme.Error,
            Text = string.Empty,
        };
        errorTexts[field] = error;

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
            Children = new Drawable[]
            {
                caption(label),
                box,
                error,
            },
        };
    }

    private Drawable labelledPreset()
    {
        presetDropdown = new BasicDropdown<RenderPreset>
        {
            RelativeSizeAxes = Axes.X,
            Items = RenderPreset.All,
        };

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
            Children = new Drawable[] { caption("Preset"), presetDropdown },
        };
    }

    private Drawable labelledFormat()
    {
        formatDropdown = new BasicDropdown<string>
        {
            RelativeSizeAxes = Axes.X,
            Items = RenderValidation.Formats,
        };

        var error = new SpriteText
        {
            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
            Colour = Theme.Error,
            Text = string.Empty,
        };
        errorTexts[RenderField.Format] = error;

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
            Children = new Drawable[] { caption("File format"), formatDropdown, error },
        };
    }

    private Drawable savePathRow()
    {
        var error = new SpriteText
        {
            Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
            Colour = Theme.Error,
            Text = string.Empty,
        };
        errorTexts[RenderField.Path] = error;

        pathBox = new AccentTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = 36,
            PlaceholderText = "save location",
        };

        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
            Children = new Drawable[]
            {
                caption("Save location"),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 36,
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding { Right = 96 },
                            Child = pathBox,
                        },
                        browseButton = new TextButton("Browse…")
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Size = new Vector2(88, 34),
                            Action = browse,
                        },
                    },
                },
                error,
            },
        };
    }

    private static Drawable twoUp(Drawable left, Drawable right) => new GridContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        ColumnDimensions = new[]
        {
            new Dimension(),
            new Dimension(GridSizeMode.Absolute, Theme.RowSpacing),
            new Dimension(),
        },
        RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
        Content = new[] { new[] { left, Empty(), right } },
    };

    private Drawable buttonRow() => new FillFlowContainer
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
                Size = new Vector2(96, 36),
                Action = Hide,
            },
            renderButton = new TextButton("Render")
            {
                Size = new Vector2(120, 36),
                IdleColour = Theme.AccentDim,
                HoverColour = Theme.Accent,
                Action = onRender,
            },
        },
    };

    private static SpriteText caption(string text) => new SpriteText
    {
        Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
        Colour = Theme.TextSecondary,
        Text = text,
    };

    private string resolutionWidth()
    {
        string[] parts = (resolutionBox.Text ?? string.Empty).Split('x', 'X', '×');
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private string resolutionHeight()
    {
        string[] parts = (resolutionBox.Text ?? string.Empty).Split('x', 'X', '×');
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    private static int tryInt(string? text)
        => int.TryParse((text ?? string.Empty).Trim(), out int value) ? value : 0;

    // ---- overlay behaviour ----------------------------------------------------------------------

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
            // Escape is Cancel: same path as the button so the two can't drift.
            cancelButton.TriggerClick();
            return true;
        }

        return base.OnKeyDown(e);
    }

    // ---- test seams (JukeBox.Game.Tests has InternalsVisibleTo) --------------------------------

    internal BasicDropdown<RenderPreset> PresetDropdown => presetDropdown;
    internal BasicDropdown<string> FormatDropdown => formatDropdown;
    internal AccentTextBox ResolutionBox => resolutionBox;
    internal AccentTextBox FpsBox => fpsBox;
    internal AccentTextBox PathBox => pathBox;
    internal AccentTextBox StartBox => startBox;
    internal AccentTextBox EndBox => endBox;
    internal AccentTextBox AudioBox => audioBox;
    internal TextButton RenderButton => renderButton;
    internal TextButton CancelButton => cancelButton;
    internal IReadOnlyDictionary<RenderField, SpriteText> ErrorTexts => errorTexts;
}
