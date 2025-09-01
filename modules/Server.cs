using System.Threading.Tasks;

namespace HomeAssistantApps.modules;

public class Server(Unifi unifi, Proxmox proxmox, ILogger<Server> logger)
{
    private readonly Unifi _unifi = unifi;
    private readonly Proxmox _proxmox = proxmox;
    private readonly ILogger<Server> _logger = logger;

    public async Task Shutdown()
    {
        _logger.LogInformation("Server shutdown sequence initiated.");
        await _unifi.UpdateDNS("8.8.8.8");
        await _proxmox.Shutdown();
    }
}