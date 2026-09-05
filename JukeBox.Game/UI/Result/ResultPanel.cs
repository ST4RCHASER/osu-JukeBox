#nullable enable

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Game.Online.Leaderboards;
using osu.Game.Scoring;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace JukeBox.Game.UI.Result;

/// <summary>
/// One player's osu!-RANKING panel, built entirely from a <see cref="PlayerResultData"/> — a compact
/// card carrying the total score, the 300/100/50/miss counts in their hit-result colours, max combo,
/// accuracy, the grade, mods and the player's name. It has no live dependencies beyond a nullable skin
/// (so it renders in a bare test scene): every value is read once from the record at construction, the
/// way the reference RANKING screen is a still frame of a finished play rather than a running readout.
///
/// <para>
/// The grade is drawn the same way <see cref="JukeBox.Game.LazerPlayer.KnockoutBoard"/> draws it: the
/// active skin's own "ranking-X-small" texture when it has one, otherwise lazer's <see cref="DrawableRank"/>
/// badge, otherwise a bare coloured letter — so a custom skin's grade art shows through here exactly as
/// it does on the rail, and a skinless test still gets a legible grade.
/// </para>
/// </summary>
public partial class ResultPanel : CompositeDrawable
{
    /// <summary>The card's fixed footprint. Compact so a grid of them (up to ~47) tiles without any one
    /// panel dominating; the score/grade type scale is sized against this.</summary>
    public const float PanelWidth = 300;

    public const float PanelHeight = 172;

    // ---- Hit-result colours (osu! RANKING convention; see the reference screenshot) ----------------
    // Kept local rather than pulled from OsuColour so the panel needs no colour-source dependency: a
    // 300 is the pale cyan/blue, a 100 the green, a 50 the gold/yellow, a miss the red.
    private static readonly Color4 colour300 = new Color4(0x66, 0xCC, 0xFF, 0xFF);
    private static readonly Color4 colour100 = new Color4(0x66, 0xDD, 0x66, 0xFF);
    private static readonly Color4 colour50 = new Color4(0xFF, 0xCC, 0x33, 0xFF);
    private static readonly Color4 colourMiss = new Color4(0xFF, 0x55, 0x55, 0xFF);

    // The skin the grade texture is looked up in — the same chain the chart renders under, resolved
    // canBeNull so a test scene (or an app state before any skin is built) simply falls through to the
    // DrawableRank badge / letter path rather than failing to construct.
    [Resolved(canBeNull: true)]
    private ISkinSource? skin { get; set; }

    private readonly PlayerResultData data;

    private SpriteText scoreText = null!;
    private SpriteText accuracyText = null!;
    private SpriteText count300Text = null!;
    private SpriteText count100Text = null!;
    private SpriteText count50Text = null!;
    private SpriteText countMissText = null!;
    private SpriteText maxComboText = null!;
    private SpriteText nameText = null!;
    private SpriteText modsText = null!;
    private Container gradeCell = null!;

    // ---- Test hooks (JukeBox.Game.Tests has InternalsVisibleTo): the exact strings drawn, so a test
    // asserts on what the panel shows rather than restating its formatting. ------------------------
    internal string ScoreText => scoreText.Text.ToString();

    internal string AccuracyText => accuracyText.Text.ToString();

    internal string Count300Text => count300Text.Text.ToString();

    internal string Count100Text => count100Text.Text.ToString();

    internal string Count50Text => count50Text.Text.ToString();

    internal string CountMissText => countMissText.Text.ToString();

    internal string MaxComboText => maxComboText.Text.ToString();

    internal string NameText => nameText.Text.ToString();

    internal string ModsText => modsText.Text.ToString();

    /// <summary>Test hook: the raw grade this panel is showing ("X", "S", "A", …). The graphic beside it
    /// is skin-dependent; this is the value that graphic stands for, regardless of which of the three
    /// draw paths was taken.</summary>
    internal string GradeText => data.Grade;

    /// <summary>Test hook: whether the grade was drawn as a graphic (skin texture or DrawableRank badge)
    /// rather than as the bare-letter fallback.</summary>
    internal bool GradeIsGraphic { get; private set; }

    public ResultPanel(PlayerResultData data)
    {
        this.data = data;

        Size = new Vector2(PanelWidth, PanelHeight);
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        Masking = true;
        CornerRadius = Theme.CornerRadius;
        EdgeEffect = Theme.PanelShadow;

        // The grade texture from the active skin if it has one; the DrawableRank / letter fallbacks are
        // decided in buildGrade() when the texture is null.
        Texture? gradeTexture = data.Grade.Length == 0 ? null : skin?.GetTexture($"ranking-{data.Grade}-small");

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.PanelSurface,
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Padding = new MarginPadding(Theme.RowSpacing),
                Spacing = new Vector2(0, 4),
                Children = new Drawable[]
                {
                    // Top band: the large total score on the left, the grade on the right — the two
                    // headline facts of the RANKING screen.
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 44,
                        Children = new Drawable[]
                        {
                            scoreText = label(formatScore(data.TotalScore), 34, "Bold", Theme.TextPrimary, Anchor.CentreLeft),
                            gradeCell = new Container
                            {
                                Anchor = Anchor.CentreRight,
                                Origin = Anchor.CentreRight,
                                Size = new Vector2(44),
                                Masking = true,
                            },
                        },
                    },

                    // The 2x2 hit-count grid, each count in its own hit-result colour.
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Full,
                        Children = new Drawable[]
                        {
                            countCell(out count300Text, data.Count300, colour300),
                            countCell(out count100Text, data.Count100, colour100),
                            countCell(out count50Text, data.Count50, colour50),
                            countCell(out countMissText, data.CountMiss, colourMiss),
                        },
                    },

