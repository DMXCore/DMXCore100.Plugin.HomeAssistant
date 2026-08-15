using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Home Assistant REST client: <c>GET /api/states</c> to discover
/// <c>scene.*</c> entities and <c>POST /api/services/scene/turn_on</c> to
/// activate one.
/// </summary>
public sealed class HomeAssistantRestClient : IHomeAssistantApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string baseUrl;
    private readonly HttpClient http;

    public HomeAssistantRestClient(string baseUrl, string accessToken, bool ignoreCertificates)
        : this(baseUrl, accessToken, CreateHandler(ignoreCertificates), disposeHandler: true)
    {
    }

    public HomeAssistantRestClient(
        string baseUrl,
        string accessToken,
        HttpMessageHandler handler,
        bool disposeHandler = false)
    {
        this.baseUrl = HomeAssistantUrl.Normalize(baseUrl)
            ?? throw new ArgumentException("Home Assistant URL is required.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Home Assistant access token is required.", nameof(accessToken));
        }

        this.http = new HttpClient(handler, disposeHandler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        this.http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<HomeAssistantScene>> GetScenesAsync(CancellationToken cancellationToken)
    {
        using var response = await this.http.GetAsync($"{this.baseUrl}/api/states", cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Home Assistant GET /api/states failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        List<HaState>? states;
        try
        {
            states = JsonSerializer.Deserialize<List<HaState>>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Home Assistant GET /api/states returned invalid JSON.", ex);
        }

        return (states ?? [])
            .Where(static state => state.EntityId.StartsWith("scene.", StringComparison.OrdinalIgnoreCase))
            .Select(static state =>
            {
                string? friendlyName = state.Attributes?.FriendlyName;
                return new HomeAssistantScene
                {
                    EntityId = state.EntityId,
                    Name = string.IsNullOrWhiteSpace(friendlyName) ? state.EntityId : friendlyName,
                };
            })
            .OrderBy(static scene => scene.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task ActivateSceneAsync(string entityId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("Entity id is required.", nameof(entityId));
        }

        string json = JsonSerializer.Serialize(new { entity_id = entityId.Trim() });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await this.http.PostAsync(
            $"{this.baseUrl}/api/services/scene/turn_on", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Home Assistant scene.turn_on failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    public void Dispose()
    {
        this.http.Dispose();
    }

    private static HttpMessageHandler CreateHandler(bool ignoreCertificates)
    {
        var handler = new HttpClientHandler();
        if (ignoreCertificates)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }

    private sealed class HaState
    {
        [JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = "";

        [JsonPropertyName("attributes")]
        public HaAttributes? Attributes { get; set; }
    }

    private sealed class HaAttributes
    {
        [JsonPropertyName("friendly_name")]
        public string? FriendlyName { get; set; }
    }
}
