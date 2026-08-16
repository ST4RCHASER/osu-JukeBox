#nullable enable

using System.Reflection;
using JukeBox.Game;
using NUnit.Framework;

namespace JukeBox.Game.Tests
{
    /// <summary>
    /// The build stamp shown at the bottom of Settings. The formatting is tested directly because
    /// the interesting cases — an unstamped dev build versus a tagged release — cannot both exist in
    /// one test run.
    /// </summary>
    [TestFixture]
    public class AppVersionTest
    {
        // A release tag stamps a real major, and the prerelease suffix has to survive: it is the
        // whole reason this reads InformationalVersion rather than AssemblyVersion, which cannot
        // hold one.
        [Test]
        public void ATaggedReleaseShowsItsVersionIncludingAnyPrereleaseSuffix()
        {
            Assert.That(AppVersion.Format("1.0.0-rc1", major: 1, debugBuild: false), Is.EqualTo("v1.0.0-rc1"));
            Assert.That(AppVersion.Format("2.3.4", major: 2, debugBuild: false), Is.EqualTo("v2.3.4"));
        }

        // JukeBox.Desktop.csproj carries 0.0.0 until a tag stamps over it. A bare "v0.0.0" would
        // read as a release that happens to be numbered zero.
        [Test]
        public void AnUnstampedBuildSaysItIsLocal()
        {
            Assert.That(AppVersion.Format("0.0.0", major: 0, debugBuild: true), Is.EqualTo("v0.0.0 (local debug)"));
            Assert.That(AppVersion.Format("0.0.0", major: 0, debugBuild: false), Is.EqualTo("v0.0.0 (local release)"));
        }

        // The SDK appends "+<sha>" by itself for a repo checkout, so the revision costs no build
        // plumbing — it just has to be shortened to something readable.
        [Test]
        public void TheSourceRevisionIsShownShortened()
        {
            Assert.That(AppVersion.Format("0.0.0+9f6d37194b86c5fcc96acc32eef195d2867695ec", major: 0, debugBuild: true),
                Is.EqualTo("v0.0.0 (local debug · 9f6d371)"));

            Assert.That(AppVersion.Format("1.0.0-rc1+9f6d37194b86c5fcc96acc32eef195d2867695ec", major: 1, debugBuild: false),
                Is.EqualTo("v1.0.0-rc1 (9f6d371)"));
        }

        [Test]
        public void AMissingAttributeDoesNotRenderAnEmptyVersion()
        {
            Assert.That(AppVersion.Format(null, major: 0, debugBuild: false), Is.EqualTo("vunknown (local release)"));
            Assert.That(AppVersion.Format("", major: 1, debugBuild: false), Is.EqualTo("vunknown"));
        }

        // The point of the whole exercise: the string is READ from the assembly, not written into
        // the source. Asserted against the attribute this test reads back itself, so a hardcoded
        // display string would have to match a value nobody typed.
        [Test]
        public void TheDisplayedVersionComesFromTheAssemblyAttribute()
        {
            var assembly = typeof(AppVersion).Assembly;
            string? attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            Assert.That(attribute, Is.Not.Null.And.Not.Empty, "the build should carry an informational version to read");

            // The numeric part of whatever the attribute says must appear in the rendered string.
            string numeric = attribute!.Split('+')[0];

            Assert.That(AppVersion.For(assembly), Does.Contain(numeric));
            Assert.That(AppVersion.For(assembly), Does.StartWith("v"));
        }
    }
}
