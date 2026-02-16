using System.Linq;
using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps;

[NetDaemonApp]
public class NightThermostat
{
    private const int FallbackRecoveryHour = 8;
    private const double LeadTimeBaseMinutes = 15;
    private const double LeadTimePerDegree = 1.5;
    private const double LeadTimeMinMinutes = 15;
    private const double LeadTimeMaxMinutes = 90;

    private readonly ILogger<NightThermostat> _logger;
    private readonly IScheduler _scheduler;
    private readonly Entities _entities;
    private readonly ClimateEntity _thermostat;
    private readonly SensorEntity _julieAlarm;
    private readonly SensorEntity _alexAlarm;

    private IDisposable? _recoveryTimer;

    public NightThermostat(ILogger<NightThermostat> logger, IScheduler scheduler, Entities entities)
    {
        _logger = logger;
        _scheduler = scheduler;
        _entities = entities;
        _thermostat = entities.Climate.Thermostat73Ca44;
        _julieAlarm = entities.Sensor.Pixel8ProNextAlarm;
        _alexAlarm = entities.Sensor.Cph2655NextAlarm;

        // Turn on NightMode at midnight (unless vacation mode is on)
        scheduler.ScheduleCron("0 0 * * *", () =>
        {
            if (_entities.InputBoolean.VacationMode.IsOn())
            {
                _logger.LogInformation("NightMode: Skipping midnight activation — vacation mode is on");
                return;
            }

            _logger.LogInformation("NightMode: Activating night mode at midnight");
            _entities.InputBoolean.NightMode.TurnOn();
        });

        // When NightMode turns on, apply setback and schedule recovery
        _entities.InputBoolean.NightMode
            .StateChanges()
            .Where(s => s.New?.State == "on")
            .Subscribe(_ =>
            {
                if (_entities.InputBoolean.VacationMode.IsOn())
                {
                    _logger.LogInformation("NightMode: Skipping setback — vacation mode is on");
                    return;
                }

                ApplyNightSetback();
            });

        // When NightMode turns off, cancel any pending recovery
        _entities.InputBoolean.NightMode
            .StateChanges()
            .Where(s => s.New?.State == "off")
            .Subscribe(_ =>
            {
                _logger.LogInformation("NightMode: Night mode turned off — cancelling recovery timer");
                _recoveryTimer?.Dispose();
                _recoveryTimer = null;
            });

        // Re-evaluate recovery when either alarm changes
        _julieAlarm.StateChanges()
            .Merge(_alexAlarm.StateChanges())
            .Subscribe(_ =>
            {
                if (_entities.InputBoolean.NightMode.IsOff() || _entities.InputBoolean.VacationMode.IsOn())
                    return;

                _logger.LogInformation("NightMode: Alarm time changed — rescheduling recovery");
                ScheduleRecovery();
            });

        _logger.LogInformation("NightMode: Service is ready");
    }

    private void ApplyNightSetback()
    {
        var hvacMode = _thermostat.State;

        if (hvacMode == "heat")
        {
            _logger.LogInformation("NightMode: Heating mode — setting temperature to {Temp}°F", ThermostatConstants.MinHeatTemp);
            _thermostat.SetTemperature(temperature: ThermostatConstants.MinHeatTemp);
        }
        else if (hvacMode == "cool")
        {
            _logger.LogInformation("NightMode: Cooling mode — setting temperature to {Temp}°F", ThermostatConstants.MaxCoolTemp);
            _thermostat.SetTemperature(temperature: ThermostatConstants.MaxCoolTemp);
        }
        else
        {
            _logger.LogInformation("NightMode: Thermostat is in '{Mode}' mode — skipping setback", hvacMode);
            return;
        }

        ScheduleRecovery();
    }

