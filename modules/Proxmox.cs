using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace HomeAssistantApps.modules;

public class ProxmoxConfig
{
    public string? Token { get; set; }
    public string? BaseUrl { get; set; }
    public string? Node { get; set; }
}

public class Proxmox
{
    private readonly ILogger<Proxmox> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxmoxConfig? _proxmoxConfig;
    private readonly Notify _notify;
    private bool _isProxmoxConfigValid = true;

    public Proxmox(ILogger<Proxmox> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory, Notify notify)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _notify = notify;
        _proxmoxConfig = configuration.GetSection("Proxmox").Get<ProxmoxConfig>();

        if (_proxmoxConfig is null)
        {
            _logger.LogError("Proxmox configuration not found.");
            _notify.Alex("Error: Proxmox configuration not found. UPS functionality disabled.");
            _isProxmoxConfigValid = false;
        }
        else
        {
            if (string.IsNullOrEmpty(_proxmoxConfig.Token))
            {
                _logger.LogError("Proxmox access token not found in configuration.");
                _notify.Alex("Error: Proxmox access token not found. UPS functionality disabled.");
                _isProxmoxConfigValid = false;
            }
            if (string.IsNullOrEmpty(_proxmoxConfig.BaseUrl))
            {
                _logger.LogError("Proxmox base URL not found in configuration.");
                _notify.Alex("Error: Proxmox base URL not found. UPS functionality disabled.");
                _isProxmoxConfigValid = false;
            }
            if (string.IsNullOrEmpty(_proxmoxConfig.Node))
            {
                _logger.LogError("Proxmox node not found in configuration.");
                _notify.Alex("Error: Proxmox node not found. UPS functionality disabled.");
                _isProxmoxConfigValid = false;
            }
        }
    }

    public async Task Shutdown()
    {
        if (!_isProxmoxConfigValid)
        {
            _logger.LogError("Proxmox configuration is invalid. Cannot proceed with shutdown.");
            _notify.Alex("Error: Proxmox configuration is invalid. Cannot proceed with shutdown.");
            return;
        }

        try
        {
            _logger.LogInformation("Attempting to send shutdown request to Proxmox.");
            var httpClient = _httpClientFactory.CreateClient("IgnoreSslClient");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={_proxmoxConfig!.Token}");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("command", "shutdown")
            ]);
            var response = await httpClient.PostAsync($"{_proxmoxConfig.BaseUrl}/api2/json/nodes/{_proxmoxConfig.Node}/status", content);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Proxmox shutdown request sent successfully.");
            _notify.All("Proxmox shutdown request sent successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send shutdown request to Proxmox.");
            _notify.Alex("Error: Failed to send shutdown request to the Proxmox server.");
        }
    }
}
