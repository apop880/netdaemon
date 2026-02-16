using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps;

[NetDaemonApp]
public class PresenceLighting
{
    public PresenceLighting(ILogger<PresenceLighting> logger, IScheduler scheduler, Entities entities, HomeMode homeMode)
    {
        scheduler.ScheduleCron("0 12 * * *", () =>
        {
            entities.InputBoolean.EveningLightingTriggered.TurnOff();
        });

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
        entities.Sensor.ThirdRealityInc3rsnl02043zIlluminance.StateChanges()
            .Where(s => s.New?.State < 15)
            .Subscribe(_ =>
            {
                if (homeMode.CurrentValue == HomeModeState.Home &&
                    !entities.InputBoolean.EveningLightingTriggered.IsOn())
                {
                    entities.Light.KitchenTable.TurnOn();
                    entities.Light.LivingCanLights.TurnOn();
                    entities.InputBoolean.EveningLightingTriggered.TurnOn();
                    logger.LogInformation("Evening lighting triggered due to low illuminance");
                }
            });
    }
}