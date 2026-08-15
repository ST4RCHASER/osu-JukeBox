#nullable enable

using osu.Framework.Graphics;
using osu.Game.Rulesets.Osu.Skinning.Default;
using osu.Game.Rulesets.Osu.UI.Cursor;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// A real <c>PlaySliderBody</c> that simply never draws — what
/// <see cref="PlayfieldElementFilter"/> hands back for a hidden
/// <see cref="PlayfieldElement.OsuSliderBody"/>, because <c>DrawableSlider</c> casts the skin's
/// answer to that type to drive the path's progress and accent colour (see
/// <see cref="PlayfieldElementCatalog.Entry.CreateHidden"/>). Its own <see cref="Drawable.Alpha"/>
/// is what hides it: <c>DrawableSlider</c> fades the <c>SkinnableDrawable</c> WRAPPER in and out
/// over the object's lifetime, never this drawable, so the zero here is never written over.
///
/// <para>
/// Top-level rather than nested inside <see cref="PlayfieldElementCatalog"/>: it is a
/// <see cref="Drawable"/>, so osu!framework's source generator wants to emit its dependency-
/// injection activation, and it can only do that when every containing type is <c>partial</c> too.
/// Nested in a static catalogue class it silently fell back to the reflection-based activation
/// path (and warned, OFSG001). Living at namespace level, it has no containing type to make
/// partial and the generated path is used.
/// </para>
/// </summary>
internal partial class HiddenSliderBody : PlaySliderBody
{
    public HiddenSliderBody()
    {
        Alpha = 0;
    }
}

/// <summary>The same trick, and the same top-level placement for the same reason, for the osu!
/// cursor — whose container casts the skin's answer to <see cref="SkinnableCursor"/> to drive its
/// expand/contract on click.</summary>
internal partial class HiddenCursor : SkinnableCursor
{
    public HiddenCursor()
    {
        Alpha = 0;
    }
}
