using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json.Serialization;
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
        => Send("mobile_app_cph2655", message, title, tag, actions, noAction, callerPath);

    public void Julie(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, bool noAction = true, [CallerFilePath] string? callerPath = null)
        => Send("mobile_app_pixel_8_pro", message, title, tag, actions, noAction, callerPath);

    public void All(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, bool noAction = true, [CallerFilePath] string? callerPath = null)
        => Send("all", message, title, tag, actions, noAction, callerPath);

    private void Send(string target, string message, string? title, string? tag,
        NotifyAction[]? actions, bool noAction, string? callerPath)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        _ = SendAsync(target, message, title, actualTag, actions, noAction);
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

    public static IDisposable OnAction(IHaContext ha, string actionId, Action callback)
    {
        return ha.Events.Filter<MobileAppNotificationActionData>("mobile_app_notification_action")
            .Where(e => e.Data?.Action == actionId)
            .Subscribe(_ => callback());
    }

    private static Dictionary<string, object>? BuildDataObject(string? tag, NotifyAction[]? actions, bool noAction)
    {
        var dict = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(tag))
            dict["tag"] = tag;

        if (noAction)
            dict["clickAction"] = "noAction";

        if (actions is { Length: > 0 })
        {
            dict["actions"] = actions.Select(a => new
            {
                action = a.Action,
                title = a.Title,
                uri = a.Uri,
                destructive = a.Destructive ? "true" : null,
                authenticationRequired = a.AuthenticationRequired ? "true" : null,
                textInputButtonTitle = a.TextInputButtonTitle,
                textInputPlaceholder = a.TextInputPlaceholder
            }).ToArray();
        }

        return dict.Count > 0 ? dict : null;
    }
}

public record MobileAppNotificationActionData
{
    [JsonPropertyName("action")] public string? Action { get; init; }
}
