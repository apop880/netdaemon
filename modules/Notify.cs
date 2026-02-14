using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;

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

public class Notify(IHaContext haContext)
{
    private readonly Services _services = new(haContext);

    public void Alex(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, [CallerFilePath] string? callerPath = null)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        var data = BuildDataObject(actualTag, actions);
        if (data == null)
            _services.Notify.MobileAppCph2655(message, title);
        else
            _services.Notify.MobileAppCph2655(message, title, data: data);
    }

    public void Julie(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, [CallerFilePath] string? callerPath = null)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        var data = BuildDataObject(actualTag, actions);
        if (data == null)
            _services.Notify.MobileAppPixel8Pro(message, title);
        else
            _services.Notify.MobileAppPixel8Pro(message, title, data: data);
    }

    public void All(string message, string? title = null, string? tag = null,
        NotifyAction[]? actions = null, [CallerFilePath] string? callerPath = null)
    {
        var actualTag = tag ?? Path.GetFileNameWithoutExtension(callerPath);
        var data = BuildDataObject(actualTag, actions);
        if (data == null)
            _services.Notify.All(message, title);
        else
            _services.Notify.All(message, title, data: data);
    }

    private static object? BuildDataObject(string? tag, NotifyAction[]? actions)
    {
        if (string.IsNullOrEmpty(tag) && (actions == null || actions.Length == 0))
            return null;

        if (actions != null && actions.Length > 0)
        {
            var actionsData = actions.Select(a => new
            {
                action = a.Action,
                title = a.Title,
                uri = a.Uri,
                destructive = a.Destructive ? "true" : null,
                authenticationRequired = a.AuthenticationRequired ? "true" : null,
                textInputButtonTitle = a.TextInputButtonTitle,
                textInputPlaceholder = a.TextInputPlaceholder
            }).ToArray();

            if (string.IsNullOrEmpty(tag))
                return new { actions = actionsData };
            return new { tag, actions = actionsData };
        }

        return new { tag };
    }
}
