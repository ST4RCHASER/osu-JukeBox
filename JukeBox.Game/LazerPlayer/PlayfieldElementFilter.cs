#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Game.Audio;
using osu.Game.Skinning;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// The highest-priority link of the chart's skin chain: an otherwise transparent
/// <see cref="SkinProvidingContainer"/> whose single source answers "nothing" for exactly those
/// component lookups belonging to a <see cref="PlayfieldElement"/> the user has switched off, and
/// <c>null</c> for everything else — which falls straight through to the real skin chain
/// (beatmap skin → user skin → classic → ruleset resources) below it.
///
/// <para>
/// Hiding through the skin lookup rather than through the drawable tree is what makes this work at
/// all: playfield pieces are created lazily and recycled by lazer's object pools, so an
/// alpha/expiry pass over <c>ChildrenOfType</c> would have to be re-run for every note that enters
/// the pool. Every one of those pieces is a <see cref="SkinnableDrawable"/> that asks the nearest
/// <see cref="ISkinSource"/> ancestor for its content and re-asks on
/// <see cref="ISkinSource.SourceChanged"/> — so sitting in that chain covers objects that don't
/// exist yet, and <see cref="SkinProvidingContainer.TriggerSourceChanged"/> covers the ones already
/// on screen. That is the same mechanism the three "Beatmap ..." settings already use (see
/// <c>LazerChartLayer.BeatmapSkinGate</c>), just deciding per component instead of per source.
/// </para>
///
/// <para>
/// Returning an empty drawable (rather than declining the lookup) is what actually suppresses the
/// element: declining would only skip THIS source and let the chain below supply the real piece.
/// An arbitrary drawable is a legal answer from any skin — that is the contract every
/// <see cref="SkinnableDrawable"/> consumer is written against — so an empty container is simply a
/// skin that draws nothing there.
/// </para>
/// </summary>
public partial class PlayfieldElementFilter : SkinProvidingContainer
{
    private readonly PlayfieldElementVisibility visibility;
    private readonly HidingSkin filter;

    private readonly IBindable<int> revision = new Bindable<int>();

    /// <summary>
    /// Test hook: the elements whose lookups the hosted ruleset has actually performed, whether or
    /// not they were hidden. Proves an entry in <see cref="PlayfieldElementCatalog"/> is wired to a
    /// component that ruleset really asks for, rather than to a name nobody looks up.
    /// </summary>
    internal IReadOnlyCollection<PlayfieldElement> ElementsLookedUp => filter.LookedUpElements;

    /// <summary>Test hook: the elements that have actually been suppressed at least once.</summary>
    internal IReadOnlyCollection<PlayfieldElement> SuppressedElementsSeen => filter.SuppressedElements;

    /// <summary>Test hook: how many lookups have been suppressed in total.</summary>
    internal int SuppressedLookups => filter.SuppressedLookups;

    public PlayfieldElementFilter(PlayfieldElementVisibility visibility, IReadOnlyCollection<PlayfieldElement>? alwaysHidden = null)
    {
        this.visibility = visibility;

        RelativeSizeAxes = Axes.Both;

        SetSources(new ISkin[] { filter = new HidingSkin(visibility, alwaysHidden) });
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        revision.BindTo(visibility.Revision);

        // Re-runs every SkinnableDrawable's lookup, so a toggle applies to the chart already
        // playing — no rebuild, no song change.
        revision.BindValueChanged(_ => TriggerSourceChanged());
    }

    /// <summary>
    /// The filter itself. Only <see cref="GetDrawableComponent"/> ever answers: textures, samples
    /// and configuration are none of its business and fall through untouched, so hiding an element
    /// changes what is DRAWN without silencing its hitsounds or altering skin configuration
    /// (combo colours, mania stage metrics) that the rest of the playfield is laid out from.
    /// </summary>
    private class HidingSkin : ISkin
    {
        private readonly PlayfieldElementVisibility visibility;
        private readonly IReadOnlyCollection<PlayfieldElement> alwaysHidden;
        private readonly HashSet<PlayfieldElement> lookedUp = new HashSet<PlayfieldElement>();
        private readonly HashSet<PlayfieldElement> suppressed = new HashSet<PlayfieldElement>();

        public IReadOnlyCollection<PlayfieldElement> LookedUpElements => lookedUp;
        public IReadOnlyCollection<PlayfieldElement> SuppressedElements => suppressed;
        public int SuppressedLookups { get; private set; }

        public HidingSkin(PlayfieldElementVisibility visibility, IReadOnlyCollection<PlayfieldElement>? alwaysHidden)
        {
            this.visibility = visibility;
            this.alwaysHidden = alwaysHidden ?? System.Array.Empty<PlayfieldElement>();
        }

        public Drawable? GetDrawableComponent(ISkinComponentLookup lookup)
        {
            foreach (var entry in PlayfieldElementCatalog.All)
            {
                if (!entry.Matches(lookup))
                    continue;

                lookedUp.Add(entry.Element);

                if (!alwaysHidden.Contains(entry.Element) && !visibility.IsHidden(entry.Element))
                    continue;

                suppressed.Add(entry.Element);
                SuppressedLookups++;

                return entry.CreateHidden();
            }

            return null;
        }

        public Texture? GetTexture(string componentName, WrapMode wrapModeS, WrapMode wrapModeT) => null;

        public ISample? GetSample(ISampleInfo sampleInfo) => null;

        public IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup)
            where TLookup : notnull
            where TValue : notnull
            => null;
    }
}
