namespace JukeBox.Game.Online
{
    /// <summary>
    /// User-selectable preferred beatmap mirror (see <see cref="SwitchableMirror"/>). The pick only
    /// changes which mirror is tried first — the others always remain as fallbacks.
    /// </summary>
    public enum MirrorSource
    {
        Auto,
        Nerinyan,
        Catboy,
        OsuDirect,
    }
}
