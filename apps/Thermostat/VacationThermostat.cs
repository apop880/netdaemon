namespace HomeAssistantApps;

[NetDaemonApp]
public class VacationThermostat
{
    private const double PreArrivalDistanceMiles = 75;

    private readonly ILogger<VacationThermostat> _logger;
    private readonly HomeMode _homeMode;
    private readonly ClimateEntity _thermostat;
    private readonly NumericSensorEntity _nearestDistance;

    private bool _preArrivalResetDone;

    public VacationThermostat(ILogger<VacationThermostat> logger, Entities entities, HomeMode homeMode)
    {
        _logger = logger;
        _homeMode = homeMode;
        _thermostat = entities.Climate.Thermostat73Ca44;
        _nearestDistance = entities.Sensor.HomeNearestDistance;

        // React to home mode changes
        homeMode.Current
            .Subscribe(mode =>
            {
                if (mode == HomeModeState.Vacation)
                {
                    _preArrivalResetDone = false;
                    ApplyVacationSetpoint();
                }
                else
                {
                    RestoreDefaultSetpoint($"home mode changed to {mode}");
                }
            });

        // Pre-arrival: restore defaults when someone is approaching home
        _nearestDistance
            .StateChanges()
            .Where(_ => _homeMode.CurrentValue == HomeModeState.Vacation && !_preArrivalResetDone)
            .Where(s => s.New?.State is not null && s.New.State < PreArrivalDistanceMiles)
            .Subscribe(s =>
            {
                _preArrivalResetDone = true;
                _logger.LogInformation(
                    "VacationThermostat: Nearest person is {Distance:F0} miles away (< {Threshold} miles) — restoring default setpoints for arrival",
                    s.New!.State, PreArrivalDistanceMiles);
                RestoreDefaultSetpoint("pre-arrival proximity detected");
            });

        _logger.LogInformation("VacationThermostat: Service is ready");
    }

    private void ApplyVacationSetpoint()
    {
        var hvacMode = _thermostat.State;

        if (hvacMode == "heat")
        {
            _logger.LogInformation("VacationThermostat: Heating mode — setting temperature to {Temp}°F", ThermostatConstants.VacationHeatTemp);
            _thermostat.SetTemperature(temperature: ThermostatConstants.VacationHeatTemp);
        }
        else if (hvacMode == "cool")
        {
            _logger.LogInformation("VacationThermostat: Cooling mode — setting temperature to {Temp}°F", ThermostatConstants.VacationCoolTemp);
            _thermostat.SetTemperature(temperature: ThermostatConstants.VacationCoolTemp);
        }
        else
        {
            _logger.LogInformation("VacationThermostat: Thermostat is in '{Mode}' mode — skipping vacation setpoint", hvacMode);
        }
    }

    private void RestoreDefaultSetpoint(string reason)
    {
        var hvacMode = _thermostat.State;

        if (hvacMode == "heat")
        {
            _logger.LogInformation("VacationThermostat: Restoring heat to {Temp}°F — {Reason}", ThermostatConstants.DefaultHeatTemp, reason);
            _thermostat.SetTemperature(temperature: ThermostatConstants.DefaultHeatTemp);
        }
        else if (hvacMode == "cool")
        {
            _logger.LogInformation("VacationThermostat: Restoring cool to {Temp}°F — {Reason}", ThermostatConstants.DefaultCoolTemp, reason);
            _thermostat.SetTemperature(temperature: ThermostatConstants.DefaultCoolTemp);
        }
        else
        {
            _logger.LogInformation("VacationThermostat: Thermostat is in '{Mode}' mode — skipping restore", hvacMode);
        }
    }
}
