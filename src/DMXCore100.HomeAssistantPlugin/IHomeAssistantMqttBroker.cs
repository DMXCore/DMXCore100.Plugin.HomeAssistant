using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Optional second MQTT connection — Home Assistant's Mosquitto (or any
/// broker the HA MQTT integration uses) — so discovery can land there even
/// when the Core's shared MQTT connection points somewhere else.
/// </summary>
internal interface IHomeAssistantMqttBroker : IAsyncDisposable
{
    bool IsConnected { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken);
}

internal delegate IHomeAssistantMqttBroker HomeAssistantMqttFactory(IPluginHost host);
