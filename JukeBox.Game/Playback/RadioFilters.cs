#nullable enable

using JukeBox.Game.Beatmaps;
using JukeBox.Game.Configuration;
using JukeBox.Game.Online;
using osu.Framework.Bindables;
using osu.Game.Overlays.BeatmapListing;

// Both namespaces declare a SearchExtra: lazer's is the listing row's multi-select value, ours is
// the single value a request carries. This file wants ours.
using SearchExtra = JukeBox.Game.Online.SearchExtra;

namespace JukeBox.Game.Playback
{
    /// <summary>
    /// The conditions the radio's random pick has to satisfy — the same filter dimensions the
    /// beatmap listing offers (mode, status, genre, language, video/storyboard, star range,
    /// featured artists), persisted so the user's idea of "my station" survives a restart.
    ///
    /// <para>
    /// A type of its own rather than a handful of bindables on <see cref="RadioService"/> because
    /// three unrelated places need the same set: the service applies it to the search, the settings
    /// panel binds controls to it, and the detached-viewer registry syncs it. It holds the config
    /// manager's own bindable COPIES in fields — <c>ConfigManager.GetBindable</c> references what it
    /// hands back only weakly, so a copy nobody keeps alive is collected and the setting silently
    /// stops propagating (the same trap <see cref="Detach.SettingsMirror"/> documents at length).
    /// </para>
    ///
    /// <para>
    /// Constructed without a config manager in tests and in bare scenes, in which case the
    /// bindables are free-standing and start at the same defaults the config declares.
    /// </para>
    /// </summary>
    public class RadioFilters
    {
        public readonly Bindable<RadioRuleset> Mode;
        public readonly Bindable<SearchCategory> Category;
        public readonly Bindable<SearchGenre> Genre;
        public readonly Bindable<SearchLanguage> Language;
        public readonly Bindable<bool> HasVideo;
        public readonly Bindable<bool> HasStoryboard;
        public readonly Bindable<double> MinStars;
        public readonly Bindable<double> MaxStars;
        public readonly Bindable<bool> FeaturedArtists;

        public RadioFilters(JukeBoxConfigManager? config = null)
        {
            Mode = config?.GetBindable<RadioRuleset>(JukeBoxSetting.RadioMode) ?? new Bindable<RadioRuleset>(RadioRuleset.Any);
            Category = config?.GetBindable<SearchCategory>(JukeBoxSetting.RadioCategory) ?? new Bindable<SearchCategory>(SearchCategory.Ranked);
            Genre = config?.GetBindable<SearchGenre>(JukeBoxSetting.RadioGenre) ?? new Bindable<SearchGenre>(SearchGenre.Any);
            Language = config?.GetBindable<SearchLanguage>(JukeBoxSetting.RadioLanguage) ?? new Bindable<SearchLanguage>(SearchLanguage.Any);
            HasVideo = config?.GetBindable<bool>(JukeBoxSetting.RadioHasVideo) ?? new Bindable<bool>();
            HasStoryboard = config?.GetBindable<bool>(JukeBoxSetting.RadioHasStoryboard) ?? new Bindable<bool>();
            MinStars = config?.GetBindable<double>(JukeBoxSetting.RadioMinStars) ?? new BindableDouble { MinValue = 0, MaxValue = 10, Precision = 0.1 };
            MaxStars = config?.GetBindable<double>(JukeBoxSetting.RadioMaxStars) ?? new BindableDouble(10) { MinValue = 0, MaxValue = 10, Precision = 0.1 };
            FeaturedArtists = config?.GetBindable<bool>(JukeBoxSetting.RadioFeaturedArtists) ?? new Bindable<bool>();
        }