                    // Max combo and accuracy, side by side, each with a small caption.
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 38,
                        Children = new Drawable[]
                        {
                            labelled("MAX COMBO", out maxComboText, $"{data.MaxCombo}x", Anchor.CentreLeft),
                            labelled("ACCURACY", out accuracyText, $"{data.Accuracy * 100:0.00}%", Anchor.CentreRight),
                        },
                    },

                    // Mods (acronyms, joined) then the player's name in their own colour, at the foot.
                    modsText = label(data.Mods.Count == 0 ? string.Empty : string.Join(" ", data.Mods),
                        Theme.CaptionTextSize, "Bold", Theme.TextSecondary, Anchor.TopLeft),
                    nameText = label(data.PlayerName, Theme.RowTitleTextSize, "SemiBold", data.Colour, Anchor.TopLeft),
                },
            },
        };

        buildGrade(gradeTexture);
    }

    /// <summary>Places the grade in its cell: the skin's own texture (fit-scaled), else lazer's rank
    /// badge, else a bare coloured letter — the same precedence the rail uses.</summary>
    private void buildGrade(Texture? texture)
    {
        if (data.Grade.Length == 0)
            return;

        if (texture != null)
        {
            gradeCell.Add(new Sprite
            {
                RelativeSizeAxes = Axes.Both,
                FillMode = FillMode.Fit,
                Texture = texture,
            });
            GradeIsGraphic = true;
            return;
        }

        if (Enum.TryParse<ScoreRank>(data.Grade, out var rank))
        {
            gradeCell.Add(new DrawableRank(rank) { RelativeSizeAxes = Axes.Both });
            GradeIsGraphic = true;
            return;
        }

        // Last resort: a bare letter, tinted so at least S/A/etc. read as distinct.
        gradeCell.Add(label(data.Grade, 34, "Bold", gradeLetterColour(data.Grade), Anchor.Centre));
        GradeIsGraphic = false;
    }

    /// <summary>One hit-count cell: a coloured dot marker followed by "923x" in that count's colour, laid
    /// out at half the panel width so four tile into a 2x2 grid.</summary>
    private static Container countCell(out SpriteText countText, int count, Color4 colour)
    {
        countText = label($"{count}x", 18, "SemiBold", colour, Anchor.CentreLeft);
        countText.Margin = new MarginPadding { Left = 18 };

        return new Container
        {
            Width = 0.5f,
            RelativeSizeAxes = Axes.X,
            Height = 26,
            Children = new Drawable[]
            {
                new Circle
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new Vector2(10),
                    Colour = colour,
                },
                countText,
            },
        };
    }

    /// <summary>A small caption over a value, anchored to one side of its row (max combo left, accuracy
    /// right, as in the reference).</summary>
    private static Container labelled(string caption, out SpriteText value, string valueString, Anchor anchor)
    {
        value = label(valueString, 20, "Bold", Theme.TextPrimary, Anchor.TopLeft);

        return new Container
        {
            Anchor = anchor,
            Origin = anchor,
            AutoSizeAxes = Axes.Both,
            Child = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 1),
                Children = new Drawable[]
                {
                    label(caption, Theme.CaptionTextSize - 2, "Regular", Theme.TextTertiary, Anchor.TopLeft),
                    value,
                },
            },
        };
    }

    /// <summary>Score in the reference's zero-padded form ("0493309") so every panel's headline number
    /// lines up at the same width.</summary>
    private static string formatScore(long score) => score.ToString("0000000");

    private static Color4 gradeLetterColour(string grade) => grade switch
    {
        "X" or "XH" or "S" or "SH" => new Color4(0xFF, 0xD9, 0x66, 0xFF), // gold
        "A" => new Color4(0x88, 0xDD, 0x44, 0xFF),                        // green
        "B" => new Color4(0x66, 0xB8, 0xFF, 0xFF),                        // blue
        "C" => new Color4(0xCC, 0x66, 0xFF, 0xFF),                        // purple
        _ => new Color4(0xFF, 0x66, 0x66, 0xFF),                          // red (D / unknown)
    };

    /// <summary>A themed <see cref="SpriteText"/> — the one place font/colour/anchor are set, so every
    /// line on the panel reads as one type system.</summary>
    private static SpriteText label(string content, float size, string weight, Color4 colour, Anchor anchor)
        => new SpriteText
        {
            Anchor = anchor,
            Origin = anchor,
            Text = content,
            Font = FontUsage.Default.With(size: size, weight: weight),
            Colour = colour,
            Shadow = true,
        };
}
