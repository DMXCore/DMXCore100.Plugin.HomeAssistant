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

    private static (string Topic, string Payload, bool Retain)? FindPublished(TestPluginHost host, string topic)
    {
        return host.PublishedMessages.Where(x => x.Topic == topic).Select(x => ((string, string, bool)?)x).LastOrDefault();
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
}
