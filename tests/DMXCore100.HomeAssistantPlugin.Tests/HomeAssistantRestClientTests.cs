using System.Net;
using System.Text;
using DMXCore.PluginSdk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DMXCore100.HomeAssistantPlugin.Tests;

[TestClass]
public class HomeAssistantRestClientTests
{
    [TestMethod]
    public async Task GetScenesAsync_FiltersSceneDomainAndUsesFriendlyName()
    {
        var handler = new StubHandler
        {
            Response = """
                [
                  {"entity_id":"light.kitchen","attributes":{"friendly_name":"Kitchen"}},
                  {"entity_id":"scene.movie_night","attributes":{"friendly_name":"Movie Night"}},
                  {"entity_id":"scene.evening","attributes":{}}
                ]
                """,
        };
        using var client = new HomeAssistantRestClient("http://ha.local:8123/", "token", handler);

        IReadOnlyList<HomeAssistantScene> scenes = await client.GetScenesAsync(CancellationToken.None);

        Assert.AreEqual(2, scenes.Count);
        Assert.AreEqual("scene.movie_night", scenes[0].EntityId);
        Assert.AreEqual("Movie Night", scenes[0].Name);
        Assert.AreEqual("scene.evening", scenes[1].EntityId);
        Assert.AreEqual("scene.evening", scenes[1].Name);
        Assert.AreEqual(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.AreEqual("http://ha.local:8123/api/states", handler.LastRequest.RequestUri!.ToString());
        Assert.AreEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.AreEqual("token", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public async Task GetScenesAsync_NonSuccess_Throws()
    {
        var handler = new StubHandler { Status = HttpStatusCode.Unauthorized, Response = "nope" };
        using var client = new HomeAssistantRestClient("http://ha.local:8123", "token", handler);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => client.GetScenesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ActivateSceneAsync_PostsTurnOn()
    {
        var handler = new StubHandler { Response = "[]" };
        using var client = new HomeAssistantRestClient("https://ha.example", "secret", handler);

        await client.ActivateSceneAsync("scene.movie_night", CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.AreEqual("https://ha.example/api/services/scene/turn_on", handler.LastRequest.RequestUri!.ToString());
        Assert.AreEqual("{\"entity_id\":\"scene.movie_night\"}", handler.LastBody);
        Assert.AreEqual("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [TestMethod]
    public void Normalize_AddsSchemeAndStripsSlash()
    {
        Assert.AreEqual("http://ha.local:8123", HomeAssistantUrl.Normalize("ha.local:8123/"));
        Assert.AreEqual("https://ha.local", HomeAssistantUrl.Normalize("https://ha.local/"));
        Assert.IsNull(HomeAssistantUrl.Normalize("  "));
    }

    [TestMethod]
    public void FromMdns_PrefersInternalUrl()
    {
        var services = new List<MdnsServiceInfo>
        {
            new()
            {
                InstanceName = "Home",
                Address = "192.168.1.9",
                Port = 8123,
                Properties = new Dictionary<string, string>
                {
                    ["internal_url"] = "http://homeassistant.local:8123/",
                },
            },
        };

        Assert.AreEqual("http://homeassistant.local:8123", HomeAssistantUrl.FromMdns(services));
    }

    [TestMethod]
    public void FromMdns_FallsBackToAddressAndPort()
    {
        var services = new List<MdnsServiceInfo>
        {
            new() { InstanceName = "Home", Address = "10.0.0.5", Port = 8123, Properties = new Dictionary<string, string>() },
        };

        Assert.AreEqual("http://10.0.0.5:8123", HomeAssistantUrl.FromMdns(services));
    }

    [TestMethod]
    public void Scene_F1Go_MatchesSlugAndDisplayName()
    {
        var scene = new HomeAssistantScene { EntityId = "scene.f1_go", Name = "F1 Go" };

        Assert.IsTrue(scene.MatchesPlayback("F1GO", "F1 Go"));
        Assert.IsTrue(scene.MatchesPlayback(null, "F1 Go"));
        Assert.IsTrue(scene.MatchesPlayback("cue.F1GO", "F1 Go"));
        Assert.AreEqual(scene, HomeAssistantScene.Find([scene], "F1GO", "F1 Go"));
    }

    [TestMethod]
    public void FindCatalogEntity_ResolvesShortcodeToDisplayName()
    {
        PluginEntity[] catalog =
        [
            new() { Code = "cue.F1GO", Name = "F1 Go", Kind = PluginEntityKind.Scene },
        ];

        PluginEntity? found = HomeAssistantScene.FindCatalogEntity(catalog, "F1GO");
        Assert.IsNotNull(found);
        Assert.AreEqual("F1 Go", found.Name);
        Assert.AreEqual("F1 Go", HomeAssistantScene.PlaybackLabel("Cue: F1 Go"));
        Assert.IsTrue(HomeAssistantScene.IsIdlePlayback(null));
        Assert.IsTrue(HomeAssistantScene.IsIdlePlayback(""));
        Assert.IsTrue(HomeAssistantScene.IsIdlePlayback("stopped"));
        Assert.IsFalse(HomeAssistantScene.IsIdlePlayback("Cue: F1 Go"));
    }

    [TestMethod]
    public void FindConfigured_MatchesNameEntityIdOrBareSceneId()
    {
        var scene = new HomeAssistantScene { EntityId = "scene.all_off", Name = "All Off" };

        Assert.AreEqual(scene, HomeAssistantScene.FindConfigured([scene], "All Off"));
        Assert.AreEqual(scene, HomeAssistantScene.FindConfigured([scene], "scene.all_off"));
        Assert.AreEqual("scene.living_off", HomeAssistantScene.FindConfigured([], "scene.living_off")!.EntityId);
        Assert.IsNull(HomeAssistantScene.FindConfigured([scene], ""));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Response { get; set; } = "[]";

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            this.LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(this.Status)
            {
                Content = new StringContent(this.Response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
