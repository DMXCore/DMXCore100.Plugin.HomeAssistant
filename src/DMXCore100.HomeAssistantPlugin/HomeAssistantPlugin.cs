using System.Globalization;
using System.Text.Json;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Bidirectional Home Assistant integration:
/// <list type="bullet">
/// <item>MQTT Discovery publishes the Core's entities into HA.</item>
/// <item>The HA REST client discovers <c>scene.*</c> entities and activates
/// them from the Core (cue name match, or MQTT
/// <c>dmxcore/{serial}/ha-scene/{objectId}/set</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// Topic layout (serial is the device hardware id):
/// <code>
/// {prefix}/{component}/dmxcore-{serial}/{objectId}/config   retained discovery
/// dmxcore/{serial}/{objectId}/state                         retained state
/// dmxcore/{serial}/{objectId}/set                           commands from HA
/// dmxcore/{serial}/ha-scene/{objectId}/set                  activate an HA scene
/// </code>
/// All handlers run on the plugin's serial dispatch queue, so no locking is
/// needed around the published-entity map or the HA scene list.
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
    private const string HaIgnoreCertificatesKey = "ha-ignore-certificates";
    private const string ActivateScenesFromCuesKey = "activate-scenes-from-cues";
    private const string StopHaSceneKey = "stop-ha-scene";
    private const string HaMqttBrokerKey = "ha-mqtt-broker";
    private const string HaMqttPortKey = "ha-mqtt-port";
    private const string HaMqttUsernameKey = "ha-mqtt-username";
    private const string HaMqttPasswordKey = "ha-mqtt-password";
    private const string HaMqttTlsKey = "ha-mqtt-tls";

    private static readonly TimeSpan HaPollInterval = TimeSpan.FromSeconds(30);

    private readonly HomeAssistantApiFactory? apiFactory;
    private readonly HomeAssistantMqttFactory? mqttFactory;
    private readonly SemaphoreSlim persistWrites = new(1, 1);
    private readonly SemaphoreSlim sceneActivations = new(1, 1);
    private readonly List<IDisposable> subscriptions = [];

    private IPluginHost host = null!;
    private IDisposable? statusSubscription;
    private IDisposable? haPoll;
    private IHomeAssistantApi? haApi;
    private IHomeAssistantMqttBroker? haMqtt;
    private string statusPrefix = "";
    private string? haError;

    // Entities currently published to HA, keyed by objectId (rebuilt by every
    // PublishAll; commands and state changes resolve through this)
    private Dictionary<string, PluginEntity> published = new(StringComparer.OrdinalIgnoreCase);

    private List<HomeAssistantScene> haScenes = [];
    private string? lastActivatedSceneId;
    private DateTime lastActivatedUtc = DateTime.MinValue;
    private CancellationTokenSource? stopSceneDelay;

    public HomeAssistantPlugin()
        : this(null, null)
    {
    }

    internal HomeAssistantPlugin(HomeAssistantApiFactory? apiFactory, HomeAssistantMqttFactory? mqttFactory = null)
    {
        this.apiFactory = apiFactory;
        this.mqttFactory = mqttFactory;
    }

    internal TimeSpan StopSceneSettle { get; set; } = TimeSpan.FromMilliseconds(400);

    public PluginInfo Info { get; } = new()
    {
        // Id/Name/Version come from the csproj (PluginId, PluginDisplayName,
        // Version) via the SDK-generated PluginBuildInfo, always in sync with
        // the generated manifest.json
        Id = PluginBuildInfo.Id,
        Name = PluginBuildInfo.Name,
        Version = PluginBuildInfo.Version,
        Description = "Publishes the device's entities to Home Assistant via MQTT Discovery, and discovers HA scenes so the Core can activate them.",
        Settings =
        [
            new()
            {
                Key = HaUrlKey,
                Label = "Home Assistant URL",
                Type = PluginSettingType.String,
                Description = "e.g. http://homeassistant.local:8123 — leave empty to pick up a server advertised on the LAN via mDNS",
            },
            new()
            {
                Key = HaTokenKey,
                Label = "Long-lived access token",
                Type = PluginSettingType.String,
                Secret = true,
                Description = "Created in Home Assistant under the user profile. Required to list and activate scenes.",
            },
            new()
            {
                Key = HaIgnoreCertificatesKey,
                Label = "Ignore TLS certificate errors",
                Type = PluginSettingType.Boolean,
                DefaultValue = "false",
            },
            new()
            {
                Key = HaMqttBrokerKey,
                Label = "Home Assistant MQTT broker",
                Type = PluginSettingType.String,
                Description = "Optional. Hostname of Home Assistant's Mosquitto (e.g. homeassistant.local). Used in addition to the Core MQTT connection when that broker is not the HA one. Leave empty to use only the Core MQTT server.",
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
            new()
            {
                Key = ActivateScenesFromCuesKey,
                Label = "Activate matching HA scenes when a cue starts",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
                Description = "When a cue or preset starts, activate the Home Assistant scene with the same name. Uses the display name (for example Movie Night), not the Core shortcode.",
            },
            new()
            {
                Key = StopHaSceneKey,
                Label = "HA scene when playback stops",
                Type = PluginSettingType.String,
                Description = "Friendly name or entity id (for example All Off or scene.all_off). Leave empty to do nothing.",
            },
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
                Description = "Comma or newline separated names or codes (for example Movie Night, preset.PARTY, cue.SUNSET). Leave empty to expose all of them when the toggle above is on.",
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

    public IReadOnlyList<HomeAssistantScene> DiscoveredScenes => this.haScenes;

    private string Serial => this.host.DeviceInfo.Serial.ToLowerInvariant();

    private string DiscoveryPrefix => (this.host.Settings.GetString(DiscoveryPrefixKey) ?? "homeassistant").Trim().TrimEnd('/');

    private string? AccessToken
    {
        get
        {
            string? token = this.host.Settings.GetString(HaTokenKey);
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
    }

    public async Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.host = host;

        // Commands from HA arrive on the per-entity set topics
        this.subscriptions.Add(host.Mqtt.Subscribe($"dmxcore/{Serial}/+/set", HandleCommand));
        this.subscriptions.Add(host.Mqtt.Subscribe($"dmxcore/{Serial}/ha-scene/+/set", HandleHaSceneCommand));

        // HA publishes a birth message on restart; retained configs usually
        // survive, but republishing is the belt-and-suspenders standard
        SubscribeStatusTopic();

        this.subscriptions.Add(host.Entities.OnStateChanged(HandleEntityState));
        this.subscriptions.Add(host.Entities.OnCatalogChanged(HandleCatalogChanged));
        this.subscriptions.Add(host.Settings.OnChanged(HandleSettingsChanged));
        this.subscriptions.Add(host.Playback.OnCueStarted(HandleCueStarted));
        this.subscriptions.Add(host.Playback.OnCueEnded(HandleCueEnded));

        PublishRecord record = await PublishRecord.Load(host, cancellationToken);
        if (record.Scenes.Count > 0)
        {
            this.haScenes = record.Scenes
                .Where(scene => !string.IsNullOrWhiteSpace(scene.EntityId))
                .ToList();
        }

        // Last: the initial callback (current state first) kicks off the
        // first PublishAll when the broker is already connected
        this.subscriptions.Add(host.Mqtt.OnConnectionChanged(HandleConnectionChanged));

        await ConnectHomeAssistantAsync(cancellationToken);
        await ConnectHaMqttAsync(cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in this.subscriptions)
        {
            subscription.Dispose();
        }

        this.statusSubscription?.Dispose();
        this.haPoll?.Dispose();
        this.haApi?.Dispose();
        CancelStopScene();
        if (this.haMqtt != null)
        {
            await this.haMqtt.DisposeAsync();
            this.haMqtt = null;
        }

        return;
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
        else
        {
            ReportStatus();
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
        if (!string.Equals(this.statusPrefix, DiscoveryPrefix, StringComparison.Ordinal))
        {
            SubscribeStatusTopic();
        }

        // PublishAll clears configs left under a previous prefix via the
        // persisted publish record
        await PublishAll(cancellationToken);
        await ConnectHomeAssistantAsync(cancellationToken);
        await ConnectHaMqttAsync(cancellationToken);
    }

    /// <summary>
    /// Publish (or re-publish) the full discovery set: remove configs for
    /// entities that disappeared (or a changed prefix), then publish configs
    /// and current states for every exposed entity.
    /// </summary>
    private async Task PublishAll(CancellationToken cancellationToken)
    {
        if (!AnyMqttConnected)
        {
            ReportStatus();
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

        await PersistAsync(writePublished: true, cancellationToken);

        this.host.Logger.LogInformation("Published {Count} entities to discovery prefix '{Prefix}'", map.Count, prefix);
        ReportStatus();
    }

    private async Task HandleEntityState(PluginEntityState state, CancellationToken cancellationToken)
    {
        if (string.Equals(state.Code, "system.nowplaying", StringComparison.OrdinalIgnoreCase))
        {
            if (HomeAssistantScene.IsIdlePlayback(state.Text))
            {
                ScheduleStopScene();
            }
            else
            {
                CancelStopScene();
                await TryActivateFromPlaybackAsync(state.Text, HomeAssistantScene.PlaybackLabel(state.Text), cancellationToken);
            }
        }

        if (!AnyMqttConnected)
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

        if (string.Equals(entity.Code, "system.stop", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleStopScene();
        }
    }

    private async Task HandleHaSceneCommand(MqttMessage message, CancellationToken cancellationToken)
    {
        // dmxcore/{serial}/ha-scene/{objectId}/set
        string[] parts = message.Topic.Split('/');
        if (parts.Length != 5)
        {
            return;
        }

        string objectId = parts[3];
        HomeAssistantScene? scene = this.haScenes.FirstOrDefault(item =>
            string.Equals(Discovery.ObjectId(item.EntityId), objectId, StringComparison.OrdinalIgnoreCase));
        if (scene == null)
        {
            this.host.Logger.LogDebug("HA scene command for unknown object id {ObjectId} ignored", objectId);
            return;
        }

        CancelStopScene();
        await ActivateSceneAsync(scene, cancellationToken);
    }

    private Task HandleCueStarted(CuePlaybackEvent evt, CancellationToken cancellationToken)
    {
        CancelStopScene();
        return TryActivateFromPlaybackAsync(evt.CueCode, name: null, cancellationToken);
    }

    private Task HandleCueEnded(CuePlaybackEvent evt, CancellationToken cancellationToken)
    {
        ScheduleStopScene();
        return Task.CompletedTask;
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

    private async Task ActivateStopSceneAfterSettleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(this.StopSceneSettle, cancellationToken);
            await ActivateConfiguredStopSceneAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ActivateConfiguredStopSceneAsync(CancellationToken cancellationToken)
    {
        string? configured = this.host.Settings.GetString(StopHaSceneKey);
        HomeAssistantScene? scene = HomeAssistantScene.FindConfigured(this.haScenes, configured);
        if (scene == null)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                this.host.Logger.LogInformation(
                    "No Home Assistant scene matched stop setting '{Setting}'. Discovered: {Scenes}",
                    configured,
                    string.Join(", ", this.haScenes.Select(item => $"{item.Name} ({item.EntityId})")));
            }

            return;
        }

        await ActivateSceneAsync(scene, cancellationToken);
    }

    private async Task TryActivateFromPlaybackAsync(string? code, string? name, CancellationToken cancellationToken)
    {
        if (this.host.Settings.GetBoolean(ActivateScenesFromCuesKey) == false)
        {
            return;
        }

        var catalog = await this.host.Entities.GetCatalogAsync(cancellationToken);
        PluginEntity? playback = HomeAssistantScene.FindCatalogEntity(catalog, code)
            ?? HomeAssistantScene.FindCatalogEntity(catalog, name);
        string? resolvedName = name ?? playback?.Name;
        string? resolvedCode = code ?? playback?.Code;

        HomeAssistantScene? scene = HomeAssistantScene.Find(this.haScenes, resolvedCode, resolvedName);
        if (scene == null && playback != null)
        {
            scene = HomeAssistantScene.Find(this.haScenes, playback.Code, playback.Name);
        }

        if (scene == null)
        {
            if (this.haApi != null && this.haScenes.Count > 0)
            {
                this.host.Logger.LogInformation(
                    "No Home Assistant scene matched playback '{Code}' / '{Name}'. Discovered: {Scenes}",
                    resolvedCode,
                    resolvedName,
                    string.Join(", ", this.haScenes.Select(item => $"{item.Name} ({item.EntityId})")));
            }

            return;
        }

        await ActivateSceneAsync(scene, cancellationToken);
    }

    private async Task ConnectHomeAssistantAsync(CancellationToken cancellationToken)
    {
        this.haApi?.Dispose();
        this.haApi = null;
        this.haPoll?.Dispose();
        this.haPoll = null;
        this.haError = null;

        string? token = AccessToken;
        if (token == null)
        {
            ReportStatus();
            return;
        }

        string? url = HomeAssistantUrl.Normalize(this.host.Settings.GetString(HaUrlKey));
        if (url == null)
        {
            IReadOnlyList<MdnsServiceInfo> advertised =
                await this.host.Mdns.GetServicesAsync(HomeAssistantUrl.MdnsServiceType, cancellationToken);
            url = HomeAssistantUrl.FromMdns(advertised);
        }

        if (url == null)
        {
            this.haError = "Home Assistant URL not set and none found via mDNS";
            this.host.Logger.LogWarning("{Message}", this.haError);
            ReportStatus();
            return;
        }

        bool ignoreCertificates = this.host.Settings.GetBoolean(HaIgnoreCertificatesKey) == true;
        HomeAssistantApiFactory factory = this.apiFactory ?? CreateRestApi;
        try
        {
            this.haApi = factory(url, token, ignoreCertificates);
        }
        catch (Exception ex)
        {
            this.haError = ex.Message;
            this.host.Logger.LogWarning(ex, "Failed to create Home Assistant client");
            ReportStatus();
            return;
        }

        this.haPoll = this.host.SchedulePeriodic(HaPollInterval, RefreshHomeAssistantScenesAsync);
        await RefreshHomeAssistantScenesAsync(cancellationToken);
    }

    private async Task ConnectHaMqttAsync(CancellationToken cancellationToken)
    {
        if (this.haMqtt != null)
        {
            await this.haMqtt.DisposeAsync();
            this.haMqtt = null;
        }

        string? broker = this.host.Settings.GetString(HaMqttBrokerKey);
        broker = string.IsNullOrWhiteSpace(broker) ? null : broker.Trim();

        if (this.mqttFactory != null)
        {
            this.haMqtt = this.mqttFactory(this.host);
        }
        else if (broker != null)
        {
            int port = this.host.Settings.GetInteger(HaMqttPortKey) ?? 1883;
            this.haMqtt = new HomeAssistantMqttBroker(
                this.host,
                broker,
                port,
                this.host.Settings.GetString(HaMqttUsernameKey),
                this.host.Settings.GetString(HaMqttPasswordKey),
                this.host.Settings.GetBoolean(HaMqttTlsKey) == true,
                this.host.Settings.GetBoolean(HaIgnoreCertificatesKey) == true,
                HandleHaMqttMessage);
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
        catch (Exception ex)
        {
            this.host.Logger.LogWarning(ex, "Home Assistant MQTT broker connection failed");
            await this.haMqtt.DisposeAsync();
            this.haMqtt = null;
        }

        ReportStatus();
    }

    private Task HandleHaMqttMessage(MqttMessage message, CancellationToken cancellationToken)
    {
        if (message.Topic.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            return HandleHaStatus(message, cancellationToken);
        }

        if (message.Topic.Contains("/ha-scene/", StringComparison.OrdinalIgnoreCase))
        {
            return HandleHaSceneCommand(message, cancellationToken);
        }

        return HandleCommand(message, cancellationToken);
    }

    private bool AnyMqttConnected => this.host.Mqtt.IsConnected || this.haMqtt is { IsConnected: true };

    private async Task PublishRetainedAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (this.host.Mqtt.IsConnected)
        {
            await this.host.Mqtt.PublishAsync(topic, payload, retain: true, MqttQos.AtLeastOnce, cancellationToken);
        }

        if (this.haMqtt is { IsConnected: true })
        {
            await this.haMqtt.PublishAsync(topic, payload, retain: true, cancellationToken);
        }
    }

    private async Task RefreshHomeAssistantScenesAsync(CancellationToken cancellationToken)
    {
        if (this.haApi == null)
        {
            return;
        }

        try
        {
            IReadOnlyList<HomeAssistantScene> scenes = await this.haApi.GetScenesAsync(cancellationToken);
            this.haScenes = scenes.ToList();
            this.haError = null;
            await PersistAsync(writePublished: false, cancellationToken);
            this.host.Logger.LogInformation("Discovered {Count} Home Assistant scenes", this.haScenes.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            this.haError = ex.Message;
            this.host.Logger.LogWarning(ex, "Home Assistant scene refresh failed");
        }

        ReportStatus();
    }

    private async Task ActivateSceneAsync(HomeAssistantScene scene, CancellationToken cancellationToken)
    {
        await this.sceneActivations.WaitAsync(cancellationToken);
        try
        {
            if (this.haApi == null)
            {
                this.host.Logger.LogWarning("Cannot activate {EntityId}; Home Assistant is not connected", scene.EntityId);
                return;
            }

            if (IsDuplicateActivation(scene.EntityId))
            {
                return;
            }

            try
            {
                await this.haApi.ActivateSceneAsync(scene.EntityId, cancellationToken);
                this.lastActivatedSceneId = scene.EntityId;
                this.lastActivatedUtc = DateTime.UtcNow;
                this.host.Logger.LogInformation("Activated Home Assistant scene {EntityId} ({Name})", scene.EntityId, scene.Name);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                this.host.Logger.LogWarning(ex, "Failed to activate Home Assistant scene {EntityId}", scene.EntityId);
            }
        }
        finally
        {
            this.sceneActivations.Release();
        }
    }

    private bool IsDuplicateActivation(string entityId)
    {
        return string.Equals(entityId, this.lastActivatedSceneId, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - this.lastActivatedUtc < TimeSpan.FromSeconds(2);
    }

    private async Task PersistAsync(bool writePublished, CancellationToken cancellationToken)
    {
        await this.persistWrites.WaitAsync(cancellationToken);
        try
        {
            PublishRecord record = await PublishRecord.Load(this.host, cancellationToken);
            if (writePublished)
            {
                record.Prefix = DiscoveryPrefix;
                record.Entries = this.published
                    .Select(kvp => new PublishRecord.Entry
                    {
                        ObjectId = kvp.Key,
                        Component = Discovery.Component(kvp.Value.Kind),
                    })
                    .OrderBy(entry => entry.ObjectId, StringComparer.Ordinal)
                    .ToList();
            }

            record.Scenes = this.haScenes
                .Select(scene => new HomeAssistantScene { EntityId = scene.EntityId, Name = scene.Name })
                .OrderBy(scene => scene.EntityId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await this.host.SetStateJsonAsync(JsonSerializer.Serialize(record), cancellationToken);
        }
        finally
        {
            this.persistWrites.Release();
        }
    }

    private void ReportStatus()
    {
        var parts = new List<string>();
        if (this.host.Mqtt.IsConnected || this.haMqtt is { IsConnected: true })
        {
            parts.Add($"{this.published.Count} entities published");
        }

        if (this.haMqtt is { IsConnected: true })
        {
            parts.Add("HA MQTT connected");
        }

        if (AccessToken != null)
        {
            if (this.haError != null)
            {
                parts.Add($"HA: {this.haError}");
            }
            else
            {
                parts.Add($"{this.haScenes.Count} HA scenes");
            }
        }

        bool connected = AnyMqttConnected || (AccessToken != null && this.haError == null && this.haApi != null);
        string detail = parts.Count > 0 ? string.Join("; ", parts) : "Idle";
        this.host.SetConnectionState(connected, detail);
    }

    private static IHomeAssistantApi CreateRestApi(string baseUrl, string accessToken, bool ignoreCertificates)
        => new HomeAssistantRestClient(baseUrl, accessToken, ignoreCertificates);

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

        if (ExposedLooks.IsLook(entity))
        {
            IReadOnlyList<string> allow = ExposedLooks.Parse(this.host.Settings.GetString(ExposedLooksKey));
            if (!ExposedLooks.Matches(entity, allow))
            {
                return false;
            }
        }

        return true;
    }
}
