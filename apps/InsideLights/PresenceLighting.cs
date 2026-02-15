namespace HomeAssistantApps;

[NetDaemonApp]
public class PresenceLighting
{
    public PresenceLighting(ILogger<PresenceLighting> logger, Entities entities, HomeMode homeMode)
    {
        homeMode.Current
            .Where(mode => mode == HomeModeState.Away)
            .Subscribe(_ =>
            {
                logger.LogInformation("HomeMode changed to Away, turning off InsideLights");
                entities.Light.InsideLights.TurnOff();
            });
    }
}