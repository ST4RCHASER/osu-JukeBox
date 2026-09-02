#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JukeBox.Game.Beatmaps;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Video;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Skinning;
using osu.Game.Storyboards;
using osu.Game.Storyboards.Drawables;

namespace JukeBox.Game.LazerPlayer;

/// <summary>
/// Hosts osu!lazer's REAL storyboard renderer (<see cref="DrawableStoryboard"/>) for one beatmap
/// set, replacing the old ReOsuStoryboardPlayer-based transform compiler. Lazer's decoder handles
/// the .osu/.osb merge, variables, layers, loops and triggers; its drawable handles sprites,
/// animations, storyboard Sample audio events (keysounds resolve through the beatmap-folder skin)
/// and storyboard Video events — all driven off the inherited (shared playback) clock.
/// </summary>
public partial class LazerStoryboardLayer : CompositeDrawable, IBeatSyncProvider
{
    private readonly CachedBeatmapSet set;
    private readonly string? osuFile;

    // IBeatSyncProvider (auto-cached to children via the interface's [Cached] attribute):
    // beat-synced storyboard pieces (DrawableStoryboardAnimation's beatmap-synced frame timing)
    // beat off the decoded beatmap's control points and our inherited playback clock. No audio
    // amplitudes — track playback lives outside lazer.
    ControlPointInfo? IBeatSyncProvider.ControlPoints => storyboard.Beatmap.ControlPointInfo;
    IClock IBeatSyncProvider.Clock => Clock;
    ChannelAmplitudes IHasAmplitudes.CurrentAmplitudes => ChannelAmplitudes.Empty;

    private Storyboard storyboard = new Storyboard();
    private BeatmapFolderSkin? folderSkin;
    private DrawableStoryboard? drawableStoryboard;

    /// <summary>The decoded lazer storyboard model (empty storyboard when decode failed/absent).</summary>
    internal Storyboard Storyboard => storyboard;

    /// <summary>
    /// lazer's name for the layer the storyboard's video event lives in. The video is not a special
    /// case in the storyboard model — it is one more layer alongside Background/Fail/Pass/
    /// Foreground/Overlay — which is exactly why "video without storyboard" (and the reverse) is a
    /// matter of switching layers rather than of building a different tree.
    /// </summary>
    internal const string video_layer_name = "Video";

    [Resolved(canBeNull: true)]
    private StoryboardLayerVisibility? layerVisibility { get; set; }

    /// <summary>
    /// Whether the storyboard's non-video layers are drawn. Set by <see cref="Screens.BeatmapVisuals"/>
    /// from <see cref="Configuration.JukeBoxSetting.ShowStoryboard"/>; the video half is <see cref="VideoShown"/>.
    /// </summary>
    public readonly BindableBool StoryboardShown = new BindableBool(true);

    /// <summary>Whether the storyboard's video layer is drawn (<see cref="Configuration.JukeBoxSetting.ShowVideo"/>).</summary>
    public readonly BindableBool VideoShown = new BindableBool(true);

    /// <summary>Test hook: the alpha the layer lazer calls <paramref name="name"/> is currently
    /// drawn at, or null before the storyboard's own async load has built its layers.</summary>
    internal float? LayerAlpha(string name)
        => drawableStoryboard?.Children.FirstOrDefault(l => l.Layer.Name == name)?.Alpha;

    /// <summary>Whether the storyboard has anything drawable at all (parity with the old layer's HasObjects).</summary>
    public bool HasObjects => storyboard.HasDrawable;

    /// <summary>Total decoded element count across layers (test hook).</summary>
    internal int ElementCount => storyboard.Layers.Sum(l => l.Elements.Count);

    /// <summary>Whether the storyboard carries a Video event.</summary>
    public bool HasVideo => storyboard.PrimaryVideo != null;

