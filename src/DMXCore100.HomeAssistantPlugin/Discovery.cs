using System.Globalization;
using System.Text.Json;
using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Pure helpers for Home Assistant MQTT Discovery: topic construction,
/// object-id derivation, config payload building, and state formatting.
/// </summary>
public static class Discovery
{
    /// <summary>
    /// HA object id for an entity code: lowercase with every character
    /// outside [a-z0-9] replaced by an underscore ("preset.PARTY" →
    /// "preset_party"). Deterministic, so state changes and commands resolve
    /// without a stored mapping.
    /// </summary>
    public static string ObjectId(string entityCode)
    {
        return new string(entityCode.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray());
    }

    /// <summary>
    /// The HA component (integration type) an entity kind maps to.
    /// </summary>
    public static string Component(PluginEntityKind kind) => kind switch
    {
        PluginEntityKind.Scene => "scene",
        PluginEntityKind.Switch => "switch",
        PluginEntityKind.Level => "number",
        PluginEntityKind.Select => "select",
        PluginEntityKind.Button => "button",
        PluginEntityKind.Sensor => "sensor",
        _ => "sensor",
    };

    public static string ConfigTopic(string prefix, string component, string serial, string objectId)
        => $"{prefix}/{component}/dmxcore-{serial}/{objectId}/config";

    public static string StateTopic(string serial, string objectId)
        => $"dmxcore/{serial}/{objectId}/state";

    public static string CommandTopic(string serial, string objectId)
        => $"dmxcore/{serial}/{objectId}/set";

    /// <summary>
    /// The retained discovery config document for an entity.
    /// </summary>
    public static string ConfigPayload(IPluginHost host, string serial, string objectId, PluginEntity entity)
    {
        var device = host.DeviceInfo;

        var doc = new Dictionary<string, object?>
        {
            ["name"] = entity.Name,
            ["unique_id"] = $"dmxcore-{serial}-{objectId}",
            ["availability"] = new object[]
            {
                new Dictionary<string, object?> { ["topic"] = host.Mqtt.DeviceAvailabilityTopic },
                new Dictionary<string, object?> { ["topic"] = host.Mqtt.PluginAvailabilityTopic },
            },
            // Unavailable when the device drops off the broker OR this plugin
            // is stopped/disabled
            ["availability_mode"] = "all",
            ["device"] = new Dictionary<string, object?>
            {
                ["identifiers"] = new[] { $"dmxcore-{serial}" },
                ["name"] = device.DeviceName,
                ["model"] = device.ProductName,
                ["sw_version"] = device.SoftwareVersion,
            },
            ["origin"] = new Dictionary<string, object?>
            {
                ["name"] = device.ProductName,
                ["sw"] = device.SoftwareVersion,
            },
        };

        string stateTopic = StateTopic(serial, objectId);
        string commandTopic = CommandTopic(serial, objectId);

        switch (entity.Kind)
        {
            case PluginEntityKind.Scene:
                doc["command_topic"] = commandTopic;
                doc["payload_on"] = "ON";
                break;

            case PluginEntityKind.Button:
                doc["command_topic"] = commandTopic;
                doc["payload_press"] = "PRESS";
                break;

            case PluginEntityKind.Switch:
                doc["command_topic"] = commandTopic;
                doc["state_topic"] = stateTopic;
                doc["payload_on"] = "ON";
                doc["payload_off"] = "OFF";
                break;

            case PluginEntityKind.Level:
                doc["command_topic"] = commandTopic;
                doc["state_topic"] = stateTopic;
                doc["min"] = 0;
                doc["max"] = 100;
                doc["step"] = 1;
                doc["unit_of_measurement"] = "%";
                doc["mode"] = "slider";
                break;

            case PluginEntityKind.Select:
                doc["command_topic"] = commandTopic;
                doc["state_topic"] = stateTopic;
                doc["options"] = entity.Choices;
                break;

            case PluginEntityKind.Sensor:
                doc["state_topic"] = stateTopic;
                break;
        }

        return JsonSerializer.Serialize(doc);
    }

    /// <summary>
    /// The state-topic payload for an entity state, or null when the state
    /// carries no value for the kind (nothing should be published).
    /// </summary>
    public static string? StateValue(PluginEntityKind kind, PluginEntityState? state)
    {
        if (state == null)
        {
            return null;
        }

        return kind switch
        {
            PluginEntityKind.Switch when state.IsOn.HasValue => state.IsOn.Value ? "ON" : "OFF",
            PluginEntityKind.Level when state.Level.HasValue =>
                Math.Round(Math.Clamp(state.Level.Value, 0.0, 1.0) * 100.0).ToString(CultureInfo.InvariantCulture),
            PluginEntityKind.Select when state.Choice != null => state.Choice,
            PluginEntityKind.Sensor when state.Text != null => state.Text,
            _ => null,
        };
    }
}
