using System.Globalization;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Home Assistant MQTT Discovery integration: mirrors the device's entity
/// catalog (presets, cues, timelines, schedules, zones, Control Values,
/// system state) onto the configured MQTT broker so Home Assistant creates
/// the corresponding entities automatically, grouped under one device.
/// openHAB, ioBroker, and Domoticz consume the same discovery format.
/// </summary>
/// <remarks>
/// Topic layout (serial is the device hardware id):
/// <code>
/// {prefix}/{component}/dmxcore-{serial}/{objectId}/config   retained discovery
/// dmxcore/{serial}/{objectId}/state                         retained state
/// dmxcore/{serial}/{objectId}/set                           commands from HA
/// </code>
/// All handlers run on the plugin's serial dispatch queue, so no locking is
/// needed around the published-entity map.
/// </remarks>
public class HomeAssistantPlugin : IPlugin
{
    private const string DiscoveryPrefixKey = "discovery-prefix";
    private const string ExposeScenesKey = "expose-scenes";
    private const string ExposeSchedulesKey = "expose-schedules";
    private const string ExposeZonesKey = "expose-zones";
    private const string ExposeControlValuesKey = "expose-control-values";
    private const string ExposeSystemKey = "expose-system";

    private IPluginHost host = null!;
    private readonly List<IDisposable> subscriptions = [];
    private IDisposable? statusSubscription;
    private string statusPrefix = "";

    // Entities currently published to HA, keyed by objectId (rebuilt by every
    // PublishAll; commands and state changes resolve through this)
    private Dictionary<string, PluginEntity> published = new(StringComparer.OrdinalIgnoreCase);

