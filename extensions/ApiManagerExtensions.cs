using System.Threading;
using System.Threading.Tasks;
using NetDaemon.Client;

namespace HomeAssistantApps.Extensions;

public record DeviceResponse
{
    public string DeviceId { get; set; } = string.Empty;
}

public static class ApiManagerExtensions
{
    public static async Task<string> GetDeviceId(this IHomeAssistantApiManager apiManager, Entity entity, CancellationToken cancellationToken = default)
    {
        var apiUrl = "template";
        var template = $"{{\"DeviceId\": \"{{{{ device_id('{entity.EntityId}') }}}}\"}}";
        var data = new { template };
        var response = await apiManager.PostApiCallAsync<DeviceResponse>(apiUrl, cancellationToken, data);
        return response?.DeviceId ?? string.Empty;
    }
}