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
    private readonly Func<MqttMessage, CancellationToken, Task> onMessage;
    private readonly Func<CancellationToken, Task>? onConnected;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connectGate = new(1, 1);

    public HomeAssistantMqttBroker(
        IPluginHost host,
        string server,
        int port,
        string? username,
        string? password,
        bool useTls,
        Func<MqttMessage, CancellationToken, Task> onMessage,
        Func<CancellationToken, Task>? onConnected = null)
    {
        this.host = host;
        this.server = server;
        this.port = port;
        this.username = username;
        this.password = password;
        this.useTls = useTls;
        this.onMessage = onMessage;
        this.onConnected = onConnected;
        this.client = new MqttFactory().CreateMqttClient();
        this.client.ApplicationMessageReceivedAsync += HandleMessageAsync;
        this.client.DisconnectedAsync += HandleDisconnectedAsync;
    }

    public bool IsConnected => this.client.IsConnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        await this.connectGate.WaitAsync(linked.Token);
        try
        {
            await ConnectAsync(linked.Token);
            await NotifyConnectedAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.host.Logger.LogWarning(ex, "Home Assistant MQTT broker connection failed");
            _ = RetryUntilCanceledAsync();
        }
        finally
        {
            try
            {
                this.connectGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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
        this.connectGate.Dispose();
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
            options = options.WithTlsOptions(tls => tls.UseTls());
        }

        await this.client.ConnectAsync(options.Build(), cancellationToken);

        string prefix = (this.host.Settings.GetString("discovery-prefix") ?? "homeassistant").Trim().TrimEnd('/');
        await this.client.SubscribeAsync($"dmxcore/{serial}/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
        await this.client.SubscribeAsync($"{prefix}/status", MqttQualityOfServiceLevel.AtLeastOnce);

        await PublishAsync(this.host.Mqtt.DeviceAvailabilityTopic, "online", retain: true, cancellationToken);
        await PublishAsync(this.host.Mqtt.PluginAvailabilityTopic, "online", retain: true, cancellationToken);

        this.host.Logger.LogInformation("Connected to Home Assistant MQTT broker {Server}:{Port}", this.server, this.port);
    }

    private async Task NotifyConnectedAsync(CancellationToken cancellationToken)
    {
        if (this.onConnected != null)
        {
            await this.onConnected(cancellationToken);
        }
    }

    private async Task RetryUntilCanceledAsync()
    {
        try
        {
            await ConnectWithRetryAsync(this.lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        await this.connectGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!this.client.IsConnected)
                    {
                        await ConnectAsync(cancellationToken);
                    }

                    await NotifyConnectedAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    this.host.Logger.LogWarning(ex, "Home Assistant MQTT broker connection failed");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        finally
        {
            try
            {
                this.connectGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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
            await ConnectWithRetryAsync(this.lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
