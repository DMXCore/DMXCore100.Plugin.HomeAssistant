using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DMXCore.PluginSdk;

namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Minimal Home Assistant REST API client for the Core→HA direction: list
/// the scenes/scripts/automations HA offers and fire one. Authenticates
/// with a long-lived access token (HA profile → Security). All failures
/// surface as exceptions with a message fit for the Output Event Test
/// button; the host never counts them against the plugin's fault budget.
/// </summary>
public sealed class HaRestClient : IDisposable
{
    /// <summary>
    /// The HA entity domains offered as targets, in display order, with the
    /// service that fires them.
    /// </summary>
    public static readonly IReadOnlyList<(string Domain, string Group, string Service)> TargetDomains =
    [
        ("scene", "Scenes", "turn_on"),
        ("script", "Scripts", "turn_on"),
        ("automation", "Automations", "trigger"),
    ];

    private readonly HttpClient http;

    public HaRestClient(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        this.http = handler == null ? new HttpClient() : new HttpClient(handler);
        this.http.BaseAddress = new Uri(baseUrl.Trim().TrimEnd('/') + "/");
        // Shorter than the host's 10 s action timeout so the plugin's more
        // specific "did not answer" message is the one the user sees
        this.http.Timeout = TimeSpan.FromSeconds(6);
        this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        this.http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseAddress => this.http.BaseAddress!;

    /// <summary>
    /// Verify the URL + token: GET /api/ answers 200 with a message when
    /// the token is accepted. Throws with a readable reason otherwise.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/"), cancellationToken);
        await EnsureSuccess(response, "API check", cancellationToken);
    }

    /// <summary>
    /// The scenes, scripts, and automations HA currently has, sorted by
    /// group (domain order) then label.
    /// </summary>
    public async Task<IReadOnlyList<PluginActionTarget>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/states"), cancellationToken);
        await EnsureSuccess(response, "listing entities", cancellationToken);

        JsonNode? root;
        try
        {
            root = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Unexpected response from Home Assistant when listing entities: {ex.Message}");
        }

        return ParseTargets(root);
    }

    /// <summary>
    /// Fire a target: scene.turn_on / script.turn_on / automation.trigger
    /// with the entity id. <paramref name="payload"/>, when it is a JSON
    /// object, is merged into the service data (e.g. script variables).
    /// </summary>
    public async Task ExecuteAsync(string targetId, string? payload, CancellationToken cancellationToken)
    {
        string domain = targetId.Split('.', 2)[0];
        var target = TargetDomains.FirstOrDefault(x => string.Equals(x.Domain, domain, StringComparison.OrdinalIgnoreCase));
        if (target.Domain == null)
        {
            throw new InvalidOperationException($"'{targetId}' is not a scene, script, or automation entity id");
        }

        var data = BuildServiceData(targetId, payload);

        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"api/services/{target.Domain}/{target.Service}");
            request.Content = JsonContent.Create(data);

            return request;
        }, cancellationToken);

        await EnsureSuccess(response, $"calling {target.Domain}.{target.Service} for {targetId}", cancellationToken);
    }

    public void Dispose()
    {
        this.http.Dispose();
    }

    /// <summary>
    /// Pure: turn the /api/states array into targets. Public for tests.
    /// </summary>
    public static IReadOnlyList<PluginActionTarget> ParseTargets(JsonNode? statesRoot)
    {
        if (statesRoot is not JsonArray states)
        {
            throw new InvalidOperationException("Unexpected response from Home Assistant when listing entities: not a list");
        }

        var result = new List<(int Order, PluginActionTarget Target)>();

        foreach (var state in states)
        {
            string? entityId = state?["entity_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(entityId))
                continue;

            string domain = entityId.Split('.', 2)[0];
            int order = -1;
            for (int i = 0; i < TargetDomains.Count; i++)
            {
                if (string.Equals(TargetDomains[i].Domain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    order = i;
                    break;
                }
            }

            if (order < 0)
                continue;

            string? friendlyName = null;
            var attributes = state?["attributes"];
            if (attributes is JsonObject && attributes["friendly_name"] is JsonValue nameValue)
            {
                friendlyName = nameValue.TryGetValue<string>(out string? s) ? s : nameValue.ToString();
            }

            result.Add((order, new PluginActionTarget
            {
                Id = entityId,
                Label = string.IsNullOrWhiteSpace(friendlyName) ? entityId : friendlyName,
                Group = TargetDomains[order].Group,
            }));
        }

        return result
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Target.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Target)
            .ToList();
    }

    /// <summary>
    /// Pure: service-call body for a target. Public for tests.
    /// </summary>
    public static JsonObject BuildServiceData(string targetId, string? payload)
    {
        JsonObject data = new();

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                if (JsonNode.Parse(payload) is JsonObject extra)
                {
                    foreach (var (key, value) in extra.ToList())
                    {
                        data[key] = value?.DeepClone();
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON payloads are ignored: nothing sensible to send
            }
        }

        data["entity_id"] = targetId;

        return data;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        using var request = requestFactory();

        try
        {
            return await this.http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Cannot reach Home Assistant at {this.http.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Home Assistant at {this.http.BaseAddress} did not answer in time");
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string detail = string.Empty;
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body) && body.Length <= 200)
                detail = $" ({body.Trim()})";
        }
        catch
        {
        }

        throw response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => new InvalidOperationException("Home Assistant rejected the access token (401); create a long-lived access token in your HA profile"),
            System.Net.HttpStatusCode.NotFound => new InvalidOperationException($"Home Assistant returned 404 while {what}{detail}"),
            _ => new InvalidOperationException($"Home Assistant returned {(int)response.StatusCode} while {what}{detail}"),
        };
    }
}
