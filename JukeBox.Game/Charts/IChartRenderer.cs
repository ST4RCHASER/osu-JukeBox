#nullable enable

namespace JukeBox.Game.Charts;

/// <summary>
/// Common test/diagnostic surface over the per-mode chart renderers (<see cref="ChartLayer"/> for
/// osu!std, <see cref="TaikoChartLayer"/>, <see cref="CatchChartLayer"/>,
/// <see cref="ManiaChartLayer"/>), so BeatmapVisuals and tests can treat them uniformly.
/// </summary>
internal interface IChartRenderer
{
    /// <summary>Total hit-object drawables compiled at load.</summary>
    int TotalObjectCount { get; }

    /// <summary>Hit-object drawables currently inside their lifetime window.</summary>
    int AliveObjectCount { get; }
}
