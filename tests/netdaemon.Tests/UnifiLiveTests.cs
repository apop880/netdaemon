using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace netdaemon.Tests;

/// <summary>
/// Runs against the real UniFi gateway configured in the repo's appsettings.json. These tests CHANGE
/// the live DHCP DNS setting and restore it afterwards, so they only run when UNIFI_LIVE_TEST=1.
///
///   UNIFI_LIVE_TEST=1                run the live tests
///   UNIFI_TEST_DNS=1.1.1.1           DNS value to apply (default 1.1.1.1)
///   UNIFI_LIVE_PAUSE_SECONDS=60      wait after applying so you can check it in the UniFi UI (default 0)
///
///   UNIFI_LIVE_TEST=1 UNIFI_LIVE_PAUSE_SECONDS=60 dotnet test tests/netdaemon.Tests --filter Live --logger "console;verbosity=detailed"
/// </summary>
public class UnifiLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [LiveFact]
    public async Task Live_UpdateDNS_ChangesGatewayDnsAndRestoresIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepoRoot(), "appsettings.json"), optional: false)
            .Build();

        var testDns = Environment.GetEnvironmentVariable("UNIFI_TEST_DNS") ?? "1.1.1.1";
        var pauseSeconds = int.TryParse(Environment.GetEnvironmentVariable("UNIFI_LIVE_PAUSE_SECONDS"), out var s) ? s : 0;

        var factory = new InsecureHttpClientFactory();
        var unifi = new Unifi(factory, configuration, NullLogger<Unifi>.Instance);

        using var client = factory.CreateClient("IgnoreSslClient");
        client.BaseAddress = new Uri(configuration["UnifiOS:BaseUrl"]!);
        client.DefaultRequestHeaders.Add("X-API-KEY", configuration["UnifiOS:ApiKey"]);

        var networkPath = await FindDefaultNetworkPath(client);
        var original = await ReadDns(client, networkPath);
        output.WriteLine($"Original DNS: [{string.Join(", ", original)}]");
        Assert.NotEmpty(original);

        try
        {
            await unifi.UpdateDNS(testDns);

            var updated = await ReadDns(client, networkPath);
            output.WriteLine($"DNS after update: [{string.Join(", ", updated)}]");
            Assert.Equal([testDns], updated);

            if (pauseSeconds > 0)
            {
                output.WriteLine($"Pausing {pauseSeconds}s - check Settings > Networks > Default > DHCP DNS Server in the UniFi UI.");
                await Task.Delay(TimeSpan.FromSeconds(pauseSeconds));
            }
        }
        finally
        {
            await RestoreDns(client, networkPath, original);
            output.WriteLine($"Restored DNS: [{string.Join(", ", await ReadDns(client, networkPath))}]");
        }
    }

    private const string ApiPath = "/proxy/network/integration/v1";

    private static async Task<string> FindDefaultNetworkPath(HttpClient client)
    {
        var sites = (await GetJson(client, "/sites"))["data"]!.AsArray();
        var siteId = sites.First(x => x!["internalReference"]!.GetValue<string>() == "default")!["id"]!.GetValue<string>();
        var networks = (await GetJson(client, $"/sites/{siteId}/networks"))["data"]!.AsArray();
        var networkId = networks.First(x => x!["default"]!.GetValue<bool>())!["id"]!.GetValue<string>();
        return $"/sites/{siteId}/networks/{networkId}";
    }

    private static async Task<List<string>> ReadDns(HttpClient client, string networkPath)
    {
        var network = await GetJson(client, networkPath);
        return network["ipv4Configuration"]!["dhcpConfiguration"]!["dnsServerIpAddressesOverride"]?
            .AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
    }

    private static async Task RestoreDns(HttpClient client, string networkPath, List<string> dns)
    {
        var network = (await GetJson(client, networkPath)).AsObject();
        network["ipv4Configuration"]!["dhcpConfiguration"]!["dnsServerIpAddressesOverride"] = new JsonArray(dns.Select(d => (JsonNode?)d).ToArray());
        network.Remove("id");
        network.Remove("metadata");
        network.Remove("default");

        using var response = await client.PutAsync($"{ApiPath}{networkPath}", new StringContent(network.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonNode> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync($"{ApiPath}{path}");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "netdaemon.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (netdaemon.csproj).");
    }

    private sealed class InsecureHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClientHandler _handler = new()
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("UNIFI_LIVE_TEST") != "1")
        {
            Skip = "Set UNIFI_LIVE_TEST=1 to run against the real UniFi gateway.";
        }
    }
}