    /// <summary>
    /// Whether the video's decoder has faulted (corrupt/unsupported file — surfaces asynchronously
    /// on the decoder thread). A faulted video renders nothing, so background auto-hide must stop
    /// counting it.
    /// </summary>
    public bool VideoFaulted => HasVideo && this.ChildrenOfType<Video>().Any(v => v.IsFaulted);

    /// <summary>
    /// Whether the storyboard declares a video whose FILE never resolved. Lazer's
    /// <c>DrawableStoryboardVideo</c> asks the resource store for the referenced path and, on a null
    /// stream, simply adds no child at all — so a video that isn't there produces no
    /// <see cref="Video"/> drawable, and therefore never becomes <see cref="VideoFaulted"/> either.
    ///
    /// <para>
    /// That gap is what made set 683417 a black screen: its [Events] references a ".avi" while the
    /// .osz ships the ".mp4" osu! re-encoded it to, so nothing loaded, nothing faulted, and the
    /// background was hidden for a video that was never going to draw. The resource store now
    /// resolves across that mismatch, but this stays as the backstop for a video that is genuinely
    /// absent or unreadable — the background must come back rather than leaving the user on black.
    /// </para>
    ///
    /// <para>
    /// Gated on the storyboard having LOADED: its children are built asynchronously, so before that
    /// there are legitimately no <see cref="Video"/> drawables yet and this would otherwise report a
    /// missing video for every set for a frame or two.
    /// </para>
    /// </summary>
    public bool VideoMissing => HasVideo && drawableStoryboard?.IsLoaded == true && !this.ChildrenOfType<Video>().Any();

    /// <summary>Whether the declared video is actually going to put pixels on screen.</summary>
    public bool VideoPlayable => HasVideo && !VideoFaulted && !VideoMissing;

    /// <summary>Whether the storyboard draws the beatmap's own background as one of its sprites, in
    /// which case osu! hides the flat background beneath it.</summary>
    public bool ReplacesBackground => storyboard.ReplacesBackground;

    /// <summary>
    /// Whether our own flat background sprite should hide beneath this layer: when the storyboard
    /// explicitly draws the beatmap background as one of its own sprites
    /// (<see cref="Storyboard.ReplacesBackground"/>), or when a video that can actually play covers
    /// it fullscreen — matching osu!'s behaviour. Plain sprite storyboards leave the background
    /// visible underneath, as mappers intend.
    ///
    /// <para>
    /// Each half is conditioned on the half of the display that would actually cover the
    /// background, since those switch independently now (see <see cref="StoryboardShown"/> /
    /// <see cref="VideoShown"/>): a hidden storyboard replaces nothing, and a hidden video covers
    /// nothing. Note the storyboard half deliberately asks only the MASTER toggle, not the
    /// per-layer ones — <c>ReplacesBackground</c> is a property of the storyboard as a whole, and
    /// second-guessing which layer the replacing sprite sits in would trade a correct answer for a
    /// guess.
    /// </para>
    /// </summary>
    public bool ShouldHideBackground
        => (StoryboardShown.Value && ReplacesBackground) || (VideoShown.Value && VideoPlayable);

    /// <summary>Test hook: frames the storyboard video has actually rendered in sync, or null with no video.</summary>
    internal int? VideoFramesProcessed => HasVideo ? this.ChildrenOfType<Video>().FirstOrDefault()?.FramesProcessed : null;

    public LazerStoryboardLayer(CachedBeatmapSet set, string? osuFile = null)
    {
        this.set = set;
        this.osuFile = osuFile ?? set.PreferredOsuFile;

        RelativeSizeAxes = Axes.Both;
    }

