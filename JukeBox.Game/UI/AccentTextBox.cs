#nullable enable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;

namespace JukeBox.Game.UI;

/// <summary>
/// A <see cref="BasicTextBox"/> restyled to the app's design system: rounded elevated-surface
/// background, tertiary-coloured placeholder and an accent border while focused. Used for the
/// search box and the map-ID input.
/// </summary>
internal partial class AccentTextBox : BasicTextBox
{
    public AccentTextBox()
    {
        Masking = true;
        CornerRadius = Theme.CornerRadius;

        BackgroundUnfocused = Theme.ElevatedSurface;
        BackgroundFocused = Theme.ElevatedSurface;
    }

    protected override SpriteText CreatePlaceholder() => new FadingPlaceholderText
    {
        Colour = Theme.TextTertiary,
        Font = FontUsage.Default.With(size: Theme.RowTitleTextSize),
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        X = CaretWidth,
    };

    protected override Caret CreateCaret() => new BasicCaret
    {
        CaretWidth = CaretWidth,
        SelectionColour = Theme.Accent,
    };

    protected override void OnFocus(FocusEvent e)
    {
        base.OnFocus(e);
        BorderThickness = 2;
        BorderColour = Theme.Accent;
    }

    protected override void OnFocusLost(FocusLostEvent e)
    {
        base.OnFocusLost(e);
        BorderThickness = 0;
    }
}
