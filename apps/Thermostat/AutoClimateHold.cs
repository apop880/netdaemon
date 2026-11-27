namespace HomeAssistantApps;

//[NetDaemonApp]
public class AutoClimateHold
{
    private readonly ILogger<AutoClimateHold> _logger;
    private readonly ClimateEntity _thermostat;
    private readonly SwitchEntity _holdSwitch;
    private readonly InputBooleanEntity _autoClimate;

    public AutoClimateHold(ILogger<AutoClimateHold> logger, Entities entities)
    {
        _logger = logger;
        _thermostat = entities.Climate.Thermostat73Ca44;
        _holdSwitch = entities.Switch.Thermostat73Ca44Hold;
        _autoClimate = entities.InputBoolean.AutoClimate;

        _autoClimate
            .StateChanges()
            .Where(s => s.New?.State == "on")
            .Subscribe(_ => EnsureHold("auto climate turned on"));

        _holdSwitch
            .StateChanges()
            .Where(s => s.New?.State == "off")
            .Subscribe(_ => EnsureHold("hold switch turned off"));

        _thermostat
            .StateChanges()
            .Subscribe(_ => EnsureHold("thermostat mode changed"));

        EnsureHold("startup");
        _logger.LogInformation("AutoClimateHold: Service is ready");
    }

    private void EnsureHold(string reason)
    {
        if (_autoClimate.IsOff())
            return;

        var hvacMode = _thermostat.State;

        if (hvacMode is not ("heat" or "cool"))
        {
            _logger.LogInformation("AutoClimateHold: Thermostat is in '{Mode}' mode — skipping hold enforcement", hvacMode);
            return;
        }

        if (_holdSwitch.IsOn())
            return;

        EnsureSetpointInRange(hvacMode, reason);

        _logger.LogInformation("AutoClimateHold: Turning hold switch back on — {Reason}", reason);
        _holdSwitch.TurnOn();
    }

    private void EnsureSetpointInRange(string hvacMode, string reason)
    {
        var currentTemp = _thermostat.Attributes?.Temperature;

        if (hvacMode == "heat")
        {
            if (currentTemp is null || currentTemp.Value < ThermostatConstants.MinHeatTemp)
            {
                _logger.LogInformation(
                    "AutoClimateHold: Heat setpoint out of range ({Temp}) — setting to {Target}°F ({Reason})",
                    currentTemp, ThermostatConstants.DefaultHeatTemp, reason);
                _thermostat.SetTemperature(temperature: ThermostatConstants.DefaultHeatTemp);
            }
        }
        else if (hvacMode == "cool")
        {
            if (currentTemp is null || currentTemp.Value > ThermostatConstants.MaxCoolTemp)
            {
                _logger.LogInformation(
                    "AutoClimateHold: Cool setpoint out of range ({Temp}) — setting to {Target}°F ({Reason})",
                    currentTemp, ThermostatConstants.DefaultCoolTemp, reason);
                _thermostat.SetTemperature(temperature: ThermostatConstants.DefaultCoolTemp);
            }
        }
    }
}
