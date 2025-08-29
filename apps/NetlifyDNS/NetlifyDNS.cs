using System.Net.Http;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;


namespace HomeAssistantApps;

[NetDaemonApp]
public class NetlifyDNS
{
    private readonly string _accessToken;
    private readonly ILogger<NetlifyDNS> _logger;

    public NetlifyDNS(IAppConfig<NetlifyDNSConfig> config, ILogger<NetlifyDNS> logger, SensorEntities entities, IConfiguration configuration, IHttpClientFactory httpClientFactory, Telegram telegram)
    {
        _logger = logger;
        _logger.LogInformation("NetlifyDNS App starting with configuration: {Config}", JsonSerializer.Serialize(config.Value));

        var _httpClient = httpClientFactory.CreateClient();
        _accessToken = configuration.GetSection("Netlify:Token").Value
            ?? throw new InvalidOperationException("Netlify access token not found in configuration.");
        _ = entities.Myip.StateChangesWithCurrent().SubscribeAsync(async s =>
        {
            _logger.LogInformation("Detected IP address change. New IP: {IP}", s.New?.State);

            if (s.New?.State is not null && config.Value.DnsZones is not null && s.New.State != "unknown")
            {
                foreach (var dnsZone in config.Value.DnsZones.Where(z => z.DnsZoneName is not null && z.Domains is not null))
                {
                    _logger.LogInformation("Processing DNS Zone: {ZoneName}", dnsZone.DnsZoneName);
                    var apiZoneName = dnsZone.DnsZoneName!.Replace(".", "_");
                    _logger.LogDebug("Formatted API Zone Name for Netlify API: {ApiZoneName}", apiZoneName);

                    string getPostUrl = $"https://api.netlify.com/api/v1/dns_zones/{apiZoneName}/dns_records?access_token={_accessToken}";
                    _logger.LogInformation("Fetching existing DNS records from Netlify API");
                    try
                    {
                        HttpResponseMessage response = await _httpClient.GetAsync(getPostUrl);
                        string responseBody = await response.Content.ReadAsStringAsync();
                        _logger.LogInformation("Netlify API GET request returned status {StatusCode}", response.StatusCode);
                        _logger.LogDebug("Netlify GET response body: {Body}", responseBody);

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogError("Failed to fetch DNS records for zone {ZoneName}. Status: {StatusCode}. Body: {Body}", dnsZone.DnsZoneName, response.StatusCode, responseBody);
                            continue; // Skip to next zone
                        }

                        // Deserialize JSON to list of records
                        var records = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(responseBody) ?? new List<Dictionary<string, object>>();

                        // Delete matching existing records
                        foreach (var record in records.Where(r => r.ContainsKey("hostname") && r.ContainsKey("id")))
                        {
                            if (dnsZone.Domains!.Contains(record["hostname"].ToString() ?? string.Empty))
                            {
                                string recordId = record["id"].ToString()!;
                                string recordHostname = record["hostname"].ToString()!;
                                _logger.LogInformation("Found existing record for {Hostname} with ID {RecordId}. Deleting.", recordHostname, recordId);
                                string delUrl = $"https://api.netlify.com/api/v1/dns_zones/{apiZoneName}/dns_records/{recordId}?access_token={_accessToken}";
                                try
                                {
                                    var deleteResponse = await _httpClient.DeleteAsync(delUrl);
                                    _logger.LogInformation("DELETE request for record {RecordId} returned status {StatusCode}", recordId, deleteResponse.StatusCode);
                                    if (!deleteResponse.IsSuccessStatusCode)
                                    {
                                        _logger.LogWarning("Failed to delete record {RecordId}. Status: {StatusCode}", recordId, deleteResponse.StatusCode);
                                    }
                                }
                                catch (HttpRequestException ex)
                                {
                                    _logger.LogError(ex, "Error deleting DNS record {RecordId}", recordId);
                                }
                            }
                        }

                        // Add new DNS records
                        foreach (string domain in dnsZone.Domains!)
                        {
                            _logger.LogInformation("Creating new 'A' record for {Domain} with value {IP}", domain, s.New.State);
                            var payload = new
                            {
                                type = "A",
                                hostname = domain,
                                value = s.New.State
                            };
                            string jsonPayload = JsonSerializer.Serialize(payload);
                            _logger.LogDebug("Sending POST request to Netlify with payload: {Payload}", jsonPayload);
                            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                            try
                            {
                                var postResponse = await _httpClient.PostAsync(getPostUrl, content);
                                var postResponseBody = await postResponse.Content.ReadAsStringAsync();
                                _logger.LogInformation("POST request for domain {Domain} returned status {StatusCode}", domain, postResponse.StatusCode);
                                _logger.LogDebug("Netlify POST response body: {Body}", postResponseBody);
                                if (!postResponse.IsSuccessStatusCode)
                                {
                                    _logger.LogError("Failed to create DNS record for {Domain}. Status: {StatusCode}. Body: {Body}", domain, postResponse.StatusCode, postResponseBody);
                                }
                            }
                            catch (HttpRequestException ex)
                            {
                                _logger.LogError(ex, "Error creating DNS record for {Domain}", domain);
                                telegram.System("Error creating DNS record for {Domain}. Status: {StatusCode}.");
                            }
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex, "Error fetching DNS records for zone {ZoneName}", dnsZone.DnsZoneName);
                    }
                }
            }
            else
            {
                if (s.New?.State is null || s.New?.State == "unknown")
                {
                    _logger.LogDebug("Skipping DNS update because new IP state is {State}.", s.New?.State);
                    if (s.New?.State == "unknown")
                    {
                        entities.Myip.CallService("homeassistant.reload_config_entry");
                    }
                }
                if (config.Value.DnsZones is null) _logger.LogWarning("Skipping DNS update because DnsZones configuration is missing.");
            }
        });
    }
}

public class NetlifyDNSConfig
{
    public List<DnsZone>? DnsZones { get; set; }
}

public class DnsZone
{
    public string? DnsZoneName { get; set; }
    public List<string>? Domains { get; set; }
}