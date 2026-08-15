using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// A Home Assistant <c>scene.*</c> entity the Core can activate.
/// </summary>
public sealed class HomeAssistantScene
{
    public string EntityId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// True when this HA scene is the same thing as a Core playback identified
    /// by shortcode and/or display name ("Movie Night" matches
    /// <c>scene.movie_night</c>).
    /// </summary>
    public bool MatchesPlayback(string? code, string? name)
    {
        if (NamesEqual(this.Name, name)
            || NamesEqual(this.Name, code)
            || SlugEquals(this.Name, name)
            || SlugEquals(this.Name, code)
            || SlugEquals(EntitySlug(this.EntityId), name)
            || SlugEquals(EntitySlug(this.EntityId), code)
            || SlugEquals(EntitySlug(this.EntityId), LocalId(code)))
        {
            return true;
        }

        return false;
    }

    public static HomeAssistantScene? Find(
        IReadOnlyList<HomeAssistantScene> scenes,
        string? code,
        string? name)
    {
        foreach (HomeAssistantScene scene in scenes)
        {
            if (scene.MatchesPlayback(code, name))
            {
                return scene;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve a scene named in plugin settings (friendly name or
    /// <c>scene.*</c> entity id).
    /// </summary>
    public static HomeAssistantScene? FindConfigured(
        IReadOnlyList<HomeAssistantScene> scenes,
        string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string value = configured.Trim();
        foreach (HomeAssistantScene scene in scenes)
        {
            if (string.Equals(scene.EntityId, value, StringComparison.OrdinalIgnoreCase)
                || scene.MatchesPlayback(value, value))
            {
                return scene;
            }
        }

        if (value.StartsWith("scene.", StringComparison.OrdinalIgnoreCase))
        {
            return new HomeAssistantScene { EntityId = value, Name = value };
        }

        return null;
    }

    /// <summary>
    /// Resolve a Core cue/preset/timeline from a playback code that may be a
    /// namespaced catalog code (<c>cue.F1GO</c>), a shortcode (<c>F1GO</c>),
    /// or a display name.
    /// </summary>
    public static PluginEntity? FindCatalogEntity(IEnumerable<PluginEntity> catalog, string? playback)
    {
        if (string.IsNullOrWhiteSpace(playback))
        {
            return null;
        }

        string value = playback.Trim();
        string local = LocalId(value);
        foreach (PluginEntity entity in catalog)
        {
            if (!IsPlaybackEntity(entity))
            {
                continue;
            }

            if (string.Equals(entity.Code, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(LocalId(entity.Code), local, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity.Name, value, StringComparison.OrdinalIgnoreCase))
            {
                return entity;
            }
        }

        return null;
    }

    public static string? PlaybackLabel(string? nowPlaying)
    {
        if (string.IsNullOrWhiteSpace(nowPlaying))
        {
            return null;
        }

        string text = nowPlaying.Trim();
        foreach (string prefix in new[] { "Cue:", "Preset:", "Timeline:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        return text.Length == 0 ? null : text;
    }

    public static bool IsIdlePlayback(string? nowPlaying)
    {
        string? label = PlaybackLabel(nowPlaying);
        if (label == null)
        {
            return true;
        }

        return label.Equals("stopped", StringComparison.OrdinalIgnoreCase)
            || label.Equals("idle", StringComparison.OrdinalIgnoreCase)
            || label.Equals("none", StringComparison.OrdinalIgnoreCase)
            || label.Equals("off", StringComparison.OrdinalIgnoreCase)
            || label == "-";
    }

    internal static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string slug = Discovery.ObjectId(value);
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug.Trim('_');
    }

    private static bool IsPlaybackEntity(PluginEntity entity)
    {
        if (entity.Kind == PluginEntityKind.Scene)
        {
            return true;
        }

        string code = entity.Code;
        return code.StartsWith("cue.", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("preset.", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("timeline.", StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalId(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "";
        }

        int dot = code.IndexOf('.');
        return dot >= 0 && dot < code.Length - 1 ? code[(dot + 1)..] : code;
    }

    private static string EntitySlug(string entityId)
    {
        const string prefix = "scene.";
        string rest = entityId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? entityId[prefix.Length..]
            : entityId;
        return Slug(rest);
    }

    private static bool NamesEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SlugEquals(string? left, string? right)
    {
        string leftSlug = Slug(left);
        string rightSlug = Slug(right);
        return leftSlug.Length > 0 && string.Equals(leftSlug, rightSlug, StringComparison.OrdinalIgnoreCase);
    }
}
