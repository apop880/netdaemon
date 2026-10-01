using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeAssistantApps.modules;

public class Telegram(IServiceProvider serviceProvider, ILogger<Telegram> logger, IOptions<TelegramSettings> telegramSettings)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly TelegramSettings _settings = telegramSettings.Value;

    public void Alex(string message)
    {
        SendMessage(message: message, chatIds: [_settings.Alex]);
    }

    public void Julie(string message)
    {
        SendMessage(message: message, chatIds: [_settings.Julie]);
    }

    public void System(string message)
    {
        SendMessage(message: message, chatIds: [_settings.System]);
    }

    public void All(string message)
    {
        SendMessage(message: message, chatIds: [_settings.Julie, _settings.Alex]);
    }

    private async void SendMessage(string message, IEnumerable<string> chatIds)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var haContext = scope.ServiceProvider.GetRequiredService<IHaContext>();
            var telegram = new Services(haContext).TelegramBot;
            await telegram.SendMessageAsync(message: message, additionalFields: new { chat_id = chatIds });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram message to {Target}", chatIds);
        }
    }
}

public class TelegramSettings
{
    public required string Julie { get; set; }
    public required string Alex { get; set; }
    public required string System { get; set; }
}