    public PluginInfo Info { get; } = new()
    {
        Id = "home-assistant",
        Name = "Home Assistant",
        Version = "1.0.0",
        Description = "Publishes the device's entities to Home Assistant via MQTT Discovery (also works with openHAB, ioBroker, and Domoticz).",
        Settings =
        [
            new()
            {
                Key = DiscoveryPrefixKey,
                Label = "Discovery prefix",
                Type = PluginSettingType.String,
                DefaultValue = "homeassistant",
                Description = "Home Assistant's MQTT discovery prefix (leave at homeassistant unless changed in HA)",
            },
            new()
            {
                Key = ExposeScenesKey,
                Label = "Expose presets, cues, and timelines",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
            new()
            {
                Key = ExposeSchedulesKey,
                Label = "Expose schedules",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
            new()
            {
                Key = ExposeZonesKey,
                Label = "Expose zones",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
            new()
            {
                Key = ExposeControlValuesKey,
                Label = "Expose Control Values",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
            new()
            {
                Key = ExposeSystemKey,
                Label = "Expose system entities (master dimmer, mute, now playing)",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
        ],
    };

    private string Serial => this.host.DeviceInfo.Serial.ToLowerInvariant();

    private string DiscoveryPrefix => (this.host.Settings.GetString(DiscoveryPrefixKey) ?? "homeassistant").Trim().TrimEnd('/');

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.host = host;

        // Commands from HA arrive on the per-entity set topics
        this.subscriptions.Add(host.Mqtt.Subscribe($"dmxcore/{Serial}/+/set", HandleCommand));

        // HA publishes a birth message on restart; retained configs usually
        // survive, but republishing is the belt-and-suspenders standard
        SubscribeStatusTopic();

        this.subscriptions.Add(host.Entities.OnStateChanged(HandleEntityState));
        this.subscriptions.Add(host.Entities.OnCatalogChanged(HandleCatalogChanged));
        this.subscriptions.Add(host.Settings.OnChanged(HandleSettingsChanged));

        // Last: the initial callback (current state first) kicks off the
        // first PublishAll when the broker is already connected
        this.subscriptions.Add(host.Mqtt.OnConnectionChanged(HandleConnectionChanged));

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in this.subscriptions)
        {
            subscription.Dispose();
        }

        this.statusSubscription?.Dispose();

        return Task.CompletedTask;
    }

    private void SubscribeStatusTopic()
    {
        this.statusSubscription?.Dispose();
        this.statusPrefix = DiscoveryPrefix;
        this.statusSubscription = this.host.Mqtt.Subscribe($"{this.statusPrefix}/status", HandleHaStatus);
    }

    private async Task HandleConnectionChanged(bool connected, CancellationToken cancellationToken)
    {
        if (connected)
        {
            await PublishAll(cancellationToken);
        }
    }

    private async Task HandleHaStatus(MqttMessage message, CancellationToken cancellationToken)
    {
        if (string.Equals(message.Payload.Trim(), "online", StringComparison.OrdinalIgnoreCase))
        {
            this.host.Logger.LogInformation("Home Assistant came online; republishing discovery");
            await PublishAll(cancellationToken);
        }
    }

    private Task HandleCatalogChanged(CancellationToken cancellationToken)
    {
        return PublishAll(cancellationToken);
    }

    private Task HandleSettingsChanged(CancellationToken cancellationToken)
    {
        if (!string.Equals(this.statusPrefix, DiscoveryPrefix, StringComparison.Ordinal))
        {
            SubscribeStatusTopic();
        }

        // PublishAll clears configs left under a previous prefix via the
        // persisted publish record
        return PublishAll(cancellationToken);
    }

    /// <summary>
    /// Publish (or re-publish) the full discovery set: remove configs for
    /// entities that disappeared (or a changed prefix), then publish configs
    /// and current states for every exposed entity.
    /// </summary>
    private async Task PublishAll(CancellationToken cancellationToken)
    {
        if (!this.host.Mqtt.IsConnected)
        {
            return;
        }

        string prefix = DiscoveryPrefix;
        var catalog = await this.host.Entities.GetCatalogAsync(cancellationToken);

        var map = new Dictionary<string, PluginEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in catalog.Where(IsExposed))
        {
            // A Select with no choices is not representable in HA
            if (entity.Kind == PluginEntityKind.Select && entity.Choices.Count == 0)
            {
                continue;
            }

            string objectId = Discovery.ObjectId(entity.Code);
            if (!map.TryAdd(objectId, entity))
            {
                this.host.Logger.LogWarning("Entity {Code} collides with {Other} on object id {ObjectId}; skipped",
                    entity.Code, map[objectId].Code, objectId);
            }
        }

        // Clear ghosts: previously published configs whose entity (or prefix)
        // is gone. Publishing an empty retained payload deletes the retained
        // config, which removes the entity in HA.
        var record = await PublishRecord.Load(this.host, cancellationToken);

        foreach (var entry in record.Entries)
        {
            bool stillPublished = string.Equals(record.Prefix, prefix, StringComparison.Ordinal)
                && map.TryGetValue(entry.ObjectId, out var current)
                && string.Equals(Discovery.Component(current.Kind), entry.Component, StringComparison.Ordinal);

            if (!stillPublished)
            {
                await this.host.Mqtt.PublishAsync(
                    Discovery.ConfigTopic(record.Prefix, entry.Component, Serial, entry.ObjectId),
                    string.Empty, retain: true, MqttQos.AtLeastOnce, cancellationToken);
            }
        }

        // Publish configs + current state
        foreach (var (objectId, entity) in map)
        {
            string component = Discovery.Component(entity.Kind);

            await this.host.Mqtt.PublishAsync(
                Discovery.ConfigTopic(prefix, component, Serial, objectId),
                Discovery.ConfigPayload(this.host, Serial, objectId, entity),
                retain: true, MqttQos.AtLeastOnce, cancellationToken);

            var state = await this.host.Entities.GetStateAsync(entity.Code, cancellationToken);
            string? stateValue = Discovery.StateValue(entity.Kind, state);
            if (stateValue != null)
            {
                await this.host.Mqtt.PublishAsync(
                    Discovery.StateTopic(Serial, objectId), stateValue,
                    retain: true, MqttQos.AtLeastOnce, cancellationToken);
            }
        }

        this.published = map;

        await PublishRecord.Save(this.host, prefix, map, cancellationToken);

        this.host.Logger.LogInformation("Published {Count} entities to discovery prefix '{Prefix}'", map.Count, prefix);
        this.host.SetConnectionState(true, $"{map.Count} entities published");
    }

    private async Task HandleEntityState(PluginEntityState state, CancellationToken cancellationToken)
    {
        if (!this.host.Mqtt.IsConnected)
        {
            return;
        }

        string objectId = Discovery.ObjectId(state.Code);
        if (!this.published.TryGetValue(objectId, out var entity))
        {
            return;
        }

        string? stateValue = Discovery.StateValue(entity.Kind, state);
        if (stateValue != null)
        {
            await this.host.Mqtt.PublishAsync(Discovery.StateTopic(Serial, objectId), stateValue,
                retain: true, MqttQos.AtLeastOnce, cancellationToken);
        }
    }

    private async Task HandleCommand(MqttMessage message, CancellationToken cancellationToken)
    {
        // dmxcore/{serial}/{objectId}/set
        string[] parts = message.Topic.Split('/');
        if (parts.Length != 4)
        {
            return;
        }

        string objectId = parts[2];
        if (!this.published.TryGetValue(objectId, out var entity))
        {
            this.host.Logger.LogDebug("Command for unknown object id {ObjectId} ignored", objectId);

            return;
        }

        var command = ParseCommand(entity.Kind, message.Payload);
        if (command == null)
        {
            this.host.Logger.LogWarning("Unparseable command payload '{Payload}' for {Code} ignored", message.Payload, entity.Code);

            return;
        }

        await this.host.Entities.ExecuteAsync(entity.Code, command, cancellationToken);
    }

    private static PluginEntityCommand? ParseCommand(PluginEntityKind kind, string payload)
    {
        string text = payload.Trim();

        switch (kind)
        {
            case PluginEntityKind.Scene:
            case PluginEntityKind.Button:
                // HA sends payload_on ("ON") / payload_press ("PRESS")
                return PluginEntityCommand.Activate();

            case PluginEntityKind.Switch:
                if (string.Equals(text, "ON", StringComparison.OrdinalIgnoreCase))
                    return PluginEntityCommand.TurnOn();
                if (string.Equals(text, "OFF", StringComparison.OrdinalIgnoreCase))
                    return PluginEntityCommand.TurnOff();

                return null;

            case PluginEntityKind.Level:
                // HA number entities send the percent value
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                    return PluginEntityCommand.SetLevel(Math.Clamp(percent / 100.0, 0.0, 1.0));

                return null;

            case PluginEntityKind.Select:
                return text.Length > 0 ? PluginEntityCommand.SetChoice(text) : null;

            default:
                return null;
        }
    }

    private bool IsExposed(PluginEntity entity)
    {
        string? settingKey = entity.Code.Split('.')[0].ToLowerInvariant() switch
        {
            "preset" or "cue" or "timeline" => ExposeScenesKey,
            "schedule" => ExposeSchedulesKey,
            "zone" => ExposeZonesKey,
            "cv" => ExposeControlValuesKey,
            "system" => ExposeSystemKey,
            _ => null,
        };

        // Unknown (future) namespaces default to exposed
        return settingKey == null || this.host.Settings.GetBoolean(settingKey) != false;
    }
}
