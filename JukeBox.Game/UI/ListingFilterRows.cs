#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// Builds the labelled chip rows shared by both listing presentations (the docked
/// <see cref="BeatmapListingOverlay"/> and the fullscreen <see cref="FullscreenListingOverlay"/>).
/// Chip groups are BINDABLE-driven — each group both writes to and follows its
/// <see cref="Online.BeatmapSearchEngine"/> bindable — so the two views' chips stay in sync
/// automatically (changing a filter in one presentation updates the other's chips), and neither
/// view carries its own filter state.
/// </summary>
internal static class ListingFilterRows
{
    /// <summary>
    /// Builds a mutually-exclusive chip group over <paramref name="current"/>: clicking a chip
    /// writes its value to the bindable, and every chip's Active state follows the bindable (so an
    /// external change — e.g. the same filter flipped in the other presentation — re-highlights
    /// the right chip here too). Clicking the already-active chip is a no-op, so a search is never
    /// restarted for an unchanged value.
    /// </summary>
    public static FilterChip[] SingleSelect<T>((string text, T value)[] options, Bindable<T> current)
    {
        var chips = options.Select(o => new FilterChip(o.text)).ToArray();

        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            chips[i].Action = () => current.Value = options[index].value;
        }

        current.BindValueChanged(e =>
        {
            for (int i = 0; i < options.Length; i++)
                chips[i].Active.Value = EqualityComparer<T>.Default.Equals(options[i].value, e.NewValue);
        }, true);

        return chips;
    }

    /// <summary>An independently-toggleable chip (the multi-select Extra row) following
    /// <paramref name="current"/> both ways, same as <see cref="SingleSelect{T}"/>.</summary>
    public static FilterChip Toggle(string text, BindableBool current)
    {
        var chip = new FilterChip(text);
        chip.Action = () => current.Value = !current.Value;
        current.BindValueChanged(e => chip.Active.Value = e.NewValue, true);
        return chip;
    }

    public static Drawable CreateChipRow(string label, string? note, float labelWidth, Drawable[] chips)
    {
        var row = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new Vector2(6, 6),
        };

        row.Add(CreateRowLabel(label, labelWidth));

        foreach (var chip in chips)
            row.Add(chip);

        if (note != null)
        {
            row.Add(new SpriteText
            {
                Text = $"({note})",
                Font = FontUsage.Default.With(size: Theme.CaptionTextSize),
                Colour = Theme.TextTertiary,
                Margin = new MarginPadding { Left = 4, Top = 4 },
            });
        }

        return row;
    }

    public static Drawable CreateRowLabel(string label, float labelWidth) => new Container
    {
        Width = labelWidth,
        AutoSizeAxes = Axes.Y,
        Child = new SpriteText
        {
            Text = label,
            Font = FontUsage.Default.With(size: Theme.RowSecondaryTextSize),
            Colour = Theme.TextSecondary,
            Margin = new MarginPadding { Top = 3 },
        },
    };
}

/// <summary>
/// The keyword text box used by both listing presentations. The base
/// <see cref="osu.Framework.Graphics.UserInterface.TextBox"/> consumes the first Escape itself
/// (killing only its own focus), which would force a second press to actually close the listing —
/// redirecting Escape to <see cref="Exit"/> makes a single press close it, matching the "Esc
/// closes" contract. <see cref="Focused"/> lets the owning view surface focus as an event (the
/// fullscreen search style opens its overlay when the docked box is focused).
/// </summary>
internal partial class ListingSearchBox : AccentTextBox
{
    public Action? Exit;
    public Action? Focused;

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == osuTK.Input.Key.Escape)
        {
            if (!e.Repeat)
                Exit?.Invoke();
            return true;
        }

        return base.OnKeyDown(e);
    }

    protected override void OnFocus(FocusEvent e)
    {
        base.OnFocus(e);
        Focused?.Invoke();
    }
}