    [BackgroundDependencyLoader]
    private void load(GameHost host, AudioManager audio, IStorageResourceProvider resourceProvider)
    {
        string? primary = osuFile ?? set.OsbFile;

        if (primary == null)
            return;

        try
        {
            storyboard = DecodeStoryboard(osuFile, set.OsbFile);
        }
        catch (Exception e)
        {
            // A malformed .osb/.osu (radio downloads arbitrary community content) must never take
            // the whole visual stack down — fall back to an empty storyboard, loudly.
            Logger.Error(e, $"Failed to decode storyboard for set {set.SetId}; falling back to empty storyboard");
            storyboard = new Storyboard();
            return;
        }

        Logger.Log($"Storyboard for set {set.SetId}: {ElementCount} element(s), video: {HasVideo}, replaces background: {storyboard.ReplacesBackground}");

        if (!storyboard.HasDrawable && storyboard.PrimaryVideo == null)
            return;

        // Storyboard Sample events resolve through ISkinSource (lazer's PausableSkinnableSound
        // chain) — the beatmap folder skin serves them straight from the map directory.
        try
        {
            folderSkin = new BeatmapFolderSkin(primary, resourceProvider, host);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"Failed to open beatmap folder skin for set {set.SetId} — storyboard samples unavailable");
        }

