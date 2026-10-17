using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace netdaemon.Tests;

public class UnifiTests
{
    private const string SiteId = "88f7af54-98f8-306a-a1c7-c9349722b1f6";
    private const string NetworkId = "5a5bace7-85db-4759-9682-47446e1ec76e";
    private const string ApiBase = "/proxy/network/integration/v1";

    [Fact]
    public async Task UpdateDNS_PutsNewDnsAndStripsReadOnlyFields()
    {
        var handler = new FakeUnifiHandler();
        var unifi = CreateUnifi(handler);

        await unifi.UpdateDNS("8.8.8.8");

        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal($"{ApiBase}/sites/{SiteId}/networks/{NetworkId}", put.Path);

        var body = JsonNode.Parse(put.Body!)!.AsObject();
        var dns = body["ipv4Configuration"]!["dhcpConfiguration"]!["dnsServerIpAddressesOverride"]!.AsArray();
        Assert.Equal(["8.8.8.8"], dns.Select(n => n!.GetValue<string>()));

        Assert.False(body.ContainsKey("id"));
        Assert.False(body.ContainsKey("metadata"));
        Assert.False(body.ContainsKey("default"));

        // Everything else is preserved because PUT is a full replacement
        Assert.Equal("GATEWAY", body["management"]!.GetValue<string>());
        Assert.Equal("192.168.1.1", body["ipv4Configuration"]!["hostIpAddress"]!.GetValue<string>());
        Assert.Equal("localdomain", body["ipv4Configuration"]!["dhcpConfiguration"]!["domainName"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateDNS_SendsApiKeyOnEveryRequest()
    {
        var handler = new FakeUnifiHandler();
        var unifi = CreateUnifi(handler);

        await unifi.UpdateDNS("8.8.8.8");

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Equal("test-key", r.ApiKey));
    }

    [Fact]
    public async Task UpdateDNS_SkipsPutWhenDnsAlreadySet()
    {
        var handler = new FakeUnifiHandler { CurrentDns = "8.8.8.8" };
        var unifi = CreateUnifi(handler);

        await unifi.UpdateDNS("8.8.8.8");

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task UpdateDNS_SelectsNetworkByName_WhenConfigured()
    {
        var handler = new FakeUnifiHandler();
        var unifi = CreateUnifi(handler, ("UnifiOS:Network", "IoT"));

        await unifi.UpdateDNS("8.8.8.8");

        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.EndsWith("/networks/iot-network-id", put.Path);
    }

    [Fact]
    public async Task UpdateDNS_DoesNotThrow_AndDoesNotPut_WhenSiteNotFound()
    {
        var handler = new FakeUnifiHandler();
        var unifi = CreateUnifi(handler, ("UnifiOS:Site", "missing"));

        await unifi.UpdateDNS("8.8.8.8");

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task UpdateDNS_DoesNotThrow_WhenPutFails()
    {
        var handler = new FakeUnifiHandler { PutStatus = HttpStatusCode.BadRequest };
        var unifi = CreateUnifi(handler);

        var exception = await Record.ExceptionAsync(() => unifi.UpdateDNS("8.8.8.8"));

        Assert.Null(exception);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task UpdateDNS_MakesNoRequests_WhenConfigMissing()
    {
        var handler = new FakeUnifiHandler();
        var unifi = CreateUnifi(handler, ("UnifiOS:ApiKey", ""));

        await unifi.UpdateDNS("8.8.8.8");

        Assert.Empty(handler.Requests);
    }

    private static Unifi CreateUnifi(FakeUnifiHandler handler, params (string Key, string Value)[] overrides)
    {
        Dictionary<string, string?> settings = new()
        {
            ["UnifiOS:BaseUrl"] = "https://unifi.test:11443",
            ["UnifiOS:ApiKey"] = "test-key",
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new Unifi(new FakeHttpClientFactory(handler), configuration, NullLogger<Unifi>.Instance);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? ApiKey);

    private sealed class FakeUnifiHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public string CurrentDns { get; init; } = "192.168.1.6";
        public HttpStatusCode PutStatus { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            request.Headers.TryGetValues("X-API-KEY", out var keys);
            Requests.Add(new RecordedRequest(request.Method, path, body, keys?.FirstOrDefault()));

            if (request.Method == HttpMethod.Put)
            {
                return Json(PutStatus, PutStatus == HttpStatusCode.OK ? body! : """{"message":"bad request"}""");
            }

            return path switch
            {
                $"{ApiBase}/sites" => Json(HttpStatusCode.OK, $$"""
                    {"offset":0,"limit":25,"count":2,"totalCount":2,"data":[
                      {"id":"{{SiteId}}","internalReference":"default","name":"Home"},
                      {"id":"20497069-013a-3930-a272-14360cb70edb","internalReference":"asuoua14","name":"RiverPark"}]}
                    """),
                $"{ApiBase}/sites/{SiteId}/networks" => Json(HttpStatusCode.OK, $$"""
                    {"offset":0,"limit":25,"count":2,"totalCount":2,"data":[
                      {"management":"GATEWAY","id":"{{NetworkId}}","name":"Default","enabled":true,"vlanId":1,"default":true},
                      {"management":"GATEWAY","id":"iot-network-id","name":"IoT","enabled":true,"vlanId":20,"default":false}]}
                    """),
                $"{ApiBase}/sites/{SiteId}/networks/{NetworkId}" => Json(HttpStatusCode.OK, NetworkJson(NetworkId, "Default", 1, true)),
                $"{ApiBase}/sites/{SiteId}/networks/iot-network-id" => Json(HttpStatusCode.OK, NetworkJson("iot-network-id", "IoT", 20, false)),
                _ => Json(HttpStatusCode.NotFound, """{"message":"not found"}"""),
            };
        }

        private string NetworkJson(string id, string name, int vlan, bool isDefault) => $$$"""
            {
              "management":"GATEWAY","id":"{{{id}}}","name":"{{{name}}}","enabled":true,"vlanId":{{{vlan}}},
              "metadata":{"origin":"SYSTEM_DEFINED","configurable":true},
              "isolationEnabled":false,"cellularBackupEnabled":true,
              "internetAccessEnabled":true,"mdnsForwardingEnabled":true,
              "ipv4Configuration":{
                "autoScaleEnabled":false,"hostIpAddress":"192.168.1.1","prefixLength":24,
                "dhcpConfiguration":{
                  "mode":"SERVER",
                  "ipAddressRange":{"start":"192.168.1.15","stop":"192.168.1.254"},
                  "dnsServerIpAddressesOverride":["{{{CurrentDns}}}"],
                  "leaseTimeSeconds":86400,"domainName":"localdomain","pingConflictDetectionEnabled":true}},
              "default":{{{isDefault.ToString().ToLowerInvariant()}}}
            }
            """;

        private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
