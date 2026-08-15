#nullable enable

using System;
using osu.Framework.Input.Events;

namespace JukeBox.Game.UI;

/// <summary>
/// The keyword text box in <see cref="FullscreenListingOverlay"/>. The base
/// <see cref="osu.Framework.Graphics.UserInterface.TextBox"/> consumes the first Escape itself
/// (killing only its own focus), which would force a second press to actually close the listing —
/// redirecting Escape to <see cref="Exit"/> makes a single press close it, matching the "Esc
/// closes" contract.
/// </summary>
internal partial class ListingSearchBox : AccentTextBox
{
    public Action? Exit;

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
}
