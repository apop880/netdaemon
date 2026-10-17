using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Linq;

namespace HomeAssistantApps.modules;

public class Unifi(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<Unifi> logger)
{
    private const string ApiPath = "/proxy/network/integration/v1";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ILogger<Unifi> _logger = logger;
    private readonly IConfiguration _configuration = configuration;

    public async Task UpdateDNS(string newDNS)
    {
        try
        {
            var baseUrl = _configuration.GetValue<string>("UnifiOS:BaseUrl");
            var apiKey = _configuration.GetValue<string>("UnifiOS:ApiKey");
            var siteName = _configuration.GetValue<string>("UnifiOS:Site", "default");
            var networkName = _configuration.GetValue<string>("UnifiOS:Network");

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogError("Missing UnifiOS configuration values in appsettings.json. Please check BaseUrl and ApiKey.");
                return;
            }

            var httpClient = _httpClientFactory.CreateClient("IgnoreSslClient");
            httpClient.BaseAddress = new Uri(baseUrl);
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
            httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var siteId = await FindSiteId(httpClient, siteName);
            var networkId = await FindNetworkId(httpClient, siteId, networkName);
            await UpdateDNSInternal(httpClient, newDNS, siteId, networkId);
            _logger.LogInformation("DNS updated successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while updating DNS.");
        }
    }

    private async Task<JsonNode> SendAsync(HttpClient client, HttpMethod method, string path, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, $"{ApiPath}{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var responseString = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Unifi API {Method} {Path} failed with {Status}: {Response}", method, path, (int)response.StatusCode, responseString);
            response.EnsureSuccessStatusCode();
        }

        return JsonNode.Parse(responseString) ?? throw new InvalidOperationException($"Empty response from {path}");
    }

    private async Task<string> FindSiteId(HttpClient client, string siteName)
    {
        var sites = (await SendAsync(client, HttpMethod.Get, "/sites"))["data"]?.AsArray() ?? [];
        var site = sites.FirstOrDefault(s =>
                string.Equals(s?["internalReference"]?.GetValue<string>(), siteName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s?["name"]?.GetValue<string>(), siteName, StringComparison.OrdinalIgnoreCase));

        return site?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Unifi site '{siteName}' not found.");
    }

    private async Task<string> FindNetworkId(HttpClient client, string siteId, string? networkName)
    {
        var networks = (await SendAsync(client, HttpMethod.Get, $"/sites/{siteId}/networks"))["data"]?.AsArray() ?? [];
        var network = string.IsNullOrWhiteSpace(networkName)
            ? networks.FirstOrDefault(n => n?["default"]?.GetValue<bool>() == true)
            : networks.FirstOrDefault(n => string.Equals(n?["name"]?.GetValue<string>(), networkName, StringComparison.OrdinalIgnoreCase));

        return network?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Unifi network '{networkName ?? "(default)"}' not found.");
    }

    private async Task UpdateDNSInternal(HttpClient client, string newDNS, string siteId, string networkId)
    {
        var path = $"/sites/{siteId}/networks/{networkId}";
        var config = await SendAsync(client, HttpMethod.Get, path) as JsonObject
            ?? throw new InvalidOperationException("Failed to parse network configuration.");

        var dhcp = config["ipv4Configuration"]?["dhcpConfiguration"] as JsonObject;
        if (dhcp is null || dhcp["mode"]?.GetValue<string>() != "SERVER")
        {
            _logger.LogError("Network {NetworkId} is not using a DHCP server configuration; cannot set DNS.", networkId);
            return;
        }

        var current = dhcp["dnsServerIpAddressesOverride"]?.AsArray().Select(n => n?.GetValue<string>()).ToList() ?? [];
        if (current.Count == 1 && current[0] == newDNS)
        {
            _logger.LogInformation("DNS already set to {DNS}; no update needed.", newDNS);
            return;
        }

        dhcp["dnsServerIpAddressesOverride"] = new JsonArray(newDNS);

        // Read-only fields returned by GET that are not part of the update schema
        config.Remove("id");
        config.Remove("metadata");
        config.Remove("default");

        await SendAsync(client, HttpMethod.Put, path, config);
        _logger.LogInformation("Network {NetworkId} DNS changed from [{Old}] to {New}.", networkId, string.Join(", ", current), newDNS);
    }
}
