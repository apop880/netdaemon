using System.Collections.Generic;
using System.Linq;
using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps;

[NetDaemonApp]
public class ParentsWakeup
{
    public ParentsWakeup(ILogger<ParentsWakeup> logger, IScheduler scheduler, Entities entities)
    {
        var config = new List<ParentsWakeupConfig>
        {
            new() {
                AlarmSensor = entities.Sensor.Pixel8ProNextAlarm,
                BooleanLightPairs = [(entities.InputBoolean.JulieBed, entities.Light.JulieLamp)]
            },
            new() {
                AlarmSensor = entities.Sensor.Cph2655NextAlarm,
                BooleanLightPairs =
                [
                    (entities.InputBoolean.AlexBed, entities.Light.AlexLamp),
                    (entities.InputBoolean.AlexBasement, entities.Light.GuestBedroomLights)
                ]
            }
        };
        foreach (var cfg in config)
        {
            cfg.AlarmSensor.StateChangesWithCurrent()
                .Where(s => !string.IsNullOrWhiteSpace(s.New?.State) && s.New.State != "unavailable" && s.New.State != "unknown")
                .Subscribe(s =>
                {
                    if (DateTime.TryParse(s.New?.State, out var alarmTime))
                    {
                        cfg.Timer?.Dispose();

                        cfg.Timer = scheduler.Schedule(alarmTime, () =>
                        {
                            logger.LogInformation("Executing {Name}'s wakeup routine", cfg.AlarmSensor.EntityId);

                            // Condition: Before 9:00 AM
                            if (scheduler.Now.LocalDateTime.Hour >= 9)
                            {
                                logger.LogInformation("Skipping {Name}'s wakeup: It is past 9:00 AM", cfg.AlarmSensor.EntityId);
                                return;
                            }                       

                            logger.LogInformation("Turning on {Name}'s lamp and turning off bedtime mode", cfg.AlarmSensor.EntityId);
                            foreach (var (bed, light) in cfg.BooleanLightPairs.Where(pair => pair.Item1.State == "on"))
                            {
                                light?.TurnOn();
                                bed.TurnOff();
                            }
                            entities.InputBoolean.NightMode.TurnOff();
                        });
                    }
                });
            
            scheduler.ScheduleCron("0 10 * * *", () =>
            {
                foreach (var (bed, _) in cfg.BooleanLightPairs.Where(pair => pair.Item1.State == "on"))
                {
                    logger.LogInformation("Fail-safe: Turning off {Bed} at 10:00 AM", bed.EntityId);
                    bed.TurnOff();
                }
                entities.InputBoolean.NightMode.TurnOff();
            });
        }
    }
}

public class ParentsWakeupConfig
{
    public required SensorEntity AlarmSensor { get; set; }
    public required List<(InputBooleanEntity, LightEntity?)> BooleanLightPairs { get; set; }
    public IDisposable? Timer { get; set; }
}