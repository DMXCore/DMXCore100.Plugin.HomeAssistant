using System.Text.Json;
using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// What was last published to the broker (discovery prefix + config topics),
/// persisted in the plugin state blob. Survives restarts so entities deleted
/// while the device was rebooting — or a changed discovery prefix — still get
/// their retained configs cleared instead of living on as ghosts in HA.
/// </summary>
public sealed class PublishRecord
{
    public string Prefix { get; set; } = "";

    public List<Entry> Entries { get; set; } = [];

    public sealed class Entry
    {
        public string ObjectId { get; set; } = "";

        public string Component { get; set; } = "";
    }

    public static async Task<PublishRecord> Load(IPluginHost host, CancellationToken cancellationToken)
    {
        try
        {
            string? json = await host.GetStateJsonAsync(cancellationToken);
            if (!string.IsNullOrEmpty(json))
            {
                return JsonSerializer.Deserialize<PublishRecord>(json) ?? new PublishRecord();
            }
        }
        catch (JsonException)
        {
            // Unreadable state: treat as nothing published
        }

        return new PublishRecord();
    }

    public static Task Save(IPluginHost host, string prefix, IReadOnlyDictionary<string, PluginEntity> published, CancellationToken cancellationToken)
    {
        var record = new PublishRecord
        {
            Prefix = prefix,
            Entries = published
                .Select(kvp => new Entry { ObjectId = kvp.Key, Component = Discovery.Component(kvp.Value.Kind) })
                .OrderBy(x => x.ObjectId, StringComparer.Ordinal)
                .ToList(),
        };

        return host.SetStateJsonAsync(JsonSerializer.Serialize(record), cancellationToken);
    }
}
