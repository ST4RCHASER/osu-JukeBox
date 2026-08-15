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
/// Game-lifetime service holding one <see cref="BindableBool"/> per <see cref="PlayfieldElement"/>
/// ("is this element shown?") and persisting the off ones into
/// <see cref="JukeBoxSetting.HiddenPlayfieldElements"/>.
///
/// <para>
/// The bindables are the UI's model (the Chart tab binds its checkboxes straight to them) and the
/// renderer's input: <see cref="PlayfieldElementFilter"/> reads <see cref="IsHidden"/> on every skin
/// lookup and re-runs the lookups whenever <see cref="Revision"/> changes, so a toggle applies to
/// the chart already on screen without a rebuild or a song change.
/// </para>
///
/// <para>
/// One config key holds the whole set rather than one key per element: elements are a list that
/// grows (a new ruleset component upstream, a finer split of an existing entry), and a list-shaped
/// setting absorbs that without either churning <see cref="JukeBoxSetting"/> or needing a migration
/// for every addition. Persisting the HIDDEN ones (not the shown ones) is what makes anything added
/// later default to visible.
/// </para>
/// </summary>
public partial class PlayfieldElementVisibility : Component
{
    private readonly Dictionary<PlayfieldElement, BindableBool> shown =
        PlayfieldElementCatalog.All.ToDictionary(e => e.Element, _ => new BindableBool(true));

    private readonly Bindable<string> hiddenList = new Bindable<string>(string.Empty);

    /// <summary>
    /// Bumped on every change to any element's visibility. Consumers that must re-evaluate the
    /// whole set (the skin filter) watch this instead of subscribing to forty bindables.
    /// </summary>
    public IBindable<int> Revision => revision;

    private readonly Bindable<int> revision = new Bindable<int>();

    /// <summary>Guards the config→bindables direction from being echoed straight back.</summary>
    private bool applyingFromConfig;

    [Resolved(canBeNull: true)]
    private JukeBoxConfigManager? config { get; set; }

    /// <summary>Whether <paramref name="element"/> should be drawn.</summary>
    public BindableBool Shown(PlayfieldElement element) => shown[element];

    public bool IsHidden(PlayfieldElement element) => !shown[element].Value;

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (config != null)
            config.BindWith(JukeBoxSetting.HiddenPlayfieldElements, hiddenList);

        hiddenList.BindValueChanged(e => applyFromConfig(e.NewValue), true);

        foreach (var (element, bindable) in shown)
        {
            var captured = element;

            bindable.BindValueChanged(_ =>
            {
                revision.Value++;

                if (!applyingFromConfig)
                    writeToConfig();

                osu.Framework.Logging.Logger.Log(
                    $"[playfield] {captured} {(shown[captured].Value ? "shown" : "hidden")}");
            });
        }
    }

    /// <summary>
    /// Names that don't parse are dropped silently — a config written by a newer build (or hand-
    /// edited) must not stop the rest of the list from applying.
    /// </summary>
    private void applyFromConfig(string value)
    {
        var hidden = new HashSet<PlayfieldElement>();

        foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse(name, out PlayfieldElement element) && shown.ContainsKey(element))
                hidden.Add(element);
        }

        applyingFromConfig = true;

        foreach (var (element, bindable) in shown)
            bindable.Value = !hidden.Contains(element);

        applyingFromConfig = false;
    }

    private void writeToConfig()
    {
        // Written in catalog order, so the persisted string is stable rather than dictionary-order
        // dependent (which would rewrite the config on every toggle for no reason).
        hiddenList.Value = string.Join(',', PlayfieldElementCatalog.All
                                                                   .Where(e => IsHidden(e.Element))
                                                                   .Select(e => e.Element.ToString()));
    }
}
