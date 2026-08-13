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
    /// Whether our own flat background sprite should hide beneath this layer: when the storyboard
    /// explicitly draws the beatmap background as one of its own sprites
    /// (<see cref="Storyboard.ReplacesBackground"/>), or when a (working) video plays fullscreen
    /// behind it — matching osu!'s behaviour. Plain sprite storyboards leave the background
    /// visible underneath, as mappers intend.
    /// </summary>
    public bool ShouldHideBackground => storyboard.ReplacesBackground || (HasVideo && !VideoFaulted);

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
        private static readonly string[] guess_extensions = { ".png", ".jpg", ".jpeg" };

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
                foreach (string ext in guess_extensions)
                {
                    if (files.TryGetValue(key + ext, out path))
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
