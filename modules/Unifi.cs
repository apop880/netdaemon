using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Linq;

namespace HomeAssistantApps.modules;

public class Unifi(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<Unifi> logger)
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ILogger<Unifi> _logger = logger;
    private readonly IConfiguration _configuration = configuration;
    public async Task UpdateDNS(string newDNS)
    {
        try
        {
            var baseUrl = _configuration.GetValue<string>("Unifi:BaseUrl");
            var username = _configuration.GetValue<string>("Unifi:Username");
            var password = _configuration.GetValue<string>("Unifi:Password");
            var site = _configuration.GetValue<string>("Unifi:Site");
            var networkId = _configuration.GetValue<string>("Unifi:NetworkId");

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(networkId))
            {
                _logger.LogError("Missing Unifi configuration values in appsettings.json. Please check BaseUrl, Username, Password, Site, and NetworkId.");
                return;
            }

            var httpClient = _httpClientFactory.CreateClient("IgnoreSslClient");
            httpClient.BaseAddress = new Uri(baseUrl);
            var loginResponse = await Login(httpClient, _logger, username, password);

            // Manually forward the authentication cookie
            if (loginResponse.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
            {
                var cookieHeader = string.Join("; ", setCookieValues.Select(c => c.Split(';')[0]));
                httpClient.DefaultRequestHeaders.Add("Cookie", cookieHeader);
                _logger.LogInformation("Manually added cookies to subsequent requests: {CookieHeader}", cookieHeader);
            }

            // Extract CSRF token (prefer header, fallback to cookie)
            string? csrfToken = null;

            // Check response headers first (updated case)
            if (loginResponse.Headers.TryGetValues("X-CSRF-Token", out var headerValues))
            {
                csrfToken = headerValues.FirstOrDefault();
                _logger.LogInformation("CSRF token extracted from header: {CsrfToken}", csrfToken);
            }

            // Fallback to cookie if not in header
            if (string.IsNullOrEmpty(csrfToken) && loginResponse.Headers.TryGetValues("Set-Cookie", out var cookieVals))
            {
                var csrfCookie = cookieVals.FirstOrDefault(c => c.Contains("csrf_token="));
                if (csrfCookie != null)
                {
                    var tokenStart = csrfCookie.IndexOf("csrf_token=") + "csrf_token=".Length;
                    var tokenEnd = csrfCookie.IndexOf(';', tokenStart);
                    if (tokenEnd == -1) tokenEnd = csrfCookie.Length;
                    csrfToken = csrfCookie[tokenStart..tokenEnd];
                    _logger.LogInformation("CSRF token extracted from Set-Cookie: {CsrfToken}", csrfToken);
                }
            }

            if (!string.IsNullOrEmpty(csrfToken))
            {
                httpClient.DefaultRequestHeaders.Add("X-CSRF-Token", csrfToken);  // Updated case
            }
            else
            {
                _logger.LogWarning("CSRF token not found - proceeding without (may fail).");
            }

            // Browser-like User-Agent to mimic Thunder Client
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));

            // Removed Referrer – not needed for API

            await UpdateDNSInternal(httpClient, newDNS, site, networkId);
            _logger.LogInformation("DNS updated successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while updating DNS.");
        }
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, ILogger<Unifi> logger, string username, string password)
    {
        var loginUrl = "/api/login";  // Test "/api/auth/login" if this fails
        var data = new { username = username, password = password, remember = true, strict = true };
        var content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(loginUrl, content);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task UpdateDNSInternal(HttpClient client, string newDNS, string site, string networkId)
    {
        var getUrl = $"/api/s/{site}/rest/networkconf/{networkId}";
        var getResponse = await client.GetAsync(getUrl);
        getResponse.EnsureSuccessStatusCode();
        var configJson = await getResponse.Content.ReadAsStringAsync();
        _logger.LogInformation("GET network config response: {ConfigJson}", configJson);
        var config = JsonNode.Parse(configJson)?["data"]?[0] as JsonObject;

        if (config is not null)
        {
            // Update DNS field
            config["dhcpd_dns_1"] = newDNS;

            var updatedConfigJson = JsonSerializer.Serialize(config);
            _logger.LogInformation("Updated config to PUT: {UpdatedConfigJson}", updatedConfigJson);

            var putUrl = $"/api/s/{site}/rest/networkconf/{networkId}";
            var putContent = new StringContent(updatedConfigJson, Encoding.UTF8, "application/json");

            var putResponse = await client.PutAsync(putUrl, putContent);
            var responseString = await putResponse.Content.ReadAsStringAsync();
            _logger.LogInformation("Unifi controller response: {Response}", responseString);
            putResponse.EnsureSuccessStatusCode();

            var responseNode = JsonNode.Parse(responseString);
            var rc = responseNode?["meta"]?["rc"]?.ToString();
            if (rc != "ok")
            {
                var msg = responseNode?["meta"]?["msg"]?.ToString() ?? "Unknown error";
                _logger.LogError("API internal error: {Rc} - {Msg}", rc, msg);
                throw new Exception($"API update failed: {msg}");
            }
        }
        else
        {
            _logger.LogError("Failed to parse network configuration.");
        }
    }
}