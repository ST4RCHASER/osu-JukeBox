#nullable enable

using System.Collections.Generic;
using osuTK.Graphics;

namespace JukeBox.Game.UI.Result;

/// <summary>
/// One player's finished play as the result screen knows it — a flat snapshot, computed once when the
/// replay(s) reach the end and handed to the UI, never read live. Everything the osu! RANKING panel
/// shows for a single player is here and nothing else, so the panel is a pure function of this record
/// and the lead can populate it from whatever scoring source is in force (ScoreV1 for a V1 play, etc.)
/// without the panel caring where the numbers came from.
/// </summary>
/// <param name="PlayerName">Display name, drawn in <paramref name="Colour"/> at the foot of the panel.</param>
/// <param name="TotalScore">The final score — the large number at the top of the panel. Whatever
/// scoring version was chosen for this play; the panel just renders it.</param>
/// <param name="Count300">Number of 300 (great) hits — drawn in the 300 hit colour.</param>
/// <param name="Count100">Number of 100 (good) hits — drawn in the 100 hit colour.</param>
/// <param name="Count50">Number of 50 (meh) hits — drawn in the 50 hit colour.</param>
/// <param name="CountMiss">Number of misses — drawn in the miss colour.</param>
/// <param name="MaxCombo">The play's greatest combo.</param>
/// <param name="Accuracy">Accuracy as a fraction 0..1; the panel renders it as a percentage to two
/// decimal places.</param>
/// <param name="Grade">The raw rank name ("X", "S", "SH", "A", "B", "C", "D") — resolved to the active
/// skin's ranking graphic where one exists, else lazer's own rank badge, else a coloured letter.</param>
/// <param name="Mods">Mod acronyms ("HD", "HR", "DT", …), drawn as a row; empty for a no-mod play.</param>
/// <param name="Colour">The player's rail colour, shared with their cursor and name so the panel reads
/// as "this player" at a glance.</param>
public record PlayerResultData(
    string PlayerName,
    long TotalScore,
    int Count300,
    int Count100,
    int Count50,
    int CountMiss,
    int MaxCombo,
    double Accuracy,
    string Grade,
    IReadOnlyList<string> Mods,
    Color4 Colour);

/// <summary>
/// The one-per-screen beatmap header shown across the top of the result screen, above the grid of
/// per-player panels — the "I love you Orchestra - Red Ocean [Ex] / Beatmap by captin1 / Played by X
/// on DATE" block in the reference. Pure text the lead assembles once; the same header sits over every
/// panel in a multi-replay grid.
/// </summary>
/// <param name="Title">The song/difficulty title line, e.g. "I love you Orchestra - Red Ocean [Ex]".</param>
/// <param name="Artist">The artist line (may be folded into <paramref name="Title"/> by the caller; kept
/// separate so the panel can style it as a sub-line if wanted).</param>
/// <param name="Mapper">The mapper credit, e.g. "captin1" — the panel prefixes it with "Beatmap by".</param>
/// <param name="PlayedByLine">The already-formatted "Played by NAME on DATE" line; empty draws nothing.</param>
public record ResultBeatmapHeader(
    string Title,
    string Artist,
    string Mapper,
    string PlayedByLine);
