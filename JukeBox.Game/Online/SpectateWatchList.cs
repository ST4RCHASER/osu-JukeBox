#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace JukeBox.Game.Online;

/// <summary>
/// The list of usernames being watched, and the rules for editing it.
///
/// <para>
/// Pure and static so the list can be persisted as one config string without a settings type of its
/// own, and so the edit rules — trimming, the duplicate check, the cap — live in one place instead
/// of being re-implemented by every button that touches the list.
/// </para>
/// </summary>
public static class SpectateWatchList
{
    /// <summary>
    /// How many players may be watched at once.
    ///
    /// <para>
    /// Twice <see cref="Replays.SpectatePanePlan.MAX_PANES"/>, deliberately. Watching is cheap
    /// (two polling requests a round for the whole list) while RENDERING is expensive, so the list
    /// is allowed to be longer than the wall: the extra names are the bench that panes rotate
    /// through as people start and stop playing. Much longer than this and the poll starts
    /// approaching osu!'s general request allowance for no benefit, since only four can ever show.
    /// </para>
    /// </summary>
    public const int MAX_WATCHED = 8;

    /// <summary>The separator in the persisted string. Usernames cannot contain it.</summary>
    private const char separator = ',';

    /// <summary>Reads the persisted string back into names, dropping blanks and duplicates.</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Array.Empty<string>();

        var names = new List<string>();

        foreach (string part in stored.Split(separator))
        {
            string name = part.Trim();

            if (name.Length == 0 || contains(names, name))
                continue;

            names.Add(name);

            if (names.Count == MAX_WATCHED)
                break;
        }

        return names;
    }

    /// <summary>Writes names back out for the config file.</summary>
    public static string Format(IEnumerable<string> names) => string.Join(separator, names);

    /// <summary>
    /// The list with <paramref name="name"/> added, or the list unchanged when the name is blank,
    /// already present, or the list is full.
    ///
    /// <para>
    /// Returns a NEW list rather than mutating, so a caller can compare against what it had and
    /// tell whether the add actually did anything — which is what decides between "added" and
    /// "already watching them" in the UI.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Add(IReadOnlyList<string> current, string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        // A username with the separator in it would come back as two names, so it is refused here
        // rather than silently splitting on the next load.
        if (trimmed.Length == 0 || trimmed.Contains(separator) || current.Count >= MAX_WATCHED || contains(current, trimmed))
            return current;

        var next = new List<string>(current) { trimmed };
        return next;
    }

    /// <summary>The list without <paramref name="name"/>, matched the way osu! matches usernames.</summary>
    public static IReadOnlyList<string> Remove(IReadOnlyList<string> current, string? name)
        => current.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Whether the list already has this name. Case-INSENSITIVE, because osu! usernames are: adding
    /// "Peppy" to a list that already has "peppy" would watch one person twice and spend two of the
    /// eight slots on them.
    /// </summary>
    public static bool Contains(IReadOnlyList<string> current, string? name) => contains(current, (name ?? string.Empty).Trim());

    private static bool contains(IReadOnlyList<string> names, string name)
        => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}
