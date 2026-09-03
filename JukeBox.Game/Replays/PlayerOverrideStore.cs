#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using osu.Game.Rulesets.Mods;
using osuTK.Graphics;

namespace JukeBox.Game.Replays;

/// <summary>What kind of per-player override changed, so a listener can react at the right cost.</summary>
public enum PlayerOverrideKind
{
    /// <summary>A cursor colour. Applied live — the cursor, its trail and the rail name are
    /// re-tinted where they stand, no rebuild.</summary>
    Colour,

    /// <summary>A mod set. Changes beatmap conversion and scoring, so the affected renderers have to
    /// be rebuilt to take it.</summary>
    Mods,

    /// <summary>A gameplay skin. Rebuilds for the same reason mods do.</summary>
    Skin,
}

/// <summary>One player's session overrides, held by <see cref="PlayerOverrideStore"/>. A null field
/// means "no override" — fall back to what the replay itself recorded (its mods) or to the slot
/// default (its hue-spread colour, the global skin).</summary>
public sealed class PlayerOverride
{
    /// <summary>The mods to render and SCORE this player under, replacing the ones they recorded.
    /// Null leaves the recorded mods in force.</summary>
    public IReadOnlyList<Mod>? Mods { get; internal set; }

    /// <summary>This player's cursor colour, or null for the slot's hue-spread default.</summary>
    public Color4? CursorColour { get; internal set; }

    /// <summary>This player's gameplay skin key, or null for the global skin.</summary>
    public string? SkinKey { get; internal set; }

    /// <summary>Whether nothing is overridden, i.e. this player renders exactly as they were dropped.</summary>
    public bool IsEmpty => Mods == null && CursorColour == null && SkinKey == null;
}

/// <summary>
/// Session-lifetime per-player overrides for multi-replay playback, keyed by the replay itself.
///
/// <para>
/// This is deliberately NOT the shared Chart-tab mod selection. That selection is one value for the
/// whole chart, and pointing every player's render at it is exactly the bug that put player one's
/// mods on all forty-seven of them (fixed in 53f7d2c). Overrides here are per replay: setting one
/// player's mods, colour or skin touches only that player, and everyone else keeps falling back to
/// what they recorded.
/// </para>
///
/// <para>
/// Keyed on the <see cref="ReplayAttachment"/> instance through a weak table, which gives two things
/// for free: the same dropped replays are the same instances across a visuals rebuild, so an
/// override survives a mode switch; and a fresh drop brings fresh instances the old overrides simply
/// do not key, so a new set of players starts clean without anyone having to sweep the old ones out.
/// Nothing here is persisted — a restart is a clean slate, which the brief says is acceptable.
/// </para>
/// </summary>
public sealed class PlayerOverrideStore
{
    private readonly ConditionalWeakTable<ReplayAttachment, PlayerOverride> overrides = new ConditionalWeakTable<ReplayAttachment, PlayerOverride>();

    /// <summary>Raised after an override changes, with the replay affected and what kind of change it
    /// was. Colour changes are applied live by listeners; mods and skin ask for a rebuild.</summary>
    public event Action<ReplayAttachment, PlayerOverrideKind>? Changed;

    /// <summary>This replay's override, or null when it has none set. A read that must not create one
    /// — the render path uses it so an untouched player allocates nothing.</summary>
    public PlayerOverride? Peek(ReplayAttachment replay)
        => overrides.TryGetValue(replay, out var existing) ? existing : null;

    /// <summary>This replay's override, created empty on first ask. For the settings UI, which is
    /// about to write to it.</summary>
    public PlayerOverride For(ReplayAttachment replay)
        => overrides.GetValue(replay, _ => new PlayerOverride());

    /// <summary>The mods to render this player under: their override if set, otherwise the
    /// <paramref name="recorded"/> set the caller already has in hand.</summary>
    public IReadOnlyList<Mod> EffectiveMods(ReplayAttachment replay, IReadOnlyList<Mod> recorded)
        => Peek(replay)?.Mods ?? recorded;

    /// <summary>The cursor colour for this player: their override if set, otherwise the
    /// <paramref name="fallback"/> slot colour.</summary>
    public Color4 EffectiveCursorColour(ReplayAttachment replay, Color4 fallback)
        => Peek(replay)?.CursorColour ?? fallback;

    /// <summary>Sets (or with null, clears) this player's cursor colour and announces it.</summary>
    public void SetCursorColour(ReplayAttachment replay, Color4? colour)
    {
        For(replay).CursorColour = colour;
        Changed?.Invoke(replay, PlayerOverrideKind.Colour);
    }

    /// <summary>Sets (or with null, clears) this player's mods and announces it.</summary>
    public void SetMods(ReplayAttachment replay, IReadOnlyList<Mod>? mods)
    {
        For(replay).Mods = mods;
        Changed?.Invoke(replay, PlayerOverrideKind.Mods);
    }

    /// <summary>Sets (or with null, clears) this player's gameplay skin and announces it.</summary>
    public void SetSkin(ReplayAttachment replay, string? skinKey)
    {
        For(replay).SkinKey = skinKey;
        Changed?.Invoke(replay, PlayerOverrideKind.Skin);
    }
}
