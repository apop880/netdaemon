using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeAssistantApps.modules;

public class Telegram(IServiceProvider serviceProvider, ILogger<Telegram> logger, IOptions<TelegramSettings> telegramSettings)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly TelegramSettings _settings = telegramSettings.Value;

    public void Alex(string message)
    {
        SendMessage(message: message, target: _settings.Alex);
    }

    public void Julie(string message)
    {
        SendMessage(message: message, target: _settings.Julie);
    }

    public void System(string message)
    {
        SendMessage(message: message, target: _settings.System);
    }

    public void All(string message)
    {
        SendMessage(message: message, target: new[] { _settings.Julie, _settings.Alex });
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

public class TelegramSettings
{
    public int Julie { get; set; }
    public int Alex { get; set; }
    public int System { get; set; }
}
