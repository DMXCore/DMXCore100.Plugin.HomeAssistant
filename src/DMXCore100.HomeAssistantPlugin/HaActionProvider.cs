using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// The plugin's output action provider: exposes HA scenes/scripts/
/// automations as Output Event targets on the device and fires them through
/// the REST API. The target list is cached briefly so opening the picker
/// doesn't hammer HA while still picking up newly created scenes.
/// </summary>
public sealed class HaActionProvider : IPluginActionProvider
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly HaRestClient client;
    private readonly Func<DateTime> now;
    private readonly Action<bool, string?> reportHealth;
    private IReadOnlyList<PluginActionTarget>? cachedTargets;
    private DateTime cachedAt;

    public HaActionProvider(HaRestClient client, Action<bool, string?> reportHealth, Func<DateTime>? now = null)
    {
        this.client = client;
        this.reportHealth = reportHealth;
        this.now = now ?? (() => DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<PluginActionTarget>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        if (this.cachedTargets != null && this.now() - this.cachedAt < CacheDuration)
        {
            return this.cachedTargets;
        }

        try
        {
            var targets = await this.client.GetTargetsAsync(cancellationToken);
            this.cachedTargets = targets;
            this.cachedAt = this.now();
            this.reportHealth(true, null);

            return targets;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.reportHealth(false, ex.Message);
            throw;
        }
    }

    public async Task ExecuteAsync(string targetId, string? payload, CancellationToken cancellationToken)
    {
        try
        {
            await this.client.ExecuteAsync(targetId, payload, cancellationToken);
            this.reportHealth(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.reportHealth(false, ex.Message);
            throw;
        }
    }
}
