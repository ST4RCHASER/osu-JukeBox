using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// Combines <see cref="JukeBoxTestScene"/>'s real-audio-host <see cref="CreateRunner"/> (needed
    /// by anything resolving <see cref="JukeBox.Game.Playback.PlaybackController"/>, which itself resolves
    /// AudioManager/GameHost) with <see cref="ManualInputManagerTestScene"/>'s <c>InputManager</c>
    /// (needed to drive real mouse drag input, e.g. over a slider). C# single inheritance means a
    /// TestScene can't derive from both existing bases at once, so this duplicates
    /// JukeBoxTestScene's CreateRunner override on top of ManualInputManagerTestScene instead.
    /// </summary>
    public abstract partial class JukeBoxManualInputTestScene : ManualInputManagerTestScene
    {
        protected override ITestSceneTestRunner CreateRunner() => new JukeBoxTestSceneTestRunner();

        private partial class JukeBoxTestSceneTestRunner : JukeBoxGameBase, ITestSceneTestRunner
        {
            private TestSceneTestRunner.TestRunner runner;

            protected override void LoadAsyncComplete()
            {
                base.LoadAsyncComplete();
                Add(runner = new TestSceneTestRunner.TestRunner());
            }

            public void RunTestBlocking(TestScene test) => runner.RunTestBlocking(test);
        }
    }
}
