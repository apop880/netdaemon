namespace HomeAssistantApps;

[NetDaemonApp]
public class AwayThermostat
{
    private const double MaxDistanceMiles = 30;

    private readonly ILogger<AwayThermostat> _logger;
    private readonly HomeMode _homeMode;
    private readonly ClimateEntity _thermostat;
    private readonly NumericSensorEntity _nearestDistance;

    private double? _lastAppliedTemp;

    public AwayThermostat(ILogger<AwayThermostat> logger, Entities entities, HomeMode homeMode)
    {
        _logger = logger;
        _homeMode = homeMode;
        _thermostat = entities.Climate.Thermostat73Ca44;
        _nearestDistance = entities.Sensor.HomeNearestDistance;

        // React to home mode changes
        homeMode.Current
            .Subscribe(mode =>
            {
                if (mode == HomeModeState.Away)
                {
                    var distance = _nearestDistance.State ?? 0;
                    ApplyScaledSetpoint(distance);
                }
                else if (_lastAppliedTemp is not null)
                {
                    RestoreDefaultSetpoint($"home mode changed to {mode}");
                }
            });

        // Scale setpoint as distance changes while away
        _nearestDistance
            .StateChanges()
            .Where(_ => _homeMode.CurrentValue == HomeModeState.Away)
            .Where(s => s.New?.State is not null)
            .Subscribe(s => ApplyScaledSetpoint(s.New!.State!.Value));

        _logger.LogInformation("AwayThermostat: Service is ready");
    }

    private void ApplyScaledSetpoint(double distanceMiles)
    {
        var hvacMode = _thermostat.State;
        double setpoint;

        if (hvacMode == "heat")
        {
            var raw = ThermostatConstants.DefaultHeatTemp
                      - (distanceMiles / MaxDistanceMiles)
                      * (ThermostatConstants.DefaultHeatTemp - ThermostatConstants.MinHeatTemp);
            setpoint = Math.Round(Math.Clamp(raw, ThermostatConstants.MinHeatTemp, ThermostatConstants.DefaultHeatTemp));
        }
        else if (hvacMode == "cool")
        {
            var raw = ThermostatConstants.DefaultCoolTemp
                      + (distanceMiles / MaxDistanceMiles)
                      * (ThermostatConstants.MaxCoolTemp - ThermostatConstants.DefaultCoolTemp);
            setpoint = Math.Round(Math.Clamp(raw, ThermostatConstants.DefaultCoolTemp, ThermostatConstants.MaxCoolTemp));
        }
        else
        {
            _logger.LogInformation("AwayThermostat: Thermostat is in '{Mode}' mode — skipping setpoint adjustment", hvacMode);
            return;
        }

        if (setpoint == _lastAppliedTemp)
            return;

        _lastAppliedTemp = setpoint;
        _logger.LogInformation(
            "AwayThermostat: Distance is {Distance:F0} miles, setting {Mode} setpoint to {Temp}°F",
            distanceMiles, hvacMode, setpoint);
        _thermostat.SetTemperature(temperature: setpoint);
    }

    private void RestoreDefaultSetpoint(string reason)
    {
        var hvacMode = _thermostat.State;
        double setpoint;

        if (hvacMode == "heat")
            setpoint = ThermostatConstants.DefaultHeatTemp;
        else if (hvacMode == "cool")
            setpoint = ThermostatConstants.DefaultCoolTemp;
        else
        {
            _logger.LogInformation("AwayThermostat: Thermostat is in '{Mode}' mode — skipping restore", hvacMode);
            _lastAppliedTemp = null;
            return;
        }

        if (setpoint == _lastAppliedTemp)
        {
            _lastAppliedTemp = null;
            return;
        }

        _logger.LogInformation("AwayThermostat: Restoring {Mode} to {Temp}°F — {Reason}", hvacMode, setpoint, reason);
        _thermostat.SetTemperature(temperature: setpoint);
        _lastAppliedTemp = null;
    }
}
