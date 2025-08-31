using System.Threading.Tasks;

namespace HomeAssistantApps;

[NetDaemonApp]
public class Server
{
    private readonly Unifi _unifi;
    private readonly Proxmox _proxmox;
    private readonly ILogger<Server> _logger;

    public Server(Unifi unifi, Proxmox proxmox, ILogger<Server> logger)
    {
        _unifi = unifi;
        _proxmox = proxmox;
        _logger = logger;
    }

    public async Task Shutdown()
    {
        _logger.LogInformation("Server shutdown sequence initiated.");
        await _unifi.UpdateDNS("8.8.8.8");
        await _proxmox.Shutdown();
    }
}