        InternalChild = new SkinProvidingContainer(folderSkin)
        {
            RelativeSizeAxes = Axes.Both,
            Child = drawableStoryboard = new FolderBackedDrawableStoryboard(storyboard, set.Directory, host)
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            },
        };
    }

    /// <summary>
    /// Decodes the storyboard exactly the way lazer's WorkingBeatmapCache does: the difficulty's
    /// own [Events] as the primary stream, the standalone .osb merged in as a secondary stream
    /// (the decoder itself owns variables/layers/ordering), and the decoded beatmap attached so
    /// widescreen sizing and <see cref="Storyboard.ReplacesBackground"/> work.
    /// Throws on malformed content — callers decide the fallback.
    /// </summary>
    internal static Storyboard DecodeStoryboard(string? osuFile, string? osbFile)
    {
        string primary = osuFile ?? osbFile ?? throw new ArgumentException("need at least one of .osu/.osb");

        Storyboard storyboard;

        using (var stream = File.OpenRead(primary))
        using (var reader = new LineBufferedReader(stream))
        {
            var decoder = Decoder.GetDecoder<Storyboard>(reader);

            if (osuFile != null && osbFile != null)
            {
                using (var secondaryStream = File.OpenRead(osbFile))
                using (var secondaryReader = new LineBufferedReader(secondaryStream))
                    storyboard = decoder.Decode(reader, secondaryReader);
            }
            else
                storyboard = decoder.Decode(reader);
        }

        if (osuFile != null)
        {
            var working = new osu.Game.Beatmaps.FlatWorkingBeatmap(osuFile);
            storyboard.Beatmap = working.Beatmap;
            storyboard.BeatmapInfo = working.Beatmap.BeatmapInfo;
        }

        return storyboard;
    }

    /// <summary>
    /// Applies the two master toggles and the per-layer choices to lazer's own layer drawables.
    ///
    /// <para>
    /// Every frame, and by ALPHA rather than by lazer's own <c>DrawableStoryboardLayer.Enabled</c>
    /// flag: that flag is <c>DrawableStoryboard</c>'s own bookkeeping, rewritten from each layer's
    /// <c>VisibleWhenPassing</c>/<c>VisibleWhenFailing</c> whenever the pass/fail state changes, so
    /// a value written there is not ours to keep. Alpha is untouched by lazer and reaches the same
    /// end: a zero-alpha layer is not present, so it is neither drawn nor updated. Re-showing one
    /// is exact rather than resumed-from-stale, since storyboard elements are transform-driven off
    /// absolute times. Per frame rather than on a change callback because assigning an unchanged
    /// alpha is a no-op in osu!framework, and the storyboard's layers only exist after its own
    /// async load — there is no single moment at which "apply once" would be correct.
    /// </para>
    /// </summary>
    protected override void Update()
    {
        base.Update();

        if (drawableStoryboard == null)
            return;

        foreach (var layer in drawableStoryboard.Children)
        {
            bool shown = layer.Layer.Name == video_layer_name
                ? VideoShown.Value
                : StoryboardShown.Value && layerVisibility?.IsLayerShown(layer.Layer.Name) != false;

            layer.Alpha = shown ? 1 : 0;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        folderSkin?.Dispose();
    }

    /// <summary>
    /// <see cref="DrawableStoryboard"/> with its realm-backed resource lookup swapped for a plain
    /// beatmap-folder store (we have no realm-imported files) — the only extension point needed
    /// to run lazer's storyboard renderer standalone.
    /// </summary>
    private partial class FolderBackedDrawableStoryboard : DrawableStoryboard
    {
        private readonly string directory;
        private readonly GameHost host;

        public FolderBackedDrawableStoryboard(Storyboard storyboard, string directory, GameHost host)
            : base(storyboard)
        {
            this.directory = directory;
            this.host = host;
        }

        protected override IResourceStore<byte[]> CreateResourceLookupStore()
            => new BeatmapFolderResourceStore(directory);
    }

    /// <summary>
    /// Case-insensitive, slash-normalized lookup over the extracted beatmap folder, with the same
    /// extension guessing lazer's <see cref="Storyboard.GetStoragePathFromStoryboardPath"/> does
    /// for extension-less old-school storyboard paths. Serves textures, animations frames and the
    /// video stream.
    /// </summary>
    internal class BeatmapFolderResourceStore : IResourceStore<byte[]>
    {
        private static readonly string[] image_extensions = { ".png", ".jpg", ".jpeg" };

        /// <summary>
        /// Video containers a beatmap may reference. osu!'s submission system re-encodes uploaded
        /// videos — a map that shipped an .avi is served as .mp4 — but the .osu keeps referencing the
        /// ORIGINAL name, so the reference and the file that arrives routinely disagree by extension.
        /// (Set 683417 is one: its [Events] says "…MV.avi" and the .osz contains "…MV.mp4".)
        /// </summary>
        private static readonly string[] video_extensions =
            { ".mp4", ".avi", ".flv", ".mov", ".mpg", ".mpeg", ".wmv", ".m4v", ".mkv", ".webm" };

        private readonly string directory;
        private readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public BeatmapFolderResourceStore(string directory)
        {
            this.directory = directory;

            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                files[normalize(Path.GetRelativePath(directory, file))] = file;
        }

        private static string normalize(string path) => path.Replace('\\', '/').Trim();

        private string? resolve(string name)
        {
            string key = normalize(name);

            if (files.TryGetValue(key, out string? path))
                return path;

            if (!Path.HasExtension(key))
            {
                foreach (string ext in image_extensions)
                {
                    if (files.TryGetValue(key + ext, out path))
                        return path;
                }

                return null;
            }

            // The reference HAS an extension but no such file arrived. Try the same base name under
            // the other extensions of the same KIND — which is how a video referenced as .avi finds
            // the .mp4 osu! actually served (see video_extensions). Deliberately kind-scoped: a
            // missing sprite must never resolve to a video, and vice versa, so a genuinely absent
            // file still reads as absent rather than silently becoming the wrong asset.
            string extension = Path.GetExtension(key);
            string[]? family = null;

            if (video_extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                family = video_extensions;
            else if (image_extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                family = image_extensions;

            if (family != null)
            {
                string withoutExtension = key.Substring(0, key.Length - extension.Length);

                foreach (string ext in family)
                {
                    if (files.TryGetValue(withoutExtension + ext, out path))
                        return path;
                }
            }

            return null;
        }

        public byte[] Get(string name)
        {
            string? path = resolve(name);
            return path == null ? null! : File.ReadAllBytes(path);
        }

        public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
            => Task.Run(() => Get(name), cancellationToken);

        public Stream? GetStream(string name)
        {
            string? path = resolve(name);
            return path == null ? null : File.OpenRead(path);
        }

        public IEnumerable<string> GetAvailableResources() => files.Keys;

        public void Dispose()
        {
        }
    }
}
