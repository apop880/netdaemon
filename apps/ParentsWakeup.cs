using System;
using System.Reactive.Linq;

namespace HomeAssistantApps;

[NetDaemonApp]
public class ParentsWakeup
{
    private IDisposable? _julieTimer;
    private IDisposable? _alexTimer;
    private IDisposable? _failSafeTimer;

    public ParentsWakeup(ILogger<ParentsWakeup> logger, IScheduler scheduler, Entities entities)
    {
        InitializeJulieWakeup(logger, scheduler, entities);
        InitializeAlexWakeup(logger, scheduler, entities);
        InitializeFailSafe(logger, scheduler, entities);
    }

    private void InitializeJulieWakeup(ILogger<ParentsWakeup> logger, IScheduler scheduler, Entities entities)
    {
        var alarmSensor = entities.Sensor.Pixel8ProNextAlarm;
        var bedtime = entities.InputBoolean.JulieBed;
        var lamp = entities.Light.JulieLamp;

        alarmSensor.StateChanges()
            .Where(s => !string.IsNullOrWhiteSpace(s.New?.State) && s.New.State != "unavailable" && s.New.State != "unknown")
            .Subscribe(s =>
            {
                if (DateTime.TryParse(s.New?.State, out var alarmTime))
                {
                    logger.LogInformation("Julie's alarm set for {AlarmTime}", alarmTime);
                    ScheduleJulieWakeup(logger, scheduler, alarmTime, bedtime, lamp);
                }
            });

        // Initial check
        if (!string.IsNullOrWhiteSpace(alarmSensor.State) && 
            alarmSensor.State != "unavailable" && 
            alarmSensor.State != "unknown" &&
            DateTime.TryParse(alarmSensor.State, out var currentAlarmTime))
        {
            ScheduleJulieWakeup(logger, scheduler, currentAlarmTime, bedtime, lamp);
        }
    }

    private void InitializeAlexWakeup(ILogger<ParentsWakeup> logger, IScheduler scheduler, Entities entities)
    {
        var alarmSensor = entities.Sensor.Cph2655NextAlarm;

        alarmSensor.StateChanges()
            .Where(s => !string.IsNullOrWhiteSpace(s.New?.State) && s.New.State != "unavailable" && s.New.State != "unknown")
            .Subscribe(s =>
            {
                if (DateTime.TryParse(s.New?.State, out var alarmTime))
                {
                    logger.LogInformation("Alex's alarm set for {AlarmTime}", alarmTime);
                    ScheduleAlexWakeup(logger, scheduler, alarmTime, entities);
                }
            });

        // Initial check
        if (!string.IsNullOrWhiteSpace(alarmSensor.State) && 
            alarmSensor.State != "unavailable" && 
            alarmSensor.State != "unknown" &&
            DateTime.TryParse(alarmSensor.State, out var currentAlarmTime))
        {
            ScheduleAlexWakeup(logger, scheduler, currentAlarmTime, entities);
        }
    }

    private void InitializeFailSafe(ILogger<ParentsWakeup> logger, IScheduler scheduler, Entities entities)
    {
        var now = scheduler.Now.DateTime;
        var nextTenAm = now.Date.AddHours(10);
        if (nextTenAm <= now)
        {
            nextTenAm = nextTenAm.AddDays(1);
        }

        var initialDelay = nextTenAm - now;

        _failSafeTimer = Observable.Timer(initialDelay, TimeSpan.FromDays(1), scheduler)
            .Subscribe(_ => CheckAndResetBeds(logger, entities));
    }

    private void CheckAndResetBeds(ILogger logger, Entities entities)
    {
        var beds = new[] { entities.InputBoolean.JulieBed, entities.InputBoolean.AlexBed, entities.InputBoolean.AlexBasement };
        foreach (var bed in beds)
        {
            if (bed.State == "on")
            {
                logger.LogInformation("Fail-safe: Turning off {Bed} at 10:00 AM", bed.EntityId);
                bed.TurnOff();
            }
        }
    }

    private void ScheduleJulieWakeup(ILogger logger, IScheduler scheduler, DateTime alarmTime, InputBooleanEntity bedtime, LightEntity lamp)
    {
        _julieTimer?.Dispose();

        var now = scheduler.Now.DateTime;
        if (alarmTime <= now) return;

        _julieTimer = scheduler.Schedule(alarmTime, () =>
        {
            logger.LogInformation("Executing Julie's wakeup routine");
            
            // Condition: Before 9:00 AM
            if (scheduler.Now.LocalDateTime.Hour >= 9)
            {
                logger.LogInformation("Skipping Julie's wakeup: It is past 9:00 AM");
                return;
            }

            // Condition: Bedtime is on
            if (bedtime.State != "on")
            {
                logger.LogInformation("Skipping Julie's wakeup: Bedtime is not on");
                return;
            }

            logger.LogInformation("Turning on Julie's lamp and turning off bedtime mode");
            lamp.TurnOn();
            bedtime.TurnOff();
        });
    }

    private void ScheduleAlexWakeup(ILogger logger, IScheduler scheduler, DateTime alarmTime, Entities entities)
    {
        _alexTimer?.Dispose();

        var now = scheduler.Now.DateTime;
        if (alarmTime <= now) return;

        _alexTimer = scheduler.Schedule(alarmTime, () =>
        {
            logger.LogInformation("Executing Alex's wakeup routine");

            // Condition: Before 9:00 AM
            if (scheduler.Now.LocalDateTime.Hour >= 9)
            {
                logger.LogInformation("Skipping Alex's wakeup: It is past 9:00 AM");
                return;
            }

            if (entities.InputBoolean.AlexBed.State == "on")
            {
                logger.LogInformation("Turning on Alex's lamp and turning off bedtime mode");
                entities.Light.AlexLamp.TurnOn();
                entities.InputBoolean.AlexBed.TurnOff();
            }

            if (entities.InputBoolean.AlexBasement.State == "on")
            {
                logger.LogInformation("Turning on office bulbs and turning off basement bedtime mode");
                entities.Light.OfficeBulbs.TurnOn();
                entities.InputBoolean.AlexBasement.TurnOff();
            }
        });
    }
}
