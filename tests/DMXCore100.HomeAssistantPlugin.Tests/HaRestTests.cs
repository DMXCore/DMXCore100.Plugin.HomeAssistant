using System.Net;
using System.Text.Json.Nodes;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DMXCore100.HomeAssistantPlugin.Tests;

/// <summary>
/// Core→HA direction: REST client request shapes and parsing, the action
/// provider's caching/health reporting, and the plugin's register/
/// unregister behavior driven by the URL + token settings.
/// </summary>
[TestClass]
public class HaRestTests
{
    private const string StatesJson = """
        [
          {"entity_id": "light.kitchen", "state": "on", "attributes": {"friendly_name": "Kitchen"}},
          {"entity_id": "scene.movie_night", "state": "unknown", "attributes": {"friendly_name": "Movie Night"}},
          {"entity_id": "scene.bright", "state": "unknown", "attributes": {"friendly_name": "All Bright"}},
          {"entity_id": "script.party_mode", "state": "off", "attributes": {"friendly_name": "Party Mode"}},
          {"entity_id": "automation.goodnight", "state": "on", "attributes": {}},
          {"entity_id": "switch.fan", "state": "off", "attributes": {"friendly_name": "Fan"}}
        ]
        """;

    [TestMethod]
    public void ParseTargets_FiltersDomains_SortsByGroupThenLabel_FallsBackToEntityId()
    {
        var targets = HaRestClient.ParseTargets(JsonNode.Parse(StatesJson));

        CollectionAssert.AreEqual(
            new[] { "scene.bright", "scene.movie_night", "script.party_mode", "automation.goodnight" },
            targets.Select(x => x.Id).ToArray());
        Assert.AreEqual("All Bright", targets[0].Label);
        Assert.AreEqual("Scenes", targets[0].Group);
        Assert.AreEqual("Scripts", targets[2].Group);
        Assert.AreEqual("automation.goodnight", targets[3].Label, "no friendly_name -> entity id");
        Assert.AreEqual("Automations", targets[3].Group);
    }

    [TestMethod]
    public void BuildServiceData_MergesJsonObjectPayload_IgnoresOtherPayloads()
    {
        var plain = HaRestClient.BuildServiceData("scene.a", null);
        Assert.AreEqual("""{"entity_id":"scene.a"}""", plain.ToJsonString());

        var merged = HaRestClient.BuildServiceData("script.b", """{"variables":{"level":50}}""");
        Assert.AreEqual("""{"variables":{"level":50},"entity_id":"script.b"}""", merged.ToJsonString());

        var junk = HaRestClient.BuildServiceData("scene.c", "not json");
        Assert.AreEqual("""{"entity_id":"scene.c"}""", junk.ToJsonString());
    }