        /// <summary>
        /// Writes these filters onto <paramref name="request"/>, leaving out whatever
        /// <paramref name="available"/> says the backend about to serve it cannot express.
        ///
        /// <para>
        /// Omitting rather than sending-and-hoping is the same rule the listing follows (see
        /// <see cref="BeatmapSearchEngine.BuildRequest"/>), and it matters more here: a filter a
        /// mirror can't apply makes the whole request unservable by that mirror
        /// (<see cref="SearchRequest.RequiredFilters"/>), so sending one the chain can't answer
        /// would push the radio onto its cache fallback rather than merely returning broader
        /// results.
        /// </para>
        /// </summary>
        public void Apply(SearchRequest request, SearchFilters available)
        {
            bool can(SearchFilters filter) => (available & filter) != 0;

            if (can(SearchFilters.Mode) && Mode.Value != RadioRuleset.Any)
                request.Mode = SearchVocabulary.ModeLetter((int)Mode.Value);

            // The one filter with a non-neutral default on a fresh request ("ranked"), so unlike
            // the rest it has to be actively cleared when the backend can't express it — leaving it
            // set is what would make a keyword-only mirror unable to serve the request at all.
            request.Status = can(SearchFilters.Status)
                ? SearchVocabulary.CategoryToEngine(Category.Value)
                : SearchRequest.ANY_STATUS;

            if (can(SearchFilters.Extra))
            {
                request.Extra = HasVideo.Value && HasStoryboard.Value ? SearchExtra.VideoAndStoryboard
                    : HasVideo.Value ? SearchExtra.Video
                    : HasStoryboard.Value ? SearchExtra.Storyboard
                    : SearchExtra.None;
            }

            if (can(SearchFilters.Stars))
            {
                (request.MinStars, request.MaxStars) = StarRange;
            }

            if (can(SearchFilters.Genre) && Genre.Value != SearchGenre.Any)
                request.GenreId = (int)Genre.Value;

            if (can(SearchFilters.Language) && Language.Value != SearchLanguage.Any)
                request.LanguageId = (int)Language.Value;

            if (can(SearchFilters.FeaturedArtists))
                request.FeaturedArtistsOnly = FeaturedArtists.Value;
        }

        /// <summary>
        /// The star band as a request expresses it: an untouched slider is null (asks nothing)
        /// rather than its endpoint value, and a crossed pair is un-crossed rather than sent as an
        /// inverted range — matching <see cref="BeatmapSearchEngine.BuildRequest"/> exactly.
        /// </summary>
        public (double? Min, double? Max) StarRange
        {
            get
            {
                double? min = MinStars.Value > 0 ? MinStars.Value : null;
                double? max = MaxStars.Value < 10 ? MaxStars.Value : null;

                if (min != null && max != null && min > max)
                    (min, max) = (max, min);

                return (min, max);
            }
        }

        /// <summary>
        /// Whether any of these filters can be checked against a set sitting in the local cache —
        /// i.e. whether narrowing the cache fallback is worth the disk reads at all.
        ///
        /// <para>
        /// Only the mode qualifies. A cached set is a folder of files: its <c>.osu</c> headers carry
        /// <c>Mode</c>, so "is this playable in mania?" is answerable, but nothing on disk carries a
        /// STAR RATING — osu! computes that from the hit objects rather than storing it, and neither
        /// does anything carry a genre or language, which exist only in osu-web's own tables. So the
        /// cache fallback narrows by mode and is honest about the rest (see
        /// <see cref="RadioService"/>).
        /// </para>
        /// </summary>
        public bool CanNarrowCache => Mode.Value != RadioRuleset.Any;

        /// <summary>Whether <paramref name="set"/>, already on disk, satisfies what
        /// <see cref="CanNarrowCache"/> covers.</summary>
        public bool MatchesCachedSet(CachedBeatmapSet set)
        {
            if (Mode.Value == RadioRuleset.Any)
                return true;

            // Any difficulty in the mode is enough: a set with one mania chart among four osu! ones
            // is a legitimate answer to "play me some mania".
            foreach (var difficulty in set.Difficulties)
            {
                if (difficulty.Mode == (int)Mode.Value)
                    return true;
            }

            return false;
        }
    }
}
