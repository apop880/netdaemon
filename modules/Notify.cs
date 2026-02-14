using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace HomeAssistantApps.modules;

public record NotifyAction(
    string Action,
    string Title,
    string? Uri = null,
    bool Destructive = false,
    bool AuthenticationRequired = false,
    string? TextInputButtonTitle = null,
    string? TextInputPlaceholder = null
);

public class Notify(IServiceProvider serviceProvider, ILogger<Notify> logger)
{
    public void Alex(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, bool noAction = true, [CallerFilePath] string? callerPath = null)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        _ = SendAsync("mobile_app_cph2655", message, title, actualTag, actions, noAction);
    }

    public void Julie(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, bool noAction = true, [CallerFilePath] string? callerPath = null)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        _ = SendAsync("mobile_app_pixel_8_pro", message, title, actualTag, actions, noAction);
    }

    public void All(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, bool noAction = true, [CallerFilePath] string? callerPath = null)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        _ = SendAsync("all", message, title, actualTag, actions, noAction);
    }

    private async Task SendAsync(string target, string message, string? title, string? tag, NotifyAction[]? actions, bool noAction)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var services = new Services(scope.ServiceProvider.GetRequiredService<IHaContext>());
            var data = BuildDataObject(tag, actions, noAction);
            
            switch (target)
            {
                case "mobile_app_cph2655":
                    if (data == null)
                        services.Notify.MobileAppCph2655(message, title);
                    else
                        services.Notify.MobileAppCph2655(message, title, data: data);
                    break;
                case "mobile_app_pixel_8_pro":
                    if (data == null)
                        services.Notify.MobileAppPixel8Pro(message, title);
                    else
                        services.Notify.MobileAppPixel8Pro(message, title, data: data);
                    break;
                default:
                    if (data == null)
                        services.Notify.All(message, title);
                    else
                        services.Notify.All(message, title, data: data);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send notification to {Target}", target);
        }
    }

    private static object? BuildDataObject(string? tag, NotifyAction[]? actions, bool noAction)
    {
        var hasTag = !string.IsNullOrEmpty(tag);
        var hasActions = actions != null && actions.Length > 0;

        if (!hasTag && !hasActions && !noAction)
            return null;

        if (hasActions)
        {
            var actionsData = actions!.Select(a => new
            {
                action = a.Action,
                title = a.Title,
                uri = a.Uri,
                destructive = a.Destructive ? "true" : null,
                authenticationRequired = a.AuthenticationRequired ? "true" : null,
                textInputButtonTitle = a.TextInputButtonTitle,
                textInputPlaceholder = a.TextInputPlaceholder
            }).ToArray();

            if (!hasTag && noAction)
                return new { clickAction = "noAction", actions = actionsData };
            if (!hasTag)
                return new { actions = actionsData };
            if (noAction)
                return new { tag, clickAction = "noAction", actions = actionsData };
            return new { tag, actions = actionsData };
        }

        if (noAction && hasTag)
            return new { tag, clickAction = "noAction" };
        if (noAction)
            return new { clickAction = "noAction" };
        if (hasTag)
            return new { tag };
        return null;
    }
}
