#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JukeBox.Game.Replays;
using osu.Game.Beatmaps;
using osu.Game.Models;
using osu.Game.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Replays;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osuTK;

namespace JukeBox.Game.Tests.Import
{
    /// <summary>
    /// Builds GENUINE .osr files through lazer's own <see cref="LegacyScoreEncoder"/> — the exact
    /// writer osu! stable's format is defined by, LZMA-compressed frames included — so
    /// <see cref="OsrReader"/> and <see cref="JukeBoxScoreDecoder"/> are exercised against real
    /// bytes rather than against a hand-rolled approximation of them that could drift into
    /// agreeing with a wrong parser.
    /// </summary>
    public static class ReplayFixture
    {
        /// <param name="path">Where to write the .osr.</param>
        /// <param name="beatmapPath">The .osu the replay claims to have been played on; its file
        /// MD5 becomes the replay's beatmap checksum, exactly as osu! records it.</param>
        /// <param name="playerName">Recorded player name.</param>
        /// <param name="frameCount">How many cursor frames to record.</param>
        public static void Write(string path, string beatmapPath, string playerName, int frameCount = 8)
        {
            var beatmap = new FlatWorkingBeatmap(beatmapPath);
            var ruleset = new OsuRuleset();

            var frames = new List<ReplayFrame>();

            for (int i = 0; i < frameCount; i++)
                frames.Add(new OsuReplayFrame(i * 100, new Vector2(64 + i * 8, 96 + i * 4)));

            var score = new Score
            {
                Replay = new Replay { Frames = frames },
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    BeatmapInfo = new BeatmapInfo { MD5Hash = Md5OfFile(beatmapPath) },
                    RealmUser = new RealmUser { Username = playerName },
                    Date = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero),
                    TotalScore = 123456,
                    MaxCombo = 321,
                    Accuracy = 0.98,
                },
            };

            using var stream = File.Create(path);
            new LegacyScoreEncoder(score, beatmap.Beatmap).Encode(stream, leaveOpen: true);
        }

        /// <summary>
        /// A replay that actually PLAYS the map: it presses on each hit object in turn, missing
        /// exactly the ones named in <paramref name="missAtIndices"/>.
        ///
        /// <para>
        /// <see cref="Write"/> records eight cursor frames near the top-left and never presses a
        /// button, so every object it is pointed at MISSES. That is fine for testing the importer,
        /// which only reads the header — but it is silently useless for testing anything about
        /// SCORING, because score, combo and accuracy all sit at zero for the whole play while
        /// judged-hit counts still climb. A test asserting "the numbers are live" passes against it
        /// without a single number ever moving, which is exactly what happened.
        /// </para>
        ///
        /// <para>
        /// The misses are the point of the parameter: knockout is a rule about WHEN someone breaks
        /// combo, so replays that break at different times are the only fixtures that can tell a
        /// working rule from a broken one.
        /// </para>
        /// </summary>
        /// <param name="path">Where to write the .osr.</param>
        /// <param name="beatmapPath">The .osu to play; its hit objects are read back out of it so
        /// the frames land on the real object times and positions.</param>
        /// <param name="playerName">Recorded player name.</param>
        /// <param name="missAtIndices">Zero-based indices of the objects this player misses, by
        /// simply never pressing on them.</param>
        public static void WriteHitting(string path, string beatmapPath, string playerName, params int[] missAtIndices)
            => WriteHitting(path, beatmapPath, playerName, Vector2.Zero, missAtIndices);

        /// <summary>
        /// As <see cref="WriteHitting(string,string,string,int[])"/>, but with this player's cursor
        /// sitting <paramref name="cursorOffset"/> away from the centre of each object.
        ///
        /// <para>
        /// Without an offset every fixture player traces the SAME path, so their cursors land
        /// exactly on top of one another. That is harmless for scoring and useless for anything
        /// about cursors: "each player has their own" cannot be seen, by eye or in a screenshot,
        /// when they are all in the same pixel. Keep it well inside the hit radius, or the player
        /// starts missing objects they were meant to hit.
        /// </para>
        /// </summary>
        public static void WriteHitting(string path, string beatmapPath, string playerName, Vector2 cursorOffset, params int[] missAtIndices)
            => WriteHitting(path, beatmapPath, playerName, cursorOffset, Array.Empty<Mod>(), missAtIndices);

        /// <summary>
        /// As above, recorded as having been played with <paramref name="mods"/>.
        ///
        /// <para>
        /// Written into the .osr through the real encoder rather than set on the decoded Score
        /// afterwards, because mods round-trip through the file's own legacy flags: assigning them
        /// to an already-decoded score does not survive into what gameplay reads.
        /// </para>
        /// </summary>
        public static void WriteHitting(string path, string beatmapPath, string playerName, Vector2 cursorOffset, IReadOnlyList<Mod> mods, params int[] missAtIndices)
        {
            var beatmap = new FlatWorkingBeatmap(beatmapPath);
            var misses = new HashSet<int>(missAtIndices);
            var objects = HitObjectsIn(beatmapPath);

            var frames = new List<ReplayFrame>();

            for (int i = 0; i < objects.Count; i++)
            {
                var (time, centre) = objects[i];
                var position = centre + cursorOffset;

                // Approach with the button up, press ON the object, hold briefly, release. A miss
                // is the same movement with the press left out — the cursor is in the right place
                // and the player simply does not click, which is what a real miss looks like.
                frames.Add(new OsuReplayFrame(time - 40, position));

                if (!misses.Contains(i))
                {
                    frames.Add(new OsuReplayFrame(time, position, OsuAction.LeftButton));
                    frames.Add(new OsuReplayFrame(time + 30, position, OsuAction.LeftButton));
                }

                frames.Add(new OsuReplayFrame(time + 60, position));
            }

            var score = new Score
            {
                Replay = new Replay { Frames = frames },
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = new OsuRuleset().RulesetInfo,
                    BeatmapInfo = new BeatmapInfo { MD5Hash = Md5OfFile(beatmapPath) },
                    RealmUser = new RealmUser { Username = playerName },
                    Date = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero),
                    TotalScore = 1,
                    MaxCombo = 1,
                    Accuracy = 1,
                    Mods = mods.ToArray(),
                },
            };

            using var stream = File.Create(path);
            new LegacyScoreEncoder(score, beatmap.Beatmap).Encode(stream, leaveOpen: true);
        }

        /// <summary>
        /// The (time, position) of every hit object in a fixture .osu, read straight out of the
        /// [HitObjects] section. Deliberately a dumb line reader rather than a decode: fixture maps
        /// here are circles only, and the replay has to land on the same coordinates the file
        /// states rather than on whatever a conversion produced.
        /// </summary>
        public static IReadOnlyList<(double Time, Vector2 Position)> HitObjectsIn(string beatmapPath)
        {
            var objects = new List<(double, Vector2)>();
            bool inSection = false;

            foreach (string raw in File.ReadLines(beatmapPath))
            {
                string line = raw.Trim();

                if (line.StartsWith('['))
                {
                    inSection = line == "[HitObjects]";
                    continue;
                }

                if (!inSection || line.Length == 0)
                    continue;

                string[] parts = line.Split(',');

                if (parts.Length < 3
                    || !float.TryParse(parts[0], out float x)
                    || !float.TryParse(parts[1], out float y)
                    || !double.TryParse(parts[2], out double time))
                {
                    continue;
                }

                objects.Add((time, new Vector2(x, y)));
            }

            return objects;
        }

        public static string Md5OfFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(stream));
        }
    }
}
