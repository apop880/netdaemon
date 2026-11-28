using System.Reactive.Disposables;

namespace HomeAssistantApps;

[NetDaemonApp]
public class AutoClimateHold : IDisposable
{
    internal static readonly TimeSpan SetpointCheckDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan HoldRetryDelay = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan TurnOnCooldown = TimeSpan.FromSeconds(10);

    private readonly ILogger<AutoClimateHold> _logger;
    private readonly IScheduler _scheduler;
    private readonly HomeMode _homeMode;
    private readonly ClimateEntity _thermostat;
    private readonly SwitchEntity _holdSwitch;
    private readonly InputBooleanEntity _autoClimate;
    private readonly CompositeDisposable _subscriptions = new();

    private IDisposable? _setpointCheckTimer;
    private IDisposable? _retryTimer;
    private DateTimeOffset? _lastTurnOnRequestedAt;

    public AutoClimateHold(
        ILogger<AutoClimateHold> logger,
        IScheduler scheduler,
        Entities entities,
        HomeMode homeMode)
    {
        _logger = logger;
        _scheduler = scheduler;
        _homeMode = homeMode;
        _thermostat = entities.Climate.Thermostat73Ca44;
        _holdSwitch = entities.Switch.Thermostat73Ca44Hold;
        _autoClimate = entities.InputBoolean.AutoClimate;

        _subscriptions.Add(
            _autoClimate
                .StateChanges()
                .Subscribe(s =>
                {
                    if (string.Equals(s.New?.State, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureHold("auto climate turned on");
                    }
                    else if (string.Equals(s.New?.State, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("AutoClimateHold: Auto climate turned off — pausing hold enforcement");
                        CancelPendingTimers();
                        _lastTurnOnRequestedAt = null;
                    }
                }));

        _subscriptions.Add(
            _holdSwitch
                .StateChanges()
                .Subscribe(s =>
                {
                    if (string.Equals(s.New?.State, "on", StringComparison.OrdinalIgnoreCase))
                    {
                        CancelRetryTimer();
                        _lastTurnOnRequestedAt = null;
                    }
                    else if (string.Equals(s.New?.State, "off", StringComparison.OrdinalIgnoreCase))
                    {
                        EnsureHold("hold switch turned off");
                    }
                }));

        _subscriptions.Add(
            _thermostat
                .StateChanges()
                .Subscribe(_ => EnsureHold("thermostat mode changed")));

        EnsureHold("startup");
        _logger.LogInformation("AutoClimateHold: Service is ready");
    }

    private static bool IsUnavailableOrUnknown(string? state) =>
        string.IsNullOrWhiteSpace(state)
        || state.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
        || state.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedHvacMode(string? hvacMode) =>
        hvacMode is not null
        && (hvacMode.Equals("heat", StringComparison.OrdinalIgnoreCase)
            || hvacMode.Equals("cool", StringComparison.OrdinalIgnoreCase));

    private void EnsureHold(string reason)
    {
        if (!_autoClimate.IsOn())
        {
            CancelPendingTimers();
            _lastTurnOnRequestedAt = null;
            return;
        }

        var hvacMode = _thermostat.State;

        if (IsUnavailableOrUnknown(hvacMode))
        {
            _logger.LogWarning(
                "AutoClimateHold: Thermostat is '{State}' — retrying hold check in {Delay} min ({Reason})",
                hvacMode, HoldRetryDelay.TotalMinutes, reason);
            ScheduleRetry("thermostat unavailable");
            return;
        }

        if (!IsSupportedHvacMode(hvacMode))
        {
            _logger.LogInformation("AutoClimateHold: Thermostat is in '{Mode}' mode — skipping hold enforcement", hvacMode);
            CancelPendingTimers();
            _lastTurnOnRequestedAt = null;
            return;
        }

        if (IsUnavailableOrUnknown(_holdSwitch.State))
        {
            _logger.LogWarning(
                "AutoClimateHold: Hold switch is '{State}' — retrying hold check in {Delay} min ({Reason})",
                _holdSwitch.State, HoldRetryDelay.TotalMinutes, reason);
            ScheduleRetry("hold switch unavailable");
            return;
        }

        if (_holdSwitch.IsOn())
        {
            CancelRetryTimer();
            _lastTurnOnRequestedAt = null;
            return;
        }

        if (_lastTurnOnRequestedAt is not null && (_scheduler.Now - _lastTurnOnRequestedAt.Value) < TurnOnCooldown)
        {
            _logger.LogDebug("AutoClimateHold: Skipping duplicate hold turn-on within cooldown ({Reason})", reason);
            return;
        }

        _lastTurnOnRequestedAt = _scheduler.Now;
        _logger.LogInformation("AutoClimateHold: Turning hold switch on — {Reason}", reason);
        _holdSwitch.TurnOn();

        ScheduleSetpointCheck(reason);
        ScheduleRetry("hold switch still off");
    }

    private void ScheduleSetpointCheck(string reason)
    {
        CancelSetpointCheckTimer();
        _setpointCheckTimer = _scheduler.Schedule(SetpointCheckDelay, () => EnsureSetpointInRange(reason));
    }

    private void ScheduleRetry(string reason)
    {
        CancelRetryTimer();
        _retryTimer = _scheduler.Schedule(HoldRetryDelay, () => EnsureHold($"retry: {reason}"));
    }

    private void EnsureSetpointInRange(string reason)
    {
        CancelSetpointCheckTimer();

        if (!_autoClimate.IsOn())
            return;

        var hvacMode = _thermostat.State;
        if (!IsSupportedHvacMode(hvacMode))
            return;

        var isVacation = _homeMode.CurrentValue == HomeModeState.Vacation;
        var currentTemp = _thermostat.Attributes?.Temperature;

        if (hvacMode!.Equals("heat", StringComparison.OrdinalIgnoreCase))
        {
            var minHeat = isVacation ? ThermostatConstants.VacationHeatTemp : ThermostatConstants.MinHeatTemp;
            var targetHeat = isVacation ? ThermostatConstants.VacationHeatTemp : ThermostatConstants.DefaultHeatTemp;

            if (currentTemp is null || currentTemp.Value < minHeat)
            {
                _logger.LogInformation(
                    "AutoClimateHold: Heat setpoint out of range ({Temp}°F < {Min}°F, homeMode={HomeMode}) — setting to {Target}°F ({Reason})",
                    currentTemp, minHeat, _homeMode.CurrentValue, targetHeat, reason);
                _thermostat.SetTemperature(temperature: targetHeat);
            }
        }
        else if (hvacMode.Equals("cool", StringComparison.OrdinalIgnoreCase))
        {
            var maxCool = isVacation ? ThermostatConstants.VacationCoolTemp : ThermostatConstants.MaxCoolTemp;
            var targetCool = isVacation ? ThermostatConstants.VacationCoolTemp : ThermostatConstants.DefaultCoolTemp;

            if (currentTemp is null || currentTemp.Value > maxCool)
            {
                _logger.LogInformation(
                    "AutoClimateHold: Cool setpoint out of range ({Temp}°F > {Max}°F, homeMode={HomeMode}) — setting to {Target}°F ({Reason})",
                    currentTemp, maxCool, _homeMode.CurrentValue, targetCool, reason);
                _thermostat.SetTemperature(temperature: targetCool);
            }
        }
    }

    private void CancelSetpointCheckTimer()
    {
        _setpointCheckTimer?.Dispose();
        _setpointCheckTimer = null;
    }

    private void CancelRetryTimer()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    private void CancelPendingTimers()
    {
        CancelSetpointCheckTimer();
        CancelRetryTimer();
    }

    public void Dispose()
    {
        CancelPendingTimers();
        _subscriptions.Dispose();
        GC.SuppressFinalize(this);
    }
}
