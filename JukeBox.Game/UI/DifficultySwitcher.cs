#nullable enable

using System.Collections.Generic;
using JukeBox.Game.Beatmaps;
using JukeBox.Game.Playback;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

namespace JukeBox.Game.UI;

/// <summary>
/// Row of pill "chips", one per difficulty of the currently playing set, named by their
/// [Metadata] Version. Clicking a chip switches chart/hitsounds/storyboard to that difficulty
/// while playback continues at the current time (via
/// <see cref="PlaybackController.SwitchDifficultyAsync"/>). osu!std difficulties show in normal
/// text; other modes are greyed (no chart for them — audio/storyboard still switch fine). The
/// current selection is highlighted with the accent colour.
/// </summary>
public partial class DifficultySwitcher : CompositeDrawable
{
    /// <summary>Keep the bar tidy on sets with absurd difficulty counts.</summary>
    private const int max_chips = 10;

    [Resolved]
    private PlaybackController playback { get; set; } = null!;

    private FillFlowContainer<DifficultyChip> flow = null!;

    /// <summary>Test-only access to the chips (JukeBox.Game.Tests has InternalsVisibleTo).</summary>
    internal IReadOnlyList<DifficultyChip> Chips => flow.Children;

    [BackgroundDependencyLoader]
    private void load()
    {
        AutoSizeAxes = Axes.Both;

        InternalChild = flow = new FillFlowContainer<DifficultyChip>
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(4, 0),
        };
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        playback.Current.BindValueChanged(onSetChanged, true);
        playback.SelectedOsuFile.BindValueChanged(_ => updateHighlight());
    }

    private void onSetChanged(ValueChangedEvent<CachedBeatmapSet?> change)
    {
        flow.Clear();

        var set = change.NewValue;
        if (set == null || set.Difficulties.Count <= 1)
        {
            updateHighlight();
            return; // nothing to switch between — keep the bar clean
        }

        for (int i = 0; i < set.Difficulties.Count && i < max_chips; i++)
        {
            var diff = set.Difficulties[i];
            flow.Add(new DifficultyChip(diff)
            {
                Action = () => _ = playback.SwitchDifficultyAsync(diff.Path),
            });
        }

        updateHighlight();
    }

    private void updateHighlight()
    {
        var set = playback.Current.Value;
        string? selected = playback.SelectedOsuFile.Value ?? set?.PreferredOsuFile;

        foreach (var chip in flow)
            chip.Selected = chip.Difficulty.Path == selected;
    }

    internal partial class DifficultyChip : ClickableContainer
    {
        private const float chip_height = 18;

        public readonly DifficultyInfo Difficulty;

        private Box background = null!;
        private SpriteText label = null!;

        private bool selected;

        public bool Selected
        {
            get => selected;
            set
            {
                selected = value;
                if (IsLoaded)
                    updateColours();
            }
        }

        public DifficultyChip(DifficultyInfo difficulty)
        {
            Difficulty = difficulty;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            AutoSizeAxes = Axes.X;
            Height = chip_height;
            Masking = true;
            CornerRadius = chip_height / 2;

            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Theme.ElevatedSurface,
                },
                label = new SpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Padding = new MarginPadding { Horizontal = 8 },
                    Font = FontUsage.Default.With(size: 11),
                    Text = Difficulty.Version,
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            updateColours();
        }

        private void updateColours()
        {
            background.FadeColour(selected ? Theme.Accent : Theme.ElevatedSurface, Theme.HoverFadeDuration);

            // Non-std modes are greyed: they're still playable (audio/storyboard), just chartless.
            label.Colour = selected
                ? Theme.Background
                : Difficulty.Mode == 0 ? Theme.TextPrimary : Theme.TextTertiary;
        }

        protected override bool OnHover(HoverEvent e)
        {
            if (!selected)
                background.FadeColour(Theme.ElevatedSurface.Lighten(0.3f), Theme.HoverFadeDuration);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            updateColours();
            base.OnHoverLost(e);
        }
    }
}