    [TestMethod]
    public async Task Execute_PostsToDomainService_WithBearerToken()
    {
        var handler = new FakeHandler();
        handler.Respond("POST", "/api/services/scene/turn_on", HttpStatusCode.OK, "[]");
        using var client = new HaRestClient("http://ha.local:8123/", "tok-123", handler);

        await client.ExecuteAsync("scene.movie_night", null, CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.AreEqual("Bearer tok-123", request.Authorization);
        Assert.AreEqual("http://ha.local:8123/api/services/scene/turn_on", request.Url);
        Assert.AreEqual("""{"entity_id":"scene.movie_night"}""", request.Body);
    }

    [TestMethod]
    public async Task Execute_AutomationUsesTriggerService()
    {
        var handler = new FakeHandler();
        handler.Respond("POST", "/api/services/automation/trigger", HttpStatusCode.OK, "[]");
        using var client = new HaRestClient("http://ha.local:8123", "t", handler);

        await client.ExecuteAsync("automation.goodnight", null, CancellationToken.None);

        Assert.AreEqual("http://ha.local:8123/api/services/automation/trigger", handler.Requests.Single().Url);
    }

    [TestMethod]
    public async Task Execute_UnsupportedDomain_ThrowsWithoutRequest()
    {
        var handler = new FakeHandler();
        using var client = new HaRestClient("http://ha.local:8123", "t", handler);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.ExecuteAsync("light.kitchen", null, CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task Unauthorized_ThrowsReadableTokenMessage()
    {
        var handler = new FakeHandler();
        handler.Respond("GET", "/api/", HttpStatusCode.Unauthorized, "401: Unauthorized");
        using var client = new HaRestClient("http://ha.local:8123", "bad", handler);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.CheckAsync(CancellationToken.None));
        StringAssert.Contains(ex.Message, "access token");
    }

    [TestMethod]
    public async Task GetTargets_ParsesStates()
    {
        var handler = new FakeHandler();
        handler.Respond("GET", "/api/states", HttpStatusCode.OK, StatesJson);
        using var client = new HaRestClient("http://ha.local:8123", "t", handler);

        var targets = await client.GetTargetsAsync(CancellationToken.None);

        Assert.AreEqual(4, targets.Count);
    }

    [TestMethod]
    public async Task Provider_CachesTargets_AndReportsHealth()
    {
        var handler = new FakeHandler();
        handler.Respond("GET", "/api/states", HttpStatusCode.OK, StatesJson);
        using var client = new HaRestClient("http://ha.local:8123", "t", handler);

        var health = new List<(bool, string?)>();
        var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var provider = new HaActionProvider(client, (ok, d) => health.Add((ok, d)), () => now);

        await provider.GetTargetsAsync(CancellationToken.None);
        await provider.GetTargetsAsync(CancellationToken.None);
        Assert.AreEqual(1, handler.Requests.Count, "second call within the cache window is served from cache");

        now += HaActionProvider.CacheDuration + TimeSpan.FromSeconds(1);
        await provider.GetTargetsAsync(CancellationToken.None);
        Assert.AreEqual(2, handler.Requests.Count);

        Assert.IsTrue(health.All(x => x.Item1));
    }

    [TestMethod]
    public async Task Provider_ExecuteFailure_ReportsUnhealthyAndRethrows()
    {
        var handler = new FakeHandler();
        handler.Respond("POST", "/api/services/scene/turn_on", HttpStatusCode.InternalServerError, "boom");
        using var client = new HaRestClient("http://ha.local:8123", "t", handler);

        var health = new List<(bool Ok, string? Detail)>();
        var provider = new HaActionProvider(client, (ok, d) => health.Add((ok, d)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ExecuteAsync("scene.a", null, CancellationToken.None));

        Assert.AreEqual(1, health.Count);
        Assert.IsFalse(health[0].Ok);
        StringAssert.Contains(health[0].Detail!, "500");
    }

    [TestMethod]
    public async Task Plugin_NoUrlOrToken_DoesNotRegisterProvider()
    {
        var plugin = new HomeAssistantPlugin();
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });

        await plugin.InitializeAsync(host, CancellationToken.None);

        Assert.IsNull(host.ActionProvider);
        StringAssert.DoesNotMatch(host.ConnectionDetail ?? string.Empty, new System.Text.RegularExpressions.Regex("HA API"));
    }

    [TestMethod]
    public async Task Plugin_UrlAndToken_RegistersProvider_ClearedSettingsUnregister()
    {
        var plugin = new HomeAssistantPlugin { RestHandler = OnlineHandler() };
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting("ha-url", "http://ha.local:8123");
        host.SetSetting("ha-token", "tok");

        await plugin.InitializeAsync(host, CancellationToken.None);

        Assert.IsNotNull(host.ActionProvider);
        Assert.IsInstanceOfType<HaActionProvider>(host.ActionProvider);
        Assert.AreEqual(1, host.PeriodicTasks.Count, "health re-check scheduled");
        StringAssert.Contains(host.ConnectionDetail!, "HA API");

        host.SetSetting("ha-token", "");
        await host.TriggerSettingsChangedAsync();

        Assert.IsNull(host.ActionProvider, "clearing the token drops the provider");
        StringAssert.DoesNotMatch(host.ConnectionDetail ?? string.Empty, new System.Text.RegularExpressions.Regex("HA API"));
    }

    [TestMethod]
    public async Task Plugin_ChangedUrl_ReplacesProvider()
    {
        var plugin = new HomeAssistantPlugin { RestHandler = OnlineHandler() };
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting("ha-url", "http://ha.local:8123");
        host.SetSetting("ha-token", "tok");
        await plugin.InitializeAsync(host, CancellationToken.None);
        var first = host.ActionProvider;

        host.SetSetting("ha-url", "http://other.local:8123");
        await host.TriggerSettingsChangedAsync();

        Assert.IsNotNull(host.ActionProvider);
        Assert.AreNotSame(first, host.ActionProvider);
    }

    [TestMethod]
    public async Task Plugin_StaleHealthCheck_DoesNotOverwriteReplacementClient()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StaleHealthHandler(firstStarted, releaseFirst);
        var plugin = new HomeAssistantPlugin { RestHandler = handler };
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting("ha-url", "http://ha.local:8123");
        host.SetSetting("ha-token", "tok");

        Task init = plugin.InitializeAsync(host, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        host.SetSetting("ha-url", "http://other.local:8123");
        await host.TriggerSettingsChangedAsync();

        StringAssert.Contains(host.ConnectionDetail!, "HA API ok");

        releaseFirst.TrySetResult();
        await init.WaitAsync(TimeSpan.FromSeconds(5));

        StringAssert.Contains(host.ConnectionDetail!, "HA API ok");
        StringAssert.DoesNotMatch(host.ConnectionDetail ?? string.Empty, new System.Text.RegularExpressions.Regex("500"));
    }

    [TestMethod]
    public async Task Plugin_InvalidUrl_ReportsDisconnected_NoProvider()
    {
        var plugin = new HomeAssistantPlugin();
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        host.SetSetting("ha-url", "not a url");
        host.SetSetting("ha-token", "tok");

        await plugin.InitializeAsync(host, CancellationToken.None);

        Assert.IsNull(host.ActionProvider);
        Assert.IsFalse(host.ConnectionState);
        StringAssert.Contains(host.ConnectionDetail!, "invalid URL");
    }

    private static FakeHandler OnlineHandler()
    {
        var handler = new FakeHandler();
        handler.Respond("GET", "/api/", HttpStatusCode.OK, """{"message":"API running."}""");
        handler.Respond("GET", "/api/states", HttpStatusCode.OK, "[]");
        return handler;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly object requestsLock = new();

        public List<(string Method, string Url, string? Authorization, string? Body)> Requests { get; } = [];

        public void Respond(string method, string path, HttpStatusCode status, string body)
        {
            this.responses[$"{method} {path}"] = (status, body);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (this.requestsLock)
            {
                this.Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            }

            if (!this.responses.TryGetValue($"{request.Method.Method} {request.RequestUri.AbsolutePath}", out var response))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no fake response") };

            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) };
        }
    }

    private sealed class StaleHealthHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstStarted;
        private readonly TaskCompletionSource releaseFirst;

        public StaleHealthHandler(TaskCompletionSource firstStarted, TaskCompletionSource releaseFirst)
        {
            this.firstStarted = firstStarted;
            this.releaseFirst = releaseFirst;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/api/")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (string.Equals(request.RequestUri.Host, "ha.local", StringComparison.OrdinalIgnoreCase))
            {
                this.firstStarted.TrySetResult();
                await this.releaseFirst.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("stale"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":"API running."}"""),
            };
        }
    }
}
