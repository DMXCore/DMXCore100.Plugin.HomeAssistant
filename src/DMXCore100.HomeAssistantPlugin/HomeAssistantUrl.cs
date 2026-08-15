using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Normalizes Home Assistant base URLs and resolves one from mDNS
/// (<c>_home-assistant._tcp</c>) when the admin left the URL setting empty.
/// </summary>
public static class HomeAssistantUrl
{
    public const string MdnsServiceType = "_home-assistant._tcp";

    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed;
    }

    public static string? FromMdns(IReadOnlyList<MdnsServiceInfo> services)
    {
        foreach (MdnsServiceInfo service in services)
        {
            IReadOnlyDictionary<string, string>? properties = service.Properties;
            if (properties != null)
            {
                if (TryProperty(properties, "internal_url", out string? internalUrl)
                    || TryProperty(properties, "base_url", out internalUrl)
                    || TryProperty(properties, "external_url", out internalUrl))
                {
                    return Normalize(internalUrl);
                }
            }

            if (!string.IsNullOrWhiteSpace(service.Address))
            {
                int port = service.Port > 0 ? service.Port : 8123;
                return Normalize($"http://{service.Address}:{port}");
            }
        }

        return null;
    }

    private static bool TryProperty(
        IReadOnlyDictionary<string, string> properties,
        string key,
        out string? value)
    {
        if (properties.TryGetValue(key, out string? found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }
}
