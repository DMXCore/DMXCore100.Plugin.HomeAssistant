using System.Text.Json;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DMXCore100.HomeAssistantPlugin.Tests;

[TestClass]
public class HomeAssistantPluginTests
{
    private static async Task<(HomeAssistantPlugin Plugin, TestPluginHost Host)> CreateInitializedAsync(Action<TestPluginHost>? configure = null)
    {
        var plugin = new HomeAssistantPlugin();
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });

        host.EntityCatalog.Add(new PluginEntity { Code = "preset.PARTY", Name = "Party Mode", Kind = PluginEntityKind.Scene });
        host.EntityCatalog.Add(new PluginEntity { Code = "system.masterdimmer", Name = "Master Dimmer", Kind = PluginEntityKind.Level });
        host.EntityCatalog.Add(new PluginEntity { Code = "system.mute", Name = "Audio Mute", Kind = PluginEntityKind.Switch });
        host.EntityCatalog.Add(new PluginEntity { Code = "cv.SRC1", Name = "Bar Source", Kind = PluginEntityKind.Select, Choices = ["Spotify", "Mic"] });
        host.EntityCatalog.Add(new PluginEntity { Code = "system.nowplaying", Name = "Now Playing", Kind = PluginEntityKind.Sensor });
        host.EntityCatalog.Add(new PluginEntity { Code = "system.stop", Name = "Stop Playback", Kind = PluginEntityKind.Button });

        host.EntityStates["system.masterdimmer"] = new PluginEntityState { Code = "system.masterdimmer", Level = 0.8 };
        host.EntityStates["system.mute"] = new PluginEntityState { Code = "system.mute", IsOn = false };

        configure?.Invoke(host);

        // TestPluginHost delivers the initial OnConnectionChanged callback
        // synchronously, so the first PublishAll completes inside Initialize
        await plugin.InitializeAsync(host, CancellationToken.None);

        return (plugin, host);
    }

    private static async Task<(HomeAssistantPlugin Plugin, TestPluginHost Host, FakeHomeAssistantApi Api)> CreateWithHomeAssistantAsync(
        Action<TestPluginHost, FakeHomeAssistantApi>? configure = null)
    {
        var api = new FakeHomeAssistantApi
        {
            Scenes =
            [
                new HomeAssistantScene { EntityId = "scene.movie_night", Name = "Movie Night" },
                new HomeAssistantScene { EntityId = "scene.sunset", Name = "Sunset Show" },
            ],
        };
        var plugin = new HomeAssistantPlugin((_, _, _) => api);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.EntityCatalog.Add(new PluginEntity { Code = "cue.SUNSET", Name = "Sunset Show", Kind = PluginEntityKind.Scene });
        host.SetSetting("ha-url", "http://ha.local:8123");
        host.SetSetting("ha-token", "test-token");
        configure?.Invoke(host, api);
        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, api);
    }

    private static (string Topic, string Payload, bool Retain)? FindPublished(TestPluginHost host, string topic)
    {
        return host.PublishedMessages.Where(x => x.Topic == topic).Select(x => ((string, string, bool)?)x).LastOrDefault();
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 1000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "Timed out waiting for condition.");
    }

    [TestMethod]
    public async Task Initialize_PublishesDiscoveryConfigsForAllKinds()
    {
        var (_, host) = await CreateInitializedAsync();

        foreach (string topic in new[]
        {
            "homeassistant/scene/dmxcore-test-serial/preset_party/config",
            "homeassistant/number/dmxcore-test-serial/system_masterdimmer/config",
            "homeassistant/switch/dmxcore-test-serial/system_mute/config",
            "homeassistant/select/dmxcore-test-serial/cv_src1/config",
            "homeassistant/sensor/dmxcore-test-serial/system_nowplaying/config",
            "homeassistant/button/dmxcore-test-serial/system_stop/config",
        })
        {
            var message = FindPublished(host, topic);
            Assert.IsNotNull(message, $"missing config on {topic}");
            Assert.IsTrue(message.Value.Retain);
        }
    }

    [TestMethod]
    public async Task ConfigPayload_CarriesDeviceBlockAndDualAvailability()
    {
        var (_, host) = await CreateInitializedAsync();

        var message = FindPublished(host, "homeassistant/number/dmxcore-test-serial/system_masterdimmer/config");
        using var doc = JsonDocument.Parse(message!.Value.Payload);
        var root = doc.RootElement;

        Assert.AreEqual("Master Dimmer", root.GetProperty("name").GetString());
        Assert.AreEqual("dmxcore-test-serial-system_masterdimmer", root.GetProperty("unique_id").GetString());
        Assert.AreEqual("dmxcore-test-serial", root.GetProperty("device").GetProperty("identifiers")[0].GetString());
        Assert.AreEqual("DMX Core 100", root.GetProperty("device").GetProperty("model").GetString());
        Assert.AreEqual("all", root.GetProperty("availability_mode").GetString());
        Assert.AreEqual(2, root.GetProperty("availability").GetArrayLength());
        Assert.AreEqual("dmxcore/test-serial/system_masterdimmer/set", root.GetProperty("command_topic").GetString());
        Assert.AreEqual("%", root.GetProperty("unit_of_measurement").GetString());
    }

    [TestMethod]
    public async Task Initialize_PublishesCurrentStates()
    {
        var (_, host) = await CreateInitializedAsync();

        Assert.AreEqual("80", FindPublished(host, "dmxcore/test-serial/system_masterdimmer/state")!.Value.Payload);
        Assert.AreEqual("OFF", FindPublished(host, "dmxcore/test-serial/system_mute/state")!.Value.Payload);

        // No stored state for the select: nothing published
        Assert.IsNull(FindPublished(host, "dmxcore/test-serial/cv_src1/state"));
    }

    [TestMethod]
    public async Task Command_Scene_Activates()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/preset_party/set", "ON");

        var (code, command) = host.ExecutedEntityCommands.Single();
        Assert.AreEqual("preset.PARTY", code);
        Assert.AreEqual(PluginEntityCommandType.Activate, command.Type);
    }

    [TestMethod]
    public async Task Command_Level_ScalesFromPercent()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/system_masterdimmer/set", "40");

        var (code, command) = host.ExecutedEntityCommands.Single();
        Assert.AreEqual("system.masterdimmer", code);
        Assert.AreEqual(PluginEntityCommandType.SetLevel, command.Type);
        Assert.AreEqual(0.4, command.Level!.Value, 1e-9);
    }

    [TestMethod]
    public async Task Command_Switch_OnOff()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/system_mute/set", "ON");
        await host.SimulateMqttMessageAsync("dmxcore/test-serial/system_mute/set", "OFF");

        Assert.AreEqual(PluginEntityCommandType.TurnOn, host.ExecutedEntityCommands[0].Command.Type);
        Assert.AreEqual(PluginEntityCommandType.TurnOff, host.ExecutedEntityCommands[1].Command.Type);
    }

    [TestMethod]
    public async Task Command_Select_SetsChoice()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/cv_src1/set", "Mic");

        var (code, command) = host.ExecutedEntityCommands.Single();
        Assert.AreEqual("cv.SRC1", code);
        Assert.AreEqual(PluginEntityCommandType.SetChoice, command.Type);
        Assert.AreEqual("Mic", command.Choice);
    }

    [TestMethod]
    public async Task Command_UnknownObjectId_Ignored()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/nope/set", "ON");

        Assert.AreEqual(0, host.ExecutedEntityCommands.Count);
    }

    [TestMethod]
    public async Task StateChange_PublishesFormattedValue()
    {
        var (_, host) = await CreateInitializedAsync();
        host.PublishedMessages.Clear();

        await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.masterdimmer", Level = 0.25 });

        var message = host.PublishedMessages.Single();
        Assert.AreEqual("dmxcore/test-serial/system_masterdimmer/state", message.Topic);
        Assert.AreEqual("25", message.Payload);
        Assert.IsTrue(message.Retain);
    }

    [TestMethod]
    public async Task CatalogChange_RemovesGhostConfig()
    {
        var (_, host) = await CreateInitializedAsync();
        host.EntityCatalog.RemoveAll(x => x.Code == "preset.PARTY");
        host.PublishedMessages.Clear();

        await host.SimulateEntityCatalogChangedAsync();

        var removal = FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config");
        Assert.IsNotNull(removal);
        Assert.AreEqual(string.Empty, removal.Value.Payload);
        Assert.IsTrue(removal.Value.Retain);
    }

    [TestMethod]
    public async Task HaBirthMessage_Republishes()
    {
        var (_, host) = await CreateInitializedAsync();
        host.PublishedMessages.Clear();

        await host.SimulateMqttMessageAsync("homeassistant/status", "online");

        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config"));
    }

    [TestMethod]
    public async Task Reconnect_Republishes()
    {
        var (_, host) = await CreateInitializedAsync();
        host.PublishedMessages.Clear();

        await host.SimulateMqttConnectionChangedAsync(false);
        await host.SimulateMqttConnectionChangedAsync(true);

        Assert.IsNotNull(FindPublished(host, "homeassistant/switch/dmxcore-test-serial/system_mute/config"));
    }

    [TestMethod]
    public async Task ExposeToggle_FiltersNamespace()
    {
        var (_, host) = await CreateInitializedAsync(h => h.SetSetting("expose-scenes", "false"));

        Assert.IsNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config"));
        Assert.IsNotNull(FindPublished(host, "homeassistant/number/dmxcore-test-serial/system_masterdimmer/config"));
    }

    [TestMethod]
    public async Task ExposedLooks_AllowList_PublishesOnlyMatches()
    {
        var (_, host) = await CreateInitializedAsync(h =>
        {
            h.EntityCatalog.Add(new PluginEntity { Code = "cue.SUNSET", Name = "Sunset Show", Kind = PluginEntityKind.Scene });
            h.EntityCatalog.Add(new PluginEntity { Code = "timeline.SHOW", Name = "Main Show", Kind = PluginEntityKind.Scene });
            h.SetSetting("exposed-looks", "Party Mode, cue.SUNSET");
        });

        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config"));
        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/cue_sunset/config"));
        Assert.IsNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/timeline_show/config"));
        Assert.IsNotNull(FindPublished(host, "homeassistant/number/dmxcore-test-serial/system_masterdimmer/config"));
    }

    [TestMethod]
    public async Task ExposedLooks_Change_ClearsUnlistedLooks()
    {
        var (_, host) = await CreateInitializedAsync(h =>
            h.EntityCatalog.Add(new PluginEntity { Code = "cue.SUNSET", Name = "Sunset Show", Kind = PluginEntityKind.Scene }));

        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config"));
        host.PublishedMessages.Clear();

        host.SetSetting("exposed-looks", "cue.SUNSET");
        await host.TriggerSettingsChangedAsync();

        var removal = FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config");
        Assert.IsNotNull(removal);
        Assert.AreEqual(string.Empty, removal.Value.Payload);
        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/cue_sunset/config"));
    }

    [TestMethod]
    public async Task PrefixChange_MovesConfigsAndClearsOldPrefix()
    {
        var (_, host) = await CreateInitializedAsync();
        host.PublishedMessages.Clear();

        host.SetSetting("discovery-prefix", "ha2");
        await host.TriggerSettingsChangedAsync();

        // Old prefix cleared with an empty retained payload, new prefix live
        var removal = FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config");
        Assert.IsNotNull(removal);
        Assert.AreEqual(string.Empty, removal.Value.Payload);
        Assert.IsNotNull(FindPublished(host, "ha2/scene/dmxcore-test-serial/preset_party/config"));
    }

    [TestMethod]
    public async Task SelectWithoutChoices_Skipped()
    {
        var (_, host) = await CreateInitializedAsync(h =>
            h.EntityCatalog.Add(new PluginEntity { Code = "cv.EMPTY", Name = "Empty", Kind = PluginEntityKind.Select }));

        Assert.IsNull(FindPublished(host, "homeassistant/select/dmxcore-test-serial/cv_empty/config"));
    }

    [TestMethod]
    public async Task DisconnectedAtInit_PublishesNothingUntilConnect()
    {
        var (_, host) = await CreateInitializedAsync(h => h.MqttConnected = false);

        Assert.AreEqual(0, host.PublishedMessages.Count);

        await host.SimulateMqttConnectionChangedAsync(true);

        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config"));
    }

    [TestMethod]
    public async Task Initialize_WithToken_DiscoversScenesAndPersists()
    {
        var (plugin, host, _) = await CreateWithHomeAssistantAsync();

        Assert.AreEqual(2, plugin.DiscoveredScenes.Count);
        Assert.IsTrue(host.ConnectionDetail!.Contains("2 HA scenes"));
        Assert.IsTrue(host.StateJson!.Contains("scene.movie_night"));
    }

    [TestMethod]
    public async Task Initialize_RestoresPersistedScenesAndSkipsBlankEntityIds()
    {
        var (plugin, _) = await CreateInitializedAsync(host =>
        {
            host.StateJson = JsonSerializer.Serialize(new PublishRecord
            {
                Scenes =
                [
                    new HomeAssistantScene { EntityId = "scene.movie_night", Name = "Movie Night" },
                    new HomeAssistantScene { EntityId = "   ", Name = "Blank" },
                    new HomeAssistantScene { EntityId = "scene.sunset", Name = "Sunset Show" },
                ],
            });
        });

        CollectionAssert.AreEqual(
            new[] { "Movie Night", "Sunset Show" },
            plugin.DiscoveredScenes.Select(scene => scene.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "scene.movie_night", "scene.sunset" },
            plugin.DiscoveredScenes.Select(scene => scene.EntityId).ToArray());
    }

    [TestMethod]
    public async Task Command_HaScene_Activates()
    {
        var (_, host, api) = await CreateWithHomeAssistantAsync();

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/ha-scene/scene_movie_night/set", "ON");

        CollectionAssert.AreEqual(new[] { "scene.movie_night" }, api.Activated);
    }

    [TestMethod]
    public async Task CueStarted_MatchingName_ActivatesScene()
    {
        var (_, host, api) = await CreateWithHomeAssistantAsync();

        await host.SimulateCueStartedAsync("cue.SUNSET");

        CollectionAssert.AreEqual(new[] { "scene.sunset" }, api.Activated);
    }

    [TestMethod]
    public async Task CueStarted_DisplayName_MatchesHaSlug()
    {
        var (_, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.EntityCatalog.Add(new PluginEntity { Code = "cue.F1GO", Name = "F1 Go", Kind = PluginEntityKind.Scene });
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.f1_go", Name = "F1 Go" });
        });

        await host.SimulateCueStartedAsync("F1GO");

        CollectionAssert.AreEqual(new[] { "scene.f1_go" }, api.Activated);
    }

    [TestMethod]
    public async Task NowPlaying_DisplayName_ActivatesScene()
    {
        var (_, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.EntityCatalog.Add(new PluginEntity { Code = "preset.F1GO", Name = "F1 Go", Kind = PluginEntityKind.Scene });
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.f1_go", Name = "F1 Go" });
        });

        await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.nowplaying", Text = "F1 Go" });

        CollectionAssert.AreEqual(new[] { "scene.f1_go" }, api.Activated);
    }

    [TestMethod]
    public async Task CueStartedAndNowPlaying_SameScene_ActivatesOnce()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.EntityCatalog.Add(new PluginEntity { Code = "cue.F1GO", Name = "F1 Go", Kind = PluginEntityKind.Scene });
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.f1_go", Name = "F1 Go" });
            fake.ActivateHook = async (_, _) =>
            {
                firstCallStarted.TrySetResult();
                await hold.Task;
            };
        });

        Task cue = host.SimulateCueStartedAsync("F1GO");
        Task nowPlaying = host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.nowplaying", Text = "F1 Go" });
        await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hold.TrySetResult();
        await Task.WhenAll(cue, nowPlaying);

        CollectionAssert.AreEqual(new[] { "scene.f1_go" }, api.Activated);
    }

    [TestMethod]
    public async Task CueStarted_Disabled_DoesNotActivate()
    {
        var (_, host, api) = await CreateWithHomeAssistantAsync((h, _) =>
            h.SetSetting("activate-scenes-from-cues", "false"));

        await host.SimulateCueStartedAsync("cue.SUNSET");

        Assert.AreEqual(0, api.Activated.Count);
    }

    [TestMethod]
    public async Task CueEnded_ConfiguredStopScene_ActivatesAfterSettle()
    {
        var (plugin, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.SetSetting("stop-ha-scene", "All Off");
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.all_off", Name = "All Off" });
        });
        plugin.StopSceneSettle = TimeSpan.FromMilliseconds(20);

        await host.SimulateCueEndedAsync("cue.SUNSET");
        Assert.AreEqual(0, api.Activated.Count);

        await WaitUntil(() => api.Activated.Count > 0);
        CollectionAssert.AreEqual(new[] { "scene.all_off" }, api.Activated);
    }

    [TestMethod]
    public async Task CueEndedThenStarted_DoesNotActivateStopScene()
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (plugin, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.SetSetting("stop-ha-scene", "All Off");
            h.SetSetting("activate-scenes-from-cues", "false");
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.all_off", Name = "All Off" });
        });
        plugin.DelayAsync = async (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            try
            {
                await holdDelay.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                delayFinished.TrySetResult();
            }
        };

        await host.SimulateCueEndedAsync("cue.SUNSET");
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.SimulateCueStartedAsync("cue.SUNSET");
        await delayFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, api.Activated.Count);
    }

    [TestMethod]
    public async Task NowPlayingIdle_ActivatesStopScene()
    {
        var (plugin, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.SetSetting("stop-ha-scene", "scene.all_off");
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.all_off", Name = "All Off" });
        });
        plugin.StopSceneSettle = TimeSpan.FromMilliseconds(20);

        await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.nowplaying", Text = "" });
        await WaitUntil(() => api.Activated.Count > 0);

        CollectionAssert.AreEqual(new[] { "scene.all_off" }, api.Activated);
    }

    [TestMethod]
    public async Task Command_SystemStop_ActivatesStopScene()
    {
        var (plugin, host, api) = await CreateWithHomeAssistantAsync((h, fake) =>
        {
            h.SetSetting("stop-ha-scene", "All Off");
            h.EntityCatalog.Add(new PluginEntity { Code = "system.stop", Name = "Stop Playback", Kind = PluginEntityKind.Button });
            fake.Scenes.Add(new HomeAssistantScene { EntityId = "scene.all_off", Name = "All Off" });
        });
        plugin.StopSceneSettle = TimeSpan.FromMilliseconds(20);

        await host.SimulateMqttMessageAsync("dmxcore/test-serial/system_stop/set", "PRESS");
        await WaitUntil(() => api.Activated.Count > 0);

        CollectionAssert.AreEqual(new[] { "scene.all_off" }, api.Activated);
    }

    [TestMethod]
    public async Task CueEnded_EmptyStopSetting_DoesNothing()
    {
        var delayRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (plugin, host, api) = await CreateWithHomeAssistantAsync();
        plugin.DelayAsync = (_, _) =>
        {
            delayRan.TrySetResult();
            return Task.CompletedTask;
        };

        await host.SimulateCueEndedAsync("cue.SUNSET");

        Assert.IsFalse(delayRan.Task.IsCompleted);
        Assert.AreEqual(0, api.Activated.Count);
    }

    [TestMethod]
    public async Task Mdns_FillsUrlWhenSettingEmpty()
    {
        string? usedUrl = null;
        var api = new FakeHomeAssistantApi
        {
            Scenes = [new HomeAssistantScene { EntityId = "scene.only", Name = "Only" }],
        };
        var plugin = new HomeAssistantPlugin((url, _, _) =>
        {
            usedUrl = url;
            return api;
        });
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting("ha-token", "test-token");
        host.MdnsServices[HomeAssistantUrl.MdnsServiceType] =
        [
            new MdnsServiceInfo
            {
                InstanceName = "Home",
                Address = "192.168.1.20",
                Port = 8123,
                Properties = new Dictionary<string, string>
                {
                    ["internal_url"] = "http://homeassistant.local:8123",
                },
            },
        ];

        await plugin.InitializeAsync(host, CancellationToken.None);

        Assert.AreEqual("http://homeassistant.local:8123", usedUrl);
        Assert.AreEqual(1, plugin.DiscoveredScenes.Count);
    }

    [TestMethod]
    public async Task RefreshFailure_ReportsHaErrorWithoutDroppingMqtt()
    {
        var (_, host, _) = await CreateWithHomeAssistantAsync((_, api) =>
            api.GetError = new HttpRequestException("connection refused"));

        Assert.AreEqual(true, host.ConnectionState);
        Assert.IsTrue(host.ConnectionDetail!.Contains("HA: connection refused"));
    }

    [TestMethod]
    public async Task HaMqttBroker_PublishesDiscoveryInAdditionToCoreMqtt()
    {
        var mqtt = new FakeHomeAssistantMqttBroker();
        var plugin = new HomeAssistantPlugin(apiFactory: null, _ => mqtt);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.EntityCatalog.Add(new PluginEntity { Code = "preset.PARTY", Name = "Party Mode", Kind = PluginEntityKind.Scene });

        await plugin.InitializeAsync(host, CancellationToken.None);

        Assert.IsTrue(mqtt.IsConnected);
        Assert.IsTrue(mqtt.Published.Any(x => x.Topic.Contains("/preset_party/config")));
        Assert.IsNotNull(FindPublished(host, "homeassistant/scene/dmxcore-test-serial/preset_party/config"));
        Assert.IsTrue(host.ConnectionDetail!.Contains("HA MQTT connected"));
    }

    [TestMethod]
    public async Task HaMqttBroker_StartFailure_ReportsMqttError()
    {
        var mqtt = new FakeHomeAssistantMqttBroker { StartError = new InvalidOperationException("broker down") };
        var plugin = new HomeAssistantPlugin(apiFactory: null, _ => mqtt);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });

        await plugin.InitializeAsync(host, CancellationToken.None);

        Assert.IsFalse(mqtt.IsConnected);
        Assert.IsTrue(host.ConnectionDetail!.Contains("HA MQTT: broker down"));
    }

    private sealed class FakeHomeAssistantApi : IHomeAssistantApi
    {
        public List<HomeAssistantScene> Scenes { get; set; } = [];

        public List<string> Activated { get; } = [];

        public Func<string, CancellationToken, Task>? ActivateHook { get; set; }

        public Exception? GetError { get; set; }

        public Task<IReadOnlyList<HomeAssistantScene>> GetScenesAsync(CancellationToken cancellationToken)
        {
            if (this.GetError != null)
            {
                throw this.GetError;
            }

            return Task.FromResult<IReadOnlyList<HomeAssistantScene>>(this.Scenes);
        }

        public async Task ActivateSceneAsync(string entityId, CancellationToken cancellationToken)
        {
            this.Activated.Add(entityId);
            if (this.ActivateHook != null)
            {
                await this.ActivateHook(entityId, cancellationToken);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeHomeAssistantMqttBroker : IHomeAssistantMqttBroker
    {
        public bool IsConnected { get; private set; }

        public Exception? StartError { get; set; }

        public List<(string Topic, string Payload, bool Retain)> Published { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (this.StartError != null)
            {
                throw this.StartError;
            }

            this.IsConnected = true;
            return Task.CompletedTask;
        }

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
        {
            this.Published.Add((topic, payload, retain));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
