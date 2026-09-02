#nullable enable

using NUnit.Framework;
using osu.Framework.Bindables;

namespace JukeBox.Game.Tests
{
    /// <summary>
    /// Hit sounds start quieter than the music on a NEW install, and only on a new install.
    ///
    /// <para>
    /// The distinction carries real weight here because the effect volume is not ours — it lives
    /// in osu!framework's config, which writes its entire store to disk (every key, defaults
    /// included) the first time any framework setting changes. So an existing install's file
    /// already carries a VolumeEffect line whether or not the user ever moved the slider, and
    /// there is no per-key "was this deliberately set" flag to consult. The only question that can
    /// honestly be asked is "has this app ever run here", which is why the rule takes a boolean.
    /// </para>
    /// </summary>
    [TestFixture]
    public class EffectVolumeDefaultTest
    {
        [Test]
        public void AFreshInstallStartsEffectsAtSixtyPercent()
        {
            var effectVolume = new Bindable<double>(1.0);

            JukeBoxGameBase.ApplyFreshInstallEffectVolume(freshInstall: true, effectVolume);

            Assert.That(effectVolume.Value, Is.EqualTo(0.6).Within(0.0001));
        }

        // The case the rule exists to protect: someone who already runs this app keeps whatever
        // they had, including a value they never touched.
        [TestCase(1.0, TestName = "AnExistingInstallIsLeftAlone_UntouchedFrameworkDefault")]
        [TestCase(0.35, TestName = "AnExistingInstallIsLeftAlone_DeliberatelyQuiet")]
        [TestCase(0.6, TestName = "AnExistingInstallIsLeftAlone_AlreadyAtTheNewValue")]
        public void AnExistingInstallIsLeftAlone(double stored)
        {
            var effectVolume = new Bindable<double>(stored);

            JukeBoxGameBase.ApplyFreshInstallEffectVolume(freshInstall: false, effectVolume);

            Assert.That(effectVolume.Value, Is.EqualTo(stored).Within(0.0001));
        }

        [Test]
        public void TheFreshInstallValueIsSixtyPercent()
        {
            Assert.That(JukeBoxGameBase.FRESH_INSTALL_EFFECT_VOLUME, Is.EqualTo(0.6).Within(0.0001));
        }
    }
}
