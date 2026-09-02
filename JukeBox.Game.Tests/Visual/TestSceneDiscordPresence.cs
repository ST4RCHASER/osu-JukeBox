#nullable enable

using System;
using System.Collections.Generic;
using JukeBox.Game.Configuration;
using JukeBox.Game.Presence;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;

namespace JukeBox.Game.Tests.Visual
{
    /// <summary>
    /// WHEN the presence is sent, driven through the real service against a fake IPC client.
    /// Playback state arrives through an overridden <see cref="DiscordPresenceService.ReadInputs"/>
    /// rather than a real audio track: what is under test here is the update policy (debounce, the
    /// toggle, clearing), and a real track would make the timing of every assertion depend on the
    /// test host's frame rate. <see cref="Tests.Presence.PresenceStateTest"/> covers what it says.
    /// </summary>
    [TestFixture]
    public partial class TestSceneDiscordPresence : JukeBoxTestScene
    {
        [Resolved]
        private JukeBoxConfigManager config { get; set; } = null!;

        private FakePresenceClient client = null!;
        private TestPresenceService service = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("fresh service", () =>
            {
                Clear();
                config.SetValue(JukeBoxSetting.RenderChart, false);
                config.SetValue(JukeBoxSetting.DiscordRichPresence, true);

                client = new FakePresenceClient();
                Add(service = new TestPresenceService(client));
            });

            AddUntilStep("service loaded", () => service.IsLoaded);
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            // Snapshots here write into the shared test-browser config; put the default back so a
            // later scene isn't started with presence switched off.
            AddStep("restore config", () => config.SetValue(JukeBoxSetting.DiscordRichPresence, true));
        }

        [Test]
        public void ConnectsOnceWhileTheSettingIsOn()
        {
            AddAssert("client started", () => client.StartCount == 1);
        }

        [Test]
        public void PublishesWhatIsPlaying()
        {
            AddUntilStep("presence sent", () => client.Published != null);
            AddAssert("it describes the track", () => client.Published!.Details == "FREEDOM DIVE" && client.Published.State == "xi");
        }

        /// <summary>
        /// The user watching the setting is the one person guaranteed to be looking at the result, so
        /// turning it off must not wait out the debounce window.
        /// </summary>
        [Test]
        public void TurningTheSettingOffClearsThePresenceImmediately()
        {
            AddUntilStep("presence sent", () => client.Published != null);

            AddStep("turn it off", () => config.SetValue(JukeBoxSetting.DiscordRichPresence, false));
            AddAssert("cleared in the same breath", () => client.ClearCount == 1 && client.Published == null);
        }

        [Test]
        public void NothingIsSentWhileTheSettingIsOff()
        {
            AddStep("turn it off", () => config.SetValue(JukeBoxSetting.DiscordRichPresence, false));
            AddStep("reset counters", () => client.ResetCounts());

            AddStep("play a different song", () =>
            {
                service.Inputs = service.Inputs with { Title = "Blue Zenith" };

                for (int i = 0; i < 20; i++)
                    service.UpdatePresence();
            });

            AddAssert("no presence published", () => client.PublishCount == 0);
        }

        /// <summary>
        /// A dragged seek bar produces a different presence every frame. Discord rate-limits presence
        /// updates, so the whole drag has to cost ONE — sent after the drag settles, and describing
        /// where the user actually let go rather than where the drag started.
        /// </summary>
        [Test]
        public void ScrubbingCollapsesToASingleUpdate()
        {
            AddUntilStep("initial presence sent", () => client.PublishCount == 1);
            AddStep("reset counters", () => client.ResetCounts());

            AddStep("drag across 60 positions within one frame", () =>
            {
                for (int i = 0; i < 60; i++)
                {
                    service.Inputs = service.Inputs with { PositionMs = i * 2_000 };
                    service.UpdatePresence();
                }
            });

            AddUntilStep("an update lands", () => client.PublishCount >= 1);
            AddAssert("exactly one, for the whole drag", () => client.PublishCount == 1);
            AddAssert("and it describes where the drag ended", () =>
            {
                // 59 * 2s into a 180s track: 118s elapsed, 62s left.
                var published = client.Published!;
                return published.StartUtc == service.Inputs.NowUtc.AddSeconds(-118)
                       && published.EndUtc == service.Inputs.NowUtc.AddSeconds(62);
            });
        }

        [Test]
        public void ASongChangeRepublishes()
        {
            AddUntilStep("initial presence sent", () => client.PublishCount == 1);

            AddStep("next song", () => service.Inputs = service.Inputs with { Title = "Blue Zenith", Artist = "xi" });

            AddUntilStep("presence follows", () => client.Published?.Details == "Blue Zenith");
        }

        [Test]
        public void TurningTheChartOnSwitchesToWatching()
        {
            AddUntilStep("listening", () => client.Published?.Activity == PresenceActivity.Listening);

            AddStep("render the chart", () => config.SetValue(JukeBoxSetting.RenderChart, true));

            AddUntilStep("watching the chart", () => client.Published?.Activity == PresenceActivity.WatchingChart);
            AddAssert("and says so", () => client.Published!.Details == "chart · FREEDOM DIVE");
        }

        [Test]
        public void PausingKeepsTheTrackButDropsTheProgressBar()
        {
            AddUntilStep("progress bar showing", () => client.Published?.StartUtc != null);

            AddStep("pause", () => service.Inputs = service.Inputs with { IsPlaying = false });

            AddUntilStep("bar gone", () => client.Published?.StartUtc == null);
            AddAssert("track still named", () => client.Published!.Details == "FREEDOM DIVE");
        }

        [Test]
        public void RunningOutOfSomethingToPlayTakesThePresenceDown()
        {
            AddUntilStep("presence sent", () => client.Published != null);

            AddStep("nothing playing", () => service.Silent = true);

            AddUntilStep("presence cleared", () => client.Published == null && client.ClearCount == 1);
        }

        /// <summary>
        /// Synthetic playback state, so the update policy can be tested without an audio track. The
        /// anchor instant is deliberately FIXED: with a moving anchor an idle track would drift past
        /// the republish tolerance mid-test and add updates nobody asked for.
        /// </summary>
        private partial class TestPresenceService : DiscordPresenceService
        {
            public PresenceInputs Inputs = new PresenceInputs(
                Title: "FREEDOM DIVE",
                Artist: "xi",
                Difficulty: null,
                HasStoryboard: false,
                RenderChart: false,
                IsPlaying: true,
                PositionMs: 0,
                LengthMs: 180_000,
                Rate: 1,
                NowUtc: new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

            public bool Silent;

            public TestPresenceService(IPresenceClient client)
                : base(client)
            {
            }

            internal override PresenceInputs? ReadInputs()
            {
                if (Silent)
                    return null;

                // The real one reads the live RenderChart bindable; mirror that so the config-driven
                // tests exercise the same path.
                return Inputs with { RenderChart = ChartIsRendering };
            }
        }

        private class FakePresenceClient : IPresenceClient
        {
            public int StartCount { get; private set; }
            public int PublishCount { get; private set; }
            public int ClearCount { get; private set; }
            public PresenceState? Published { get; private set; }

            public readonly List<PresenceState> All = new List<PresenceState>();

            public void Start() => StartCount++;

            public void Publish(PresenceState state)
            {
                PublishCount++;
                Published = state;
                All.Add(state);
            }

            public void Clear()
            {
                ClearCount++;
                Published = null;
            }

            public void ResetCounts()
            {
                PublishCount = 0;
                ClearCount = 0;
                All.Clear();
            }

            public void Dispose()
            {
            }
        }
    }
}
