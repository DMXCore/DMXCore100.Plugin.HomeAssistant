using System.Globalization;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Home Assistant integration, both directions:
/// <list type="bullet">
/// <item>HA→Core (MQTT Discovery): mirrors the device's entity catalog
/// (presets, cues, timelines, schedules, zones, Control Values, system
/// state) onto the configured MQTT broker so Home Assistant creates the
/// corresponding entities automatically, grouped under one device. openHAB,
/// ioBroker, and Domoticz consume the same discovery format.</item>
/// <item>Core→HA (REST): when a Home Assistant URL and long-lived access
/// token are configured, the plugin registers an output action provider so
/// HA scenes, scripts, and automations can be fired from the device's
/// Output Events (and thus from control surfaces, custom menus, input
/// triggers, timelines, and scripts).</item>
/// </list>
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
    private const string ExposedLooksKey = "exposed-looks";
    private const string ExposeSchedulesKey = "expose-schedules";
    private const string ExposeZonesKey = "expose-zones";
    private const string ExposeControlValuesKey = "expose-control-values";
    private const string ExposeSystemKey = "expose-system";
    private const string HaUrlKey = "ha-url";
    private const string HaTokenKey = "ha-token";
    private const string StopHaSceneKey = "stop-ha-scene";
    private const string HaMqttBrokerKey = "ha-mqtt-broker";
    private const string HaMqttPortKey = "ha-mqtt-port";
    private const string HaMqttUsernameKey = "ha-mqtt-username";
    private const string HaMqttPasswordKey = "ha-mqtt-password";
    private const string HaMqttTlsKey = "ha-mqtt-tls";

    private IPluginHost host = null!;
    private readonly List<IDisposable> subscriptions = [];
    private readonly HomeAssistantMqttFactory? mqttFactory;
    private IDisposable? statusSubscription;
    private string statusPrefix = "";

    // Core→HA REST side: present only while URL + token are configured
    private HaRestClient? restClient;
    private HaActionProvider? actionProvider;
    private IDisposable? actionProviderHandle;
    private IDisposable? restHealthSchedule;
    private string restConfigKey = "";
    private int publishedCount;
    private bool? restHealthy;
    private string? restHealthDetail;

    private IHomeAssistantMqttBroker? haMqtt;
    private string haMqttConfigKey = "";
    private CancellationTokenSource? stopSceneDelay;
    private readonly HashSet<string> playingCueCodes = new(StringComparer.OrdinalIgnoreCase);
    private string? stopSceneResolvedId;
    private string? stopSceneResolvedSetting;

    // Entities currently published to HA, keyed by objectId (rebuilt by every
    // PublishAll; commands and state changes resolve through this)
    private Dictionary<string, PluginEntity> published = new(StringComparer.OrdinalIgnoreCase);

    public HomeAssistantPlugin()
        : this(null)
    {
    }

    internal HomeAssistantPlugin(HomeAssistantMqttFactory? mqttFactory)
    {
        this.mqttFactory = mqttFactory;
    }

    internal TimeSpan StopSceneSettle { get; set; } = TimeSpan.FromMilliseconds(400);

    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    internal HttpMessageHandler? RestHandler { get; set; }

    internal string? CachedStopSceneEntityId => this.stopSceneResolvedId;

    public PluginInfo Info { get; } = new()
    {
        // Id/Name/Version come from the csproj (PluginId, PluginDisplayName,
        // Version) via the SDK-generated PluginBuildInfo, always in sync with
        // the generated manifest.json
        Id = PluginBuildInfo.Id,
        Name = PluginBuildInfo.Name,
        Version = PluginBuildInfo.Version,
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
                Key = ExposedLooksKey,
                Label = "Only these presets, cues, and timelines",
                Type = PluginSettingType.String,
                Description = "Comma or newline separated names or codes. Empty publishes every look when the category is on.",
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
            new()
            {
                Key = HaUrlKey,
                Label = "Home Assistant URL",
                Type = PluginSettingType.String,
                Description = "Lets the device fire HA scenes, scripts, and automations (Output Events of type Home Assistant). E.g. http://homeassistant.local:8123 — prefer an IPv4 address if .local does not resolve on the device. Leave empty if you only need HA to control the device.",
            },
            new()
            {
                Key = HaTokenKey,
                Label = "Long-lived access token",
                Type = PluginSettingType.String,
                Secret = true,
                Description = "Create one in Home Assistant under your profile → Security → Long-lived access tokens",
            },
            new()
            {
                Key = StopHaSceneKey,
                Label = "HA scene when playback stops",
                Type = PluginSettingType.String,
                Description = "Optional scene, script, or automation (friendly name or entity id) fired after a short settle when the last cue ends or Now Playing is idle",
            },
            new()
            {
                Key = HaMqttBrokerKey,
                Label = "Home Assistant MQTT broker",
                Type = PluginSettingType.String,
                Description = "Only when Core MQTT (Settings → Remote Control) is a different broker from Home Assistant's Mosquitto. Do not enter the same server as Core MQTT — every command would run twice and the two clients would fight over availability. Leave empty to use only the Core MQTT server.",
            },
            new()
            {
                Key = HaMqttPortKey,
                Label = "Home Assistant MQTT port",
                Type = PluginSettingType.Integer,
                DefaultValue = "1883",
            },
            new()
            {
                Key = HaMqttUsernameKey,
                Label = "Home Assistant MQTT username",
                Type = PluginSettingType.String,
            },
            new()
            {
                Key = HaMqttPasswordKey,
                Label = "Home Assistant MQTT password",
                Type = PluginSettingType.String,
                Secret = true,
            },
            new()
            {
                Key = HaMqttTlsKey,
                Label = "Home Assistant MQTT TLS",
                Type = PluginSettingType.Boolean,
                DefaultValue = "false",
            },
        ],
    };

    private string Serial => this.host.DeviceInfo.Serial.ToLowerInvariant();

    private string DiscoveryPrefix => (this.host.Settings.GetString(DiscoveryPrefixKey) ?? "homeassistant").Trim().TrimEnd('/');

    public async Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
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
        this.subscriptions.Add(host.Playback.OnCueStarted(HandleCueStarted));
        this.subscriptions.Add(host.Playback.OnCueEnded(HandleCueEnded));

        // Last: the initial callback (current state first) kicks off the
        // first PublishAll when the broker is already connected
        this.subscriptions.Add(host.Mqtt.OnConnectionChanged(HandleConnectionChanged));

        await ConfigureRestProviderAsync(cancellationToken);
        await ConfigureHaMqttAsync(cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in this.subscriptions)
        {
            subscription.Dispose();
        }

        this.statusSubscription?.Dispose();
        CancelStopScene();
        TearDownRestProvider();
        await TearDownHaMqttAsync();
    }

    /// <summary>
    /// (Re)build the Core→HA side from the URL/token settings: register the
    /// action provider when both are set, drop it when either is cleared,
    /// rebuild when they change. Runs a health check so a bad URL or token
    /// shows on the Plugins page without waiting for the first Output Event.
    /// </summary>
    private Task ConfigureRestProviderAsync(CancellationToken cancellationToken)
    {
        string url = (this.host.Settings.GetString(HaUrlKey) ?? string.Empty).Trim();
        string token = (this.host.Settings.GetString(HaTokenKey) ?? string.Empty).Trim();
        string key = url.Length > 0 && token.Length > 0 ? $"{url}\n{token}" : string.Empty;

        if (string.Equals(key, this.restConfigKey, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        TearDownRestProvider();
        this.restConfigKey = key;

        if (key.Length == 0)
        {
            ReportStatus();

            return Task.CompletedTask;
        }

        try
        {
            this.restClient = new HaRestClient(url, token, this.RestHandler);
        }
        catch (Exception ex)
        {
            this.host.Logger.LogWarning("Home Assistant URL '{Url}' is not valid: {Message}", url, ex.Message);
            this.restHealthy = false;
            this.restHealthDetail = $"invalid URL '{url}'";
            ReportStatus();

            return Task.CompletedTask;
        }

        var client = this.restClient;
        var provider = new HaActionProvider(client, ReportRestHealth);
        this.actionProvider = provider;
        this.actionProviderHandle = this.host.Actions.RegisterProvider(provider);
        this.host.Logger.LogInformation("Home Assistant REST API configured at {Url}; scenes/scripts/automations available as Output Event targets", client.BaseAddress);

        // SchedulePeriodic invokes immediately on the real host (no initial
        // delay), so this is the first health check without blocking
        // InitializeAsync / settings-changed on the network.
        this.restHealthSchedule = this.host.SchedulePeriodic(TimeSpan.FromMinutes(5), CheckRestHealth);
        ReportStatus();
        return Task.CompletedTask;
    }

    private void TearDownRestProvider()
    {
        this.restHealthSchedule?.Dispose();
        this.restHealthSchedule = null;
        this.actionProviderHandle?.Dispose();
        this.actionProviderHandle = null;
        this.actionProvider = null;
        HaRestClient? client = this.restClient;
        this.restClient = null;
        client?.Dispose();
        this.restHealthy = null;
        this.restHealthDetail = null;
        this.restConfigKey = string.Empty;
        ClearStopSceneResolution();
    }

    private async Task CheckRestHealth(CancellationToken cancellationToken)
    {
        var client = this.restClient;
        if (client == null)
        {
            return;
        }

        try
        {
            await client.CheckAsync(cancellationToken);
            if (!ReferenceEquals(client, this.restClient))
            {
                return;
            }

            ReportRestHealth(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!ReferenceEquals(client, this.restClient))
            {
                return;
            }

            ReportRestHealth(false, ex.Message);
        }
    }

    private void ReportRestHealth(bool healthy, string? detail)
    {
        if (this.restHealthy == healthy && this.restHealthDetail == detail)
        {
            return;
        }

        if (!healthy)
        {
            this.host.Logger.LogWarning("Home Assistant REST API unavailable: {Detail}", detail);
        }
        else if (this.restHealthy == false)
        {
            this.host.Logger.LogInformation("Home Assistant REST API reachable again");
        }

        this.restHealthy = healthy;
        this.restHealthDetail = detail;
        ReportStatus();
    }

    /// <summary>
    /// One connection-state line for the Plugins page covering both
    /// directions: "N entities published" (MQTT) plus the REST API health
    /// when configured. Connected only when everything configured works.
    /// </summary>
    private void ReportStatus()
    {
        bool mqttOk = this.host.Mqtt.IsConnected || this.haMqtt is { IsConnected: true };
        string detail = mqttOk ? $"{this.publishedCount} entities published" : "MQTT broker not connected";
        if (this.haMqtt is { IsConnected: true })
        {
            detail += "; HA MQTT connected";
        }

        if (this.restConfigKey.Length > 0)
        {
            detail += this.restHealthy switch
            {
                true => "; HA API ok",
                false => $"; HA API: {this.restHealthDetail}",
                null => "; HA API: checking",
            };
        }

        this.host.SetConnectionState(mqttOk && this.restHealthy != false, detail);
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

    private async Task HandleSettingsChanged(CancellationToken cancellationToken)
    {
        ClearStopSceneResolution();

        if (!string.Equals(this.statusPrefix, DiscoveryPrefix, StringComparison.Ordinal))
        {
            SubscribeStatusTopic();
        }

        await ConfigureRestProviderAsync(cancellationToken);
        await ConfigureHaMqttAsync(cancellationToken);

        // PublishAll clears configs left under a previous prefix via the
        // persisted publish record
        await PublishAll(cancellationToken);
    }

    /// <summary>
    /// Publish (or re-publish) the full discovery set: remove configs for
    /// entities that disappeared (or a changed prefix), then publish configs
    /// and current states for every exposed entity.
    /// </summary>
    private async Task PublishAll(CancellationToken cancellationToken)
    {
        if (!this.host.Mqtt.IsConnected && this.haMqtt is not { IsConnected: true })
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
                await PublishRetainedAsync(
                    Discovery.ConfigTopic(record.Prefix, entry.Component, Serial, entry.ObjectId),
                    string.Empty, cancellationToken);
            }
        }

        // Publish configs + current state
        foreach (var (objectId, entity) in map)
        {
            string component = Discovery.Component(entity.Kind);

            await PublishRetainedAsync(
                Discovery.ConfigTopic(prefix, component, Serial, objectId),
                Discovery.ConfigPayload(this.host, Serial, objectId, entity),
                cancellationToken);

            var state = await this.host.Entities.GetStateAsync(entity.Code, cancellationToken);
            string? stateValue = Discovery.StateValue(entity.Kind, state);
            if (stateValue != null)
            {
                await PublishRetainedAsync(
                    Discovery.StateTopic(Serial, objectId), stateValue, cancellationToken);
            }
        }

        this.published = map;

        await PublishRecord.Save(this.host, prefix, map, cancellationToken);

        this.host.Logger.LogInformation("Published {Count} entities to discovery prefix '{Prefix}'", map.Count, prefix);
        this.publishedCount = map.Count;
        ReportStatus();
    }

    private async Task HandleEntityState(PluginEntityState state, CancellationToken cancellationToken)
    {
        if (string.Equals(state.Code, "system.nowplaying", StringComparison.OrdinalIgnoreCase))
        {
            bool idle = IsIdlePlayback(state.Text);
            if (idle && this.playingCueCodes.Count == 0)
            {
                ScheduleStopScene();
            }
            else if (!idle)
            {
                CancelStopScene();
            }
        }

        if (!this.host.Mqtt.IsConnected && this.haMqtt is not { IsConnected: true })
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
            await PublishRetainedAsync(Discovery.StateTopic(Serial, objectId), stateValue, cancellationToken);
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

    private Task HandleHaMqttMessage(MqttMessage message, CancellationToken cancellationToken)
    {
        if (message.Topic.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            return HandleHaStatus(message, cancellationToken);
        }

        return HandleCommand(message, cancellationToken);
    }

    private async Task PublishRetainedAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        Exception? first = null;
        if (this.host.Mqtt.IsConnected)
        {
            try
            {
                await this.host.Mqtt.PublishAsync(topic, payload, retain: true, MqttQos.AtLeastOnce, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                first = ex;
            }
        }

        if (this.haMqtt is { IsConnected: true })
        {
            try
            {
                await this.haMqtt.PublishAsync(topic, payload, retain: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                first ??= ex;
            }
        }

        if (first != null)
        {
            throw first;
        }
    }

    private async Task ConfigureHaMqttAsync(CancellationToken cancellationToken)
    {
        string? broker = this.host.Settings.GetString(HaMqttBrokerKey);
        broker = string.IsNullOrWhiteSpace(broker) ? null : broker.Trim();
        int port = this.host.Settings.GetInteger(HaMqttPortKey) ?? 1883;
        string? username = this.host.Settings.GetString(HaMqttUsernameKey);
        string? password = this.host.Settings.GetString(HaMqttPasswordKey);
        bool tls = this.host.Settings.GetBoolean(HaMqttTlsKey) == true;
        string prefix = DiscoveryPrefix;
        string key = this.mqttFactory != null
            ? $"factory\n{prefix}"
            : broker == null ? "" : $"{broker}\n{port}\n{username}\n{password}\n{tls}\n{prefix}";

        if (string.Equals(key, this.haMqttConfigKey, StringComparison.Ordinal))
        {
            return;
        }

        await TearDownHaMqttAsync();
        this.haMqttConfigKey = key;

        if (this.mqttFactory != null)
        {
            this.haMqtt = this.mqttFactory(this.host);
        }
        else if (broker != null)
        {
            this.host.Logger.LogWarning(
                "Connecting a dedicated Home Assistant MQTT broker at {Server}:{Port}. Use this only when it is a different server from Settings → Remote Control MQTT; pointing both at the same broker duplicates commands and fights over availability.",
                broker, port);
            this.haMqtt = new HomeAssistantMqttBroker(
                this.host, broker, port, username, password, tls, HandleHaMqttMessage, PublishAll);
        }

        if (this.haMqtt == null)
        {
            ReportStatus();
            return;
        }

        try
        {
            await this.haMqtt.StartAsync(cancellationToken);
            await PublishAll(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.host.Logger.LogWarning(ex, "Home Assistant MQTT broker connection failed");
        }

        ReportStatus();
    }

    private async Task TearDownHaMqttAsync()
    {
        if (this.haMqtt != null)
        {
            await this.haMqtt.DisposeAsync();
            this.haMqtt = null;
        }

        this.haMqttConfigKey = string.Empty;
    }

    private Task HandleCueStarted(CuePlaybackEvent evt, CancellationToken cancellationToken)
    {
        this.playingCueCodes.Add(evt.CueCode);
        CancelStopScene();
        return Task.CompletedTask;
    }

    private Task HandleCueEnded(CuePlaybackEvent evt, CancellationToken cancellationToken)
    {
        this.playingCueCodes.Remove(evt.CueCode);
        if (this.playingCueCodes.Count == 0)
        {
            ScheduleStopScene();
        }

        return Task.CompletedTask;
    }

    private void CancelStopScene()
    {
        CancellationTokenSource? delay = this.stopSceneDelay;
        this.stopSceneDelay = null;
        if (delay == null)
        {
            return;
        }

        delay.Cancel();
        delay.Dispose();
    }

    private void ScheduleStopScene()
    {
        CancelStopScene();
        if (string.IsNullOrWhiteSpace(this.host.Settings.GetString(StopHaSceneKey)))
        {
            return;
        }

        var delay = new CancellationTokenSource();
        this.stopSceneDelay = delay;
        _ = ActivateStopSceneAfterSettleAsync(delay.Token);
    }

    private async Task ActivateStopSceneAfterSettleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.DelayAsync(this.StopSceneSettle, cancellationToken);
            await ActivateConfiguredStopSceneAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            this.host.Logger.LogWarning(ex, "HA scene when playback stops failed");
        }
    }

    private async Task ActivateConfiguredStopSceneAsync(CancellationToken cancellationToken)
    {
        string? configured = this.host.Settings.GetString(StopHaSceneKey);
        var provider = this.actionProvider;
        if (provider == null || string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        configured = configured.Trim();
        if (IsSupportedEntityId(configured))
        {
            await provider.ExecuteAsync(configured, payload: null, cancellationToken);
            this.host.Logger.LogInformation("Activated Home Assistant {Id} on stop", configured);
            return;
        }

        string? resolvedId = this.stopSceneResolvedId;
        if (resolvedId != null
            && string.Equals(configured, this.stopSceneResolvedSetting, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await provider.ExecuteAsync(resolvedId, payload: null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Scene renamed or deleted in HA since we resolved the friendly name
                ClearStopSceneResolution();
                throw;
            }

            this.host.Logger.LogInformation("Activated Home Assistant {Id} on stop", resolvedId);
            return;
        }

        IReadOnlyList<PluginActionTarget> targets = await provider.GetTargetsAsync(cancellationToken);
        PluginActionTarget? target = targets.FirstOrDefault(item =>
            string.Equals(item.Id, configured, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Label, configured, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            this.host.Logger.LogInformation(
                "No Home Assistant target matched stop setting '{Setting}'", configured);
            return;
        }

        await provider.ExecuteAsync(target.Id, payload: null, cancellationToken);
        this.stopSceneResolvedSetting = configured;
        this.stopSceneResolvedId = target.Id;
        this.host.Logger.LogInformation("Activated Home Assistant {Id} ({Label}) on stop", target.Id, target.Label);
    }

    private void ClearStopSceneResolution()
    {
        this.stopSceneResolvedId = null;
        this.stopSceneResolvedSetting = null;
    }

    private static bool IsSupportedEntityId(string value)
    {
        int dot = value.IndexOf('.');
        if (dot <= 0 || dot == value.Length - 1)
        {
            return false;
        }

        string domain = value[..dot];
        return HaRestClient.TargetDomains.Any(item =>
            string.Equals(item.Domain, domain, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIdlePlayback(string? nowPlaying)
        => string.IsNullOrWhiteSpace(nowPlaying);

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
        if (settingKey != null && this.host.Settings.GetBoolean(settingKey) == false)
        {
            return false;
        }

        if (ExposedLooks.IsLook(entity)
            && !ExposedLooks.Matches(entity, ExposedLooks.Parse(this.host.Settings.GetString(ExposedLooksKey))))
        {
            return false;
        }

        return true;
    }
}
