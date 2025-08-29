using System.Collections.Generic;
using System.Net.Http;

namespace HomeAssistantApps;

public class ProxmoxConfig
{
    public string? Token { get; set; }
    public string? BaseUrl { get; set; }
    public string? Node { get; set; }
}

[NetDaemonApp]
public class Ups
{
    private const double LowBatteryWarningThreshold = 80.0;
    private const double CriticalBatteryShutdownThreshold = 60.0;
    private const double FullyChargedThreshold = 100.0;

    public Ups(ILogger<Ups> logger, Entities entities, Telegram telegram, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        var shutdownSwitch = entities.InputBoolean.ShutdownOnUps;
        var upsCharge = entities.Sensor.ServerUpsBatteryCharge;
        var upsStatus = entities.Sensor.ServerUpsStatus;
        var _isProxmoxConfigValid = true;
        var proxmoxConfig = configuration.GetSection("Proxmox").Get<ProxmoxConfig>();

        if (proxmoxConfig is null)
        {
            logger.LogError("Proxmox configuration not found.");
            telegram.System("Error: Proxmox configuration not found. UPS functionality disabled.");
            _isProxmoxConfigValid = false;
        }
        else
        {
            if (string.IsNullOrEmpty(proxmoxConfig.Token))
            {
                logger.LogError("Proxmox access token not found in configuration.");
                telegram.System("Error: Proxmox access token not found. UPS functionality disabled.");
                _isProxmoxConfigValid = false;
            }
            if (string.IsNullOrEmpty(proxmoxConfig.BaseUrl))
            {
                logger.LogError("Proxmox base URL not found in configuration.");
                telegram.System("Error: Proxmox base URL not found. UPS functionality disabled.");
                _isProxmoxConfigValid = false;
            }
            if (string.IsNullOrEmpty(proxmoxConfig.Node))
            {
                logger.LogError("Proxmox node not found in configuration.");
                telegram.System("Error: Proxmox node not found. UPS functionality disabled.");
                _isProxmoxConfigValid = false;
            }
        }

        if (!_isProxmoxConfigValid)
        {
            return; // Terminate the constructor if configuration is invalid
        }

        upsStatus.StateChanges()
            .Where(s => s.New?.State != s.Old?.State && s.New?.State == "On Battery")
            .Subscribe(_ =>
            {
                // Power lost (discharging from 100%)
                telegram.All("Power has been lost to UPS. Currently discharging.");
            });

        upsCharge.StateChanges()
            .Where(s => s.New?.State is not null && s.Old?.State is not null)
            .SubscribeAsync(async s =>
            {
                var newState = s.New!.State!.Value;
                var oldState = s.Old!.State!.Value;

                // Crossed below 80%
                if (newState < LowBatteryWarningThreshold && oldState >= LowBatteryWarningThreshold)
                {
                    var shutdownStatus = shutdownSwitch.IsOn() ? "on" : "off";
                    telegram.All($"UPS battery has dropped below {LowBatteryWarningThreshold}%. Home server will initiate shutdown at {CriticalBatteryShutdownThreshold}% if shutdown switch is on. Shutdown switch is currently {shutdownStatus}.");
                }

                // Crossed below 60%
                if (newState < CriticalBatteryShutdownThreshold && oldState >= CriticalBatteryShutdownThreshold)
                {
                    if (shutdownSwitch.IsOn())
                    {
                        telegram.All($"UPS battery has dropped below {CriticalBatteryShutdownThreshold}%. Initiating shutdown process.");
                        try
                        {
                            logger.LogInformation("Attempting to send shutdown request.");
                            var httpClient = httpClientFactory.CreateClient("IgnoreSslClient");
                            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={proxmoxConfig!.Token}");
                            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
                            var content = new FormUrlEncodedContent(
                            [
                                new KeyValuePair<string, string>("command", "shutdown")
                            ]);
                            var response = await httpClient.PostAsync($"{proxmoxConfig.BaseUrl}/api2/json/nodes/{proxmoxConfig.Node}/status", content);
                            response.EnsureSuccessStatusCode();
                            logger.LogInformation("Shutdown request sent successfully.");
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to send shutdown request.");
                            telegram.Alex("Error: Failed to send shutdown request to the server.");
                        }
                    }
                    else
                    {
                        telegram.All($"UPS battery has dropped below {CriticalBatteryShutdownThreshold}%. Shutdown switch is off.");
                    }
                }

                // Reached 100%
                if (newState == FullyChargedThreshold && oldState < FullyChargedThreshold)
                {
                    telegram.All("UPS is fully charged.");
                    shutdownSwitch.TurnOn();
                }
            });
    }
}