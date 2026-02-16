namespace HomeAssistantApps;

[NetDaemonApp]
public class PresenceLighting
{
    public PresenceLighting(ILogger<PresenceLighting> logger, Entities entities, HomeMode homeMode)
    {
        homeMode.Current
            .Where(mode => mode is HomeModeState.Away or HomeModeState.Vacation)
            .Subscribe(_ =>
            {
                logger.LogInformation("HomeMode changed to Away, turning off InsideLights");
                entities.Light.InsideLights.TurnOff();
            });
        homeMode.Current
            .Where(mode => mode == HomeModeState.Home)
            .Subscribe(_ =>
            {
                if (entities.Light.KitchenSink.IsOn())
                {
                    entities.Light.KitchenSink.TurnOn(brightnessPct: 80);
                }
            });
        entities.Sensor.ThirdRealityInc3rsnl02043zIlluminance.StateChanges()
            .Where(s => s.New?.State < 10)
            .Subscribe(_ =>
            {
                if (homeMode.CurrentValue == HomeModeState.Away)
                {
                    entities.Light.KitchenSink.TurnOn(brightnessPct: 40);
                }
            });
    }
}