    private void ScheduleRecovery()
    {
        _recoveryTimer?.Dispose();
        _recoveryTimer = null;

        var hvacMode = _thermostat.State;
        double targetTemp;

        if (hvacMode == "heat")
            targetTemp = ThermostatConstants.DefaultHeatTemp;
        else if (hvacMode == "cool")
            targetTemp = ThermostatConstants.DefaultCoolTemp;
        else
        {
            _logger.LogInformation("NightMode: Thermostat is in '{Mode}' mode — skipping recovery scheduling", hvacMode);
            return;
        }

        var alarmTime = GetEarliestAlarmTime();
        var leadTimeMinutes = CalculateLeadTimeMinutes(targetTemp);
        var recoveryTime = alarmTime.AddMinutes(-leadTimeMinutes);
        var now = _scheduler.Now.LocalDateTime;

        _logger.LogInformation(
            "NightMode: Target alarm time is {Alarm:HH:mm}, lead time is {Lead:F0} min, recovery at {Recovery:HH:mm}",
            alarmTime, leadTimeMinutes, recoveryTime);

        if (recoveryTime <= now)
        {
            _logger.LogInformation("NightMode: Recovery time is in the past — recovering immediately");
            RecoverTemperature(hvacMode, targetTemp);
            return;
        }

        var delay = recoveryTime - now;
        _recoveryTimer = _scheduler.Schedule(TimeSpan.FromMinutes(delay.TotalMinutes), () =>
        {
            if (_entities.InputBoolean.NightMode.IsOff())
            {
                _logger.LogInformation("NightMode: Night mode already off at recovery time — skipping");
                return;
            }

            RecoverTemperature(hvacMode, targetTemp);
        });
    }

    private void RecoverTemperature(string? hvacMode, double targetTemp)
    {
        _logger.LogInformation("NightMode: Recovering temperature to {Temp}°F (mode: {Mode})", targetTemp, hvacMode);
        _thermostat.SetTemperature(temperature: targetTemp);
    }

    private DateTime GetEarliestAlarmTime()
    {
        var now = _scheduler.Now.LocalDateTime;
        var tomorrow9am = now.Date.AddDays(1).AddHours(9);

        DateTime? julieAlarm = null;
        DateTime? alexAlarm = null;

        if (DateTime.TryParse(_julieAlarm.State, out var jTime) && jTime > now && jTime < tomorrow9am)
            julieAlarm = jTime;

        if (DateTime.TryParse(_alexAlarm.State, out var aTime) && aTime > now && aTime < tomorrow9am)
            alexAlarm = aTime;

        if (julieAlarm.HasValue && alexAlarm.HasValue)
        {
            var earliest = julieAlarm.Value < alexAlarm.Value ? julieAlarm.Value : alexAlarm.Value;
            _logger.LogInformation("NightMode: Using earliest alarm at {Time:HH:mm}", earliest);
            return earliest;
        }

        if (julieAlarm.HasValue)
        {
            _logger.LogInformation("NightMode: Using Julie's alarm at {Time:HH:mm}", julieAlarm.Value);
            return julieAlarm.Value;
        }

        if (alexAlarm.HasValue)
        {
            _logger.LogInformation("NightMode: Using Alex's alarm at {Time:HH:mm}", alexAlarm.Value);
            return alexAlarm.Value;
        }

        var fallback = now.Date.AddHours(FallbackRecoveryHour);
        if (fallback <= now)
            fallback = fallback.AddDays(1);

        _logger.LogInformation("NightMode: No valid alarms found — using fallback recovery at {Time:HH:mm}", fallback);
        return fallback;
    }

    private double CalculateLeadTimeMinutes(double targetTemp)
    {
        var outsideTemp = _entities.Weather.Pirateweather.Attributes?.Temperature;

        if (outsideTemp is null)
        {
            _logger.LogWarning("NightMode: Outside temperature unavailable — using base lead time of {Time} min", LeadTimeBaseMinutes);
            return LeadTimeBaseMinutes;
        }

        var delta = Math.Abs(outsideTemp.Value - targetTemp);
        var leadTime = LeadTimeBaseMinutes + (LeadTimePerDegree * delta);
        leadTime = Math.Clamp(leadTime, LeadTimeMinMinutes, LeadTimeMaxMinutes);

        _logger.LogInformation(
            "NightMode: Outside temp is {Outside}°F, target is {Target}°F, delta is {Delta:F1}°, lead time is {Lead:F0} min",
            outsideTemp.Value, targetTemp, delta, leadTime);

        return leadTime;
    }
}
