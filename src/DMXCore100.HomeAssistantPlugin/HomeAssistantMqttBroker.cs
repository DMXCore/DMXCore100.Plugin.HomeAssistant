using System.Text;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Dedicated MQTT 3.1.1 client for the Home Assistant broker. Last-will on
/// the device availability topic; command and birth subscriptions are
/// registered after connect.
/// </summary>
internal sealed class HomeAssistantMqttBroker : IHomeAssistantMqttBroker
{
    private readonly IMqttClient client;
    private readonly IPluginHost host;
    private readonly string server;
    private readonly int port;
    private readonly string? username;
    private readonly string? password;
    private readonly bool useTls;
    private readonly bool ignoreCertificates;
    private readonly Func<MqttMessage, CancellationToken, Task> onMessage;
    private readonly CancellationTokenSource lifetime = new();

    public HomeAssistantMqttBroker(
        IPluginHost host,
        string server,
        int port,
        string? username,
        string? password,
        bool useTls,
        bool ignoreCertificates,
        Func<MqttMessage, CancellationToken, Task> onMessage)
    {
        this.host = host;
        this.server = server;
        this.port = port;
        this.username = username;
        this.password = password;
        this.useTls = useTls;
        this.ignoreCertificates = ignoreCertificates;
        this.onMessage = onMessage;
        this.client = new MqttFactory().CreateMqttClient();
        this.client.ApplicationMessageReceivedAsync += HandleMessageAsync;
        this.client.DisconnectedAsync += HandleDisconnectedAsync;
    }

    public bool IsConnected => this.client.IsConnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (!this.client.IsConnected)
        {
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await this.client.PublishAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await this.lifetime.CancelAsync();
        try
        {
            if (this.client.IsConnected)
            {
                await PublishAsync(this.host.Mqtt.DeviceAvailabilityTopic, "offline", retain: true, CancellationToken.None);
                await PublishAsync(this.host.Mqtt.PluginAvailabilityTopic, "offline", retain: true, CancellationToken.None);
                await this.client.DisconnectAsync();
            }
        }
        catch (Exception)
        {
        }

        this.client.Dispose();
        this.lifetime.Dispose();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string serial = this.host.DeviceInfo.Serial.ToLowerInvariant();
        MqttClientOptionsBuilder options = new MqttClientOptionsBuilder()
            .WithTcpServer(this.server, this.port)
            .WithClientId($"dmxcore-{serial}-home-assistant")
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithWillTopic(this.host.Mqtt.DeviceAvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(this.username))
        {
            options = options.WithCredentials(this.username, this.password ?? "");
        }

        if (this.useTls)
        {
            options = options.WithTlsOptions(tls =>
            {
                tls.UseTls();
                if (this.ignoreCertificates)
                {
                    tls.WithCertificateValidationHandler(_ => true);
                }
            });
        }

        await this.client.ConnectAsync(options.Build(), cancellationToken);

        string prefix = (this.host.Settings.GetString("discovery-prefix") ?? "homeassistant").Trim().TrimEnd('/');
        await this.client.SubscribeAsync($"dmxcore/{serial}/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
        await this.client.SubscribeAsync($"dmxcore/{serial}/ha-scene/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
        await this.client.SubscribeAsync($"{prefix}/status", MqttQualityOfServiceLevel.AtLeastOnce);

        await PublishAsync(this.host.Mqtt.DeviceAvailabilityTopic, "online", retain: true, cancellationToken);
        await PublishAsync(this.host.Mqtt.PluginAvailabilityTopic, "online", retain: true, cancellationToken);

        this.host.Logger.LogInformation("Connected to Home Assistant MQTT broker {Server}:{Port}", this.server, this.port);
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        string payload = args.ApplicationMessage.PayloadSegment.Count == 0
            ? ""
            : Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
        var message = new MqttMessage(args.ApplicationMessage.Topic, payload, args.ApplicationMessage.Retain);
        try
        {
            await this.onMessage(message, this.lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.host.Logger.LogWarning(ex, "Home Assistant MQTT handler failed for {Topic}", message.Topic);
        }
    }

    private async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (this.lifetime.IsCancellationRequested)
        {
            return;
        }

        this.host.Logger.LogWarning("Home Assistant MQTT broker disconnected; retrying");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), this.lifetime.Token);
            await ConnectAsync(this.lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.host.Logger.LogWarning(ex, "Home Assistant MQTT reconnect failed");
        }
    }
}
