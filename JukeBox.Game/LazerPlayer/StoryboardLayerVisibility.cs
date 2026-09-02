#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using JukeBox.Game.Configuration;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The osu! storyboard layers, as lazer's decoder names them. The set is FIXED — every storyboard
/// has all of these, empty ones included (an empty <c>Storyboard</c> still reports Video,
/// Background, Fail, Pass, Foreground, Overlay) — so these are a complete, stable list rather than
/// a view of what the current map happens to use.
///
/// <para>
/// The video layer is deliberately absent: the storyboard's video event lives in a layer of its
/// own, and that layer is what <see cref="JukeBoxSetting.ShowVideo"/> switches — see
/// <see cref="LazerStoryboardLayer"/>. The two settings meet the same mechanism from opposite ends.
/// </para>
/// </summary>
public enum StoryboardLayerKind
{
    Background,

    /// <summary>
    /// Only ever drawn while the player is FAILING (lazer sets each layer's own
    /// <c>VisibleWhenPassing</c>/<c>VisibleWhenFailing</c> from the layer definition, and this one
    /// is failing-only). Nothing here ever fails — the chart is autoplay with no health — so this
    /// layer draws nothing whatever this toggle says. It is listed anyway because the list is the
    /// storyboard's real shape, and hiding a row would only raise the question of where it went.
    /// </summary>
    Fail,

    /// <summary>The counterpart of <see cref="Fail"/>, and the one that is always live here.</summary>
    Pass,

    Foreground,
    Overlay,
}

/// <summary>
/// Game-lifetime service holding one <see cref="BindableBool"/> per <see cref="StoryboardLayerKind"/>
/// ("is this layer drawn?") and persisting the off ones into
/// <see cref="JukeBoxSetting.HiddenStoryboardLayers"/> — the same shape (and for the same reasons)
/// as <see cref="PlayfieldElementVisibility"/>: one list-shaped key rather than one key per layer,
/// storing the HIDDEN ones so anything added later defaults to visible.
///
/// <para>
/// <see cref="LazerStoryboardLayer"/> reads this every frame and sets the matching
/// <c>DrawableStoryboardLayer</c>'s alpha, so a toggle applies to the storyboard already on screen
/// with no rebuild.
/// </para>
/// </summary>
public partial class StoryboardLayerVisibility : Component
{
    /// <summary>Every layer, in the order osu! itself stacks them (back to front).</summary>
    public static readonly StoryboardLayerKind[] All = Enum.GetValues<StoryboardLayerKind>();

    private readonly Dictionary<StoryboardLayerKind, BindableBool> shown =
        All.ToDictionary(l => l, _ => new BindableBool(true));

    private readonly Bindable<string> hiddenList = new Bindable<string>(string.Empty);

    /// <summary>Bumped on every change, for consumers that re-evaluate the whole set.</summary>
    public IBindable<int> Revision => revision;

    private readonly Bindable<int> revision = new Bindable<int>();

    /// <summary>Guards the config→bindables direction from being echoed straight back.</summary>
    private bool applyingFromConfig;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    public BindableBool Shown(StoryboardLayerKind layer) => shown[layer];

    public bool IsHidden(StoryboardLayerKind layer) => !shown[layer].Value;

    /// <summary>
    /// Whether the layer lazer calls <paramref name="name"/> should be drawn. Answered by name
    /// because that is what the decoded storyboard carries; a name we don't model (a future layer)
    /// is drawn, which is the same "new things default to visible" rule the persisted list has.
    /// </summary>
    public bool IsLayerShown(string name)
        => !Enum.TryParse(name, out StoryboardLayerKind layer) || !shown.ContainsKey(layer) || shown[layer].Value;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (config != null)
            config.BindWith(JukeBoxSetting.HiddenStoryboardLayers, hiddenList);

        hiddenList.BindValueChanged(e => applyFromConfig(e.NewValue), true);

        foreach (var (layer, bindable) in shown)
        {
            var captured = layer;

            bindable.BindValueChanged(_ =>
            {
                revision.Value++;

                if (!applyingFromConfig)
                    writeToConfig();

                osu.Framework.Logging.Logger.Log(
                    $"[storyboard] {captured} layer {(shown[captured].Value ? "shown" : "hidden")}");
            });
        }
    }

    /// <summary>Names that don't parse are dropped silently — a config written by a newer build (or
    /// hand-edited) must not stop the rest of the list from applying.</summary>
    private void applyFromConfig(string value)
    {
        var hidden = new HashSet<StoryboardLayerKind>();

        foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse(name, out StoryboardLayerKind layer) && shown.ContainsKey(layer))
                hidden.Add(layer);
        }

        applyingFromConfig = true;

        foreach (var (layer, bindable) in shown)
            bindable.Value = !hidden.Contains(layer);

        applyingFromConfig = false;
    }

    private void writeToConfig()
        => hiddenList.Value = string.Join(',', All.Where(IsHidden).Select(l => l.ToString()));
}
