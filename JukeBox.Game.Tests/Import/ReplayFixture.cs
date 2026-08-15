#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using JukeBox.Game.Replays;
using osu.Game.Beatmaps;
using osu.Game.Models;
using osu.Game.Replays;
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

        public static string Md5OfFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(stream));
        }
    }
}
