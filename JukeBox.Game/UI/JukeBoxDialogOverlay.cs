#nullable enable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Game.Overlays;
using osu.Game.Overlays.Dialog;
using osuTK.Input;

namespace JukeBox.Game.UI;

/// <summary>
/// Hosts lazer's confirmation dialogs (<see cref="PopupDialog"/> and its
/// <see cref="DangerousActionDialog"/> subclasses) so destructive actions in this app confirm the
/// way they do in osu! rather than through something hand-rolled.
///
/// <para>
/// Only the HOST is ours. lazer's own <see cref="DialogOverlay"/> would have been the thing to
/// reuse, but it resolves <c>MusicController</c> — which needs a realm-backed BeatmapManager this
/// app deliberately does not have (see <see cref="JukeBoxGameBase"/>'s lazer shims). The dialogs
/// themselves have no such dependency: <see cref="PopupDialog"/> wants an AudioManager,
/// <see cref="PopupDialogDangerousButton"/> wants OsuColour and OsuConfigManager, and this app
/// caches all three. So this replaces roughly thirty lines of overlay plumbing and nothing else —
/// the chrome, the hold-to-confirm button and the wording are all lazer's.
/// </para>
///
/// <para>
/// Cached game-wide as <see cref="IDialogOverlay"/>, which is the interface lazer's own components
/// resolve, so anything pushing a dialog here is written exactly as it would be in lazer.
/// </para>
/// </summary>
public partial class JukeBoxDialogOverlay : CompositeDrawable, IDialogOverlay
{
    private readonly Container dialogContainer;
    private readonly Box scrim;

    public PopupDialog? CurrentDialog { get; private set; }

    public JukeBoxDialogOverlay()
    {
        RelativeSizeAxes = Axes.Both;

        // Nothing to draw and nothing to swallow input with until a dialog actually arrives —
        // an always-present full-screen layer would eat every click in the app underneath it.
        Alpha = 0;

        InternalChildren = new Drawable[]
        {
            scrim = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Theme.ModalScrim,
            },
            dialogContainer = new Container { RelativeSizeAxes = Axes.Both },
        };
    }

    public void Push(PopupDialog dialog)
    {
        if (dialog == CurrentDialog)
            return;

        // One dialog at a time, like lazer's overlay: a second push replaces the first rather
        // than stacking two confirmations the user has to answer in order.
        CurrentDialog?.Hide();

        CurrentDialog = dialog;
        dialogContainer.Add(dialog);

        // A dialog dismisses itself once a button is pressed (PopupDialog hides on any button
        // action), so its own State is what tells us the interaction is over — there is no
        // "closed" callback to hook. Expire rather than just hide: nothing reopens a dialog that
        // has been answered, and leaving it parented keeps its transforms running forever.
        dialog.State.BindValueChanged(state =>
        {
            if (state.NewValue != Visibility.Hidden)
                return;

            if (CurrentDialog == dialog)
            {
                CurrentDialog = null;
                this.FadeOut(Theme.HoverFadeDuration, Easing.OutQuint);
            }

            dialog.Delay(PopupDialog.EXIT_DURATION).Expire();
        });

        Alpha = 1;
        scrim.FadeTo(1, Theme.HoverFadeDuration, Easing.OutQuint);
        dialog.Show();
    }

    /// <summary>
    /// Swallows every click that lands outside the dialog card, so a destructive confirmation
    /// cannot be answered by accident by clicking something behind it. Clicking the scrim cancels,
    /// matching lazer, where dismissing the overlay is the same as choosing "cancel".
    /// </summary>
    protected override bool OnClick(ClickEvent e)
    {
        CurrentDialog?.Hide();
        return true;
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Key.Escape && !e.Repeat && CurrentDialog != null)
        {
            CurrentDialog.Hide();
            return true;
        }

        return base.OnKeyDown(e);
    }

    // Only reachable while a dialog is up: Alpha 0 makes the whole overlay non-present, and a
    // non-present drawable receives no input at all.
    public override bool PropagatePositionalInputSubTree => Alpha > 0;

    public override bool PropagateNonPositionalInputSubTree => Alpha > 0;
}
