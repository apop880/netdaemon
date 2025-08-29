using Microsoft.Extensions.DependencyInjection;

namespace HomeAssistantApps.modules;

public class Telegram(IServiceProvider serviceProvider, ILogger<Telegram> logger)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public void Alex(string message)
    {
        SendMessage(message: message, target: TelegramGlobals.Alex);
    }

    public void Julie(string message)
    {
        SendMessage(message: message, target: TelegramGlobals.Julie);
    }

    public void System(string message)
    {
        SendMessage(message: message, target: TelegramGlobals.System);
    }

    public void All(string message)
    {
        SendMessage(message: message, target: TelegramGlobals.All);
    }

    private async void SendMessage(string message, object? target)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var haContext = scope.ServiceProvider.GetRequiredService<IHaContext>();
            var telegram = new Services(haContext).TelegramBot;
            await telegram.SendMessageAsync(message: message, target: target);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram message to {Target}", target);
        }
    }
}