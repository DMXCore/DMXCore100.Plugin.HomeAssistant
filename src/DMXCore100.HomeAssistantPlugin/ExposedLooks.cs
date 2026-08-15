using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Allow-list of Core presets, cues, and timelines to publish over MQTT.
/// Empty means every look is eligible (subject to the category toggle).
/// </summary>
internal static class ExposedLooks
{
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .ToList();
    }

    public static bool IsLook(PluginEntity entity)
    {
        string ns = Namespace(entity.Code);
        return ns is "preset" or "cue" or "timeline";
    }

    public static bool Matches(PluginEntity entity, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return true;
        }

        foreach (string token in tokens)
        {
            if (MatchesToken(entity, token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesToken(PluginEntity entity, string token)
    {
        if (string.Equals(entity.Code, token, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.Name, token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string tokenNs = Namespace(token);
        if (tokenNs is "preset" or "cue" or "timeline")
        {
            return string.Equals(entity.Code, token, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(LocalId(entity.Code), token, StringComparison.OrdinalIgnoreCase)
            || string.Equals(HomeAssistantScene.Slug(entity.Name), HomeAssistantScene.Slug(token), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Discovery.ObjectId(entity.Code), Discovery.ObjectId(token), StringComparison.OrdinalIgnoreCase);
    }

    private static string Namespace(string code)
    {
        int dot = code.IndexOf('.');
        return dot > 0 ? code[..dot].ToLowerInvariant() : "";
    }

    private static string LocalId(string code)
    {
        int dot = code.IndexOf('.');
        return dot >= 0 && dot < code.Length - 1 ? code[(dot + 1)..] : code;
    }
}
