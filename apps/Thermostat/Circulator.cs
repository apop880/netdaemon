using System.Linq;
using System.Reactive.Disposables;

namespace HomeAssistantApps;

public enum CirculatorMode
{
    Off,
    Recirculate,
    Fireplace
}

[NetDaemonApp]
public class Circulator : IDisposable
{
    internal static readonly TimeSpan RecirculateOnDuration = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RecirculateOffDuration = TimeSpan.FromMinutes(45);
    internal static readonly TimeSpan PostCoolingCooldown = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan FireplaceTimeout = TimeSpan.FromHours(4);
    internal static readonly TimeSpan UnavailableRetryDelay = TimeSpan.FromMinutes(1);

    private const string DefaultOffOption = "Off";

    private readonly ILogger<Circulator> _logger;
    private readonly IScheduler _scheduler;
    private readonly HomeMode _homeMode;
    private readonly Notify _notify;
    private readonly InputSelectEntity _circulator;
    private readonly ClimateEntity _thermostat;
    private readonly CompositeDisposable _subscriptions = new();

    private IDisposable? _cycleTimer;
    private IDisposable? _fireplaceTimeoutTimer;
    private DateTimeOffset? _lastCoolingEndedAt;
    private CirculatorMode _currentMode;

    public Circulator(
        ILogger<Circulator> logger,
        IScheduler scheduler,
        Entities entities,
        HomeMode homeMode,
        Notify notify)
    {
        _logger = logger;
        _scheduler = scheduler;
        _homeMode = homeMode;
        _notify = notify;
        _circulator = entities.InputSelect.Circulator;
        _thermostat = entities.Climate.Thermostat73Ca44;
        _currentMode = ParseMode(_circulator.State) ?? CirculatorMode.Off;

        _subscriptions.Add(
            _circulator
                .StateChanges()
                .Subscribe(s => OnCirculatorModeChanged(s.New?.State)));

        _subscriptions.Add(
            _thermostat
                .StateAllChanges()
                .Where(s => !string.Equals(s.New?.Attributes?.HvacAction, s.Old?.Attributes?.HvacAction, StringComparison.OrdinalIgnoreCase))
                .Subscribe(s => OnHvacActionChanged(s.New?.Attributes?.HvacAction, s.Old?.Attributes?.HvacAction)));

        _subscriptions.Add(
            _homeMode.Current
                .Subscribe(OnHomeModeChanged));

        _logger.LogInformation(
            "Circulator: Service is ready (mode={Mode}, homeMode={HomeMode})",
            _currentMode, _homeMode.CurrentValue);

        ApplyCurrentMode(isStartup: true, previousMode: null);
    }

    internal static CirculatorMode? ParseMode(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)
            || state.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
            || state.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return state.Trim().ToLowerInvariant() switch
        {
            "off" => CirculatorMode.Off,
            "recirculate" => CirculatorMode.Recirculate,
            "fireplace" => CirculatorMode.Fireplace,
            _ => null
        };
    }

    internal static bool IsActiveHvac(string? hvacAction) =>
        hvacAction is not null
        && (hvacAction.Equals("heating", StringComparison.OrdinalIgnoreCase)
            || hvacAction.Equals("cooling", StringComparison.OrdinalIgnoreCase)
            || hvacAction.Equals("preheating", StringComparison.OrdinalIgnoreCase)
            || hvacAction.Equals("drying", StringComparison.OrdinalIgnoreCase)
            || hvacAction.Equals("defrosting", StringComparison.OrdinalIgnoreCase));

    internal static bool IsCooling(string? hvacAction) =>
        hvacAction is not null && hvacAction.Equals("cooling", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPausedHomeMode(HomeModeState mode) =>
        mode is HomeModeState.Away or HomeModeState.Vacation;

    private bool IsPausedByHomeMode => IsPausedHomeMode(_homeMode.CurrentValue);

    private bool IsThermostatUnavailable() =>
        string.IsNullOrWhiteSpace(_thermostat.State)
        || _thermostat.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
        || _thermostat.State.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private TimeSpan GetRemainingPostCoolingCooldown()
    {
        if (_lastCoolingEndedAt is null)
            return TimeSpan.Zero;

        var elapsed = _scheduler.Now - _lastCoolingEndedAt.Value;
        var remaining = PostCoolingCooldown - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private string ResolveOffOption() =>
        _circulator.Attributes?.Options?
            .FirstOrDefault(o => o.Equals(DefaultOffOption, StringComparison.OrdinalIgnoreCase))
        ?? DefaultOffOption;

    private void OnCirculatorModeChanged(string? newState)
    {
        var parsedMode = ParseMode(newState);
        if (parsedMode is null)
        {
            _logger.LogWarning("Circulator: Ignoring unrecognized or transient circulator state '{State}'", newState);
            return;
        }

        if (parsedMode.Value == _currentMode)
            return;

        var previousMode = _currentMode;
        _currentMode = parsedMode.Value;
        _logger.LogInformation("Circulator: Mode changed from {PreviousMode} to {NewMode}", previousMode, _currentMode);

        ApplyCurrentMode(isStartup: false, previousMode: previousMode);
    }

    private void ApplyCurrentMode(bool isStartup, CirculatorMode? previousMode)
    {
        CancelCycleTimer();
        CancelFireplaceTimeout();

        switch (_currentMode)
        {
            case CirculatorMode.Off:
                if (!isStartup)
                    EnsureFanAuto();
                break;

            case CirculatorMode.Recirculate:
                StartRecirculate(previousMode);
                break;

            case CirculatorMode.Fireplace:
                StartFireplace();
                break;
        }
    }

    private void StartRecirculate(CirculatorMode? previousMode)
    {
        if (IsPausedByHomeMode)
        {
            _logger.LogInformation("Circulator: Recirculate paused because Home Mode is {HomeMode}", _homeMode.CurrentValue);
            EnsureFanAuto();
            return;
        }

        var hvacAction = _thermostat.Attributes?.HvacAction;
        if (IsActiveHvac(hvacAction))
        {
            _logger.LogInformation("Circulator: Deferring Recirculate until HVAC '{Action}' completes", hvacAction);
            if (IsCooling(hvacAction))
                SetFanModeIfNeeded("auto");
            return;
        }

        var remainingPostCool = GetRemainingPostCoolingCooldown();
        if (remainingPostCool > TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Circulator: Waiting {Minutes:F0} min remaining in post-cooling cooldown before Recirculate",
                remainingPostCool.TotalMinutes);
            EnsureFanAuto();
            ScheduleRecirculatePhase(turnFanOn: true, remainingPostCool);
            return;
        }

        if (previousMode == CirculatorMode.Fireplace)
        {
            _logger.LogInformation(
                "Circulator: Transitioned from Fireplace to Recirculate — resting fan for {Minutes} min",
                RecirculateOffDuration.TotalMinutes);
            EnsureFanAuto();
            ScheduleRecirculatePhase(turnFanOn: true, RecirculateOffDuration);
            return;
        }

        RunRecirculatePhase(turnFanOn: true);
    }

    private void StartFireplace()
    {
        if (IsPausedByHomeMode)
        {
            TurnOffFireplace($"Home Mode is {_homeMode.CurrentValue}");
            return;
        }

        _logger.LogInformation(
            "Circulator: Starting Fireplace mode (continuous fan, {Hours:0}h auto-off timeout)",
            FireplaceTimeout.TotalHours);

        EnsureFanOnForFireplace();

        _fireplaceTimeoutTimer = _scheduler.Schedule(FireplaceTimeout, () =>
        {
            if (_currentMode != CirculatorMode.Fireplace)
                return;

            TurnOffFireplace($"{FireplaceTimeout.TotalHours:0}-hour timeout reached");
        });
    }

    private void EnsureFanOnForFireplace()
    {
        if (_currentMode != CirculatorMode.Fireplace)
            return;

        if (IsThermostatUnavailable())
        {
            _logger.LogWarning(
                "Circulator: Thermostat is '{State}'; retrying Fireplace fan 'on' in {Delay} min",
                _thermostat.State, UnavailableRetryDelay.TotalMinutes);
            CancelCycleTimer();
            _cycleTimer = _scheduler.Schedule(UnavailableRetryDelay, EnsureFanOnForFireplace);
            return;
        }

        SetFanModeIfNeeded("on");
    }

    private void TurnOffFireplace(string reason)
    {
        CancelCycleTimer();
        CancelFireplaceTimeout();
        _currentMode = CirculatorMode.Off;

        _logger.LogInformation("Circulator: Turning off Fireplace mode ({Reason})", reason);
        EnsureFanAuto();
        _circulator.SelectOption(ResolveOffOption());
        _notify.All($"Fireplace circulator mode turned off ({reason}).", title: "Circulator");
    }

    private void OnHvacActionChanged(string? newHvacAction, string? oldHvacAction)
    {
        if (IsThermostatUnavailable())
        {
            _logger.LogDebug("Circulator: Ignoring HVAC action change while thermostat is '{State}'", _thermostat.State);
            return;
        }

        if (IsCooling(oldHvacAction) && !IsCooling(newHvacAction))
        {
            _lastCoolingEndedAt = _scheduler.Now;
            _logger.LogDebug("Circulator: Recorded cooling end at {Time}", _lastCoolingEndedAt);
        }

        if (_currentMode != CirculatorMode.Recirculate || IsPausedByHomeMode)
            return;

        if (IsActiveHvac(newHvacAction))
        {
            CancelCycleTimer();
            if (IsCooling(newHvacAction))
            {
                _logger.LogInformation("Circulator: HVAC cooling started — pausing Recirculate and setting fan to auto");
                SetFanModeIfNeeded("auto");
            }
            else
            {
                _logger.LogInformation("Circulator: HVAC '{Action}' started — pausing Recirculate cycle", newHvacAction);
            }
        }
        else if (IsActiveHvac(oldHvacAction))
        {
            CancelCycleTimer();
            SetFanModeIfNeeded("auto");

            var baseCooldown = IsCooling(oldHvacAction) ? PostCoolingCooldown : RecirculateOffDuration;
            var remainingPostCool = GetRemainingPostCoolingCooldown();
            var cooldown = remainingPostCool > baseCooldown ? remainingPostCool : baseCooldown;

            _logger.LogInformation(
                "Circulator: HVAC '{OldAction}' ended — waiting {Minutes:F0} min before next Recirculate cycle",
                oldHvacAction, cooldown.TotalMinutes);
            ScheduleRecirculatePhase(turnFanOn: true, cooldown);
        }
    }

    private void OnHomeModeChanged(HomeModeState mode)
    {
        if (IsPausedHomeMode(mode))
        {
            if (_currentMode == CirculatorMode.Recirculate)
            {
                _logger.LogInformation("Circulator: Pausing Recirculate (Home Mode changed to {Mode})", mode);
                CancelCycleTimer();
                EnsureFanAuto();
            }
            else if (_currentMode == CirculatorMode.Fireplace)
            {
                TurnOffFireplace($"Home Mode changed to {mode}");
            }
        }
        else if (_currentMode == CirculatorMode.Recirculate && _cycleTimer is null)
        {
            _logger.LogInformation("Circulator: Resuming Recirculate (Home Mode changed to {Mode})", mode);
            StartRecirculate(previousMode: null);
        }
    }

    private void ScheduleRecirculatePhase(bool turnFanOn, TimeSpan delay)
    {
        CancelCycleTimer();
        _cycleTimer = _scheduler.Schedule(delay, () => RunRecirculatePhase(turnFanOn));
    }

    private void RunRecirculatePhase(bool turnFanOn)
    {
        CancelCycleTimer();

        if (_currentMode != CirculatorMode.Recirculate || IsPausedByHomeMode)
            return;

        var hvacAction = _thermostat.Attributes?.HvacAction;
        if (IsActiveHvac(hvacAction))
        {
            _logger.LogDebug("Circulator: Skipping Recirculate phase while HVAC is '{Action}'", hvacAction);
            return;
        }

        if (IsThermostatUnavailable())
        {
            _logger.LogWarning(
                "Circulator: Thermostat is '{State}'; retrying Recirculate fan '{Target}' in {Delay} min",
                _thermostat.State, turnFanOn ? "on" : "auto", UnavailableRetryDelay.TotalMinutes);
            ScheduleRecirculatePhase(turnFanOn, UnavailableRetryDelay);
            return;
        }

        if (turnFanOn)
        {
            var remainingPostCool = GetRemainingPostCoolingCooldown();
            if (remainingPostCool > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Circulator: Deferring Recirculate fan ON for {Minutes:F0} min (post-cooling cooldown)",
                    remainingPostCool.TotalMinutes);
                ScheduleRecirculatePhase(turnFanOn: true, remainingPostCool);
                return;
            }

            SetFanModeIfNeeded("on");
            _logger.LogInformation("Circulator: Recirculate fan ON for {Minutes} min", RecirculateOnDuration.TotalMinutes);
            ScheduleRecirculatePhase(turnFanOn: false, RecirculateOnDuration);
        }
        else
        {
            SetFanModeIfNeeded("auto");
            _logger.LogInformation("Circulator: Recirculate fan AUTO for {Minutes} min", RecirculateOffDuration.TotalMinutes);
            ScheduleRecirculatePhase(turnFanOn: true, RecirculateOffDuration);
        }
    }

    private void EnsureFanAuto()
    {
        if (IsThermostatUnavailable())
        {
            _logger.LogWarning(
                "Circulator: Thermostat is '{State}'; retrying fan 'auto' in {Delay} min",
                _thermostat.State, UnavailableRetryDelay.TotalMinutes);
            CancelCycleTimer();
            _cycleTimer = _scheduler.Schedule(UnavailableRetryDelay, () =>
            {
                if (_currentMode == CirculatorMode.Fireplace)
                    return;
                EnsureFanAuto();
            });
            return;
        }

        SetFanModeIfNeeded("auto");
    }

    private void SetFanModeIfNeeded(string desiredFanMode)
    {
        var currentFanMode = _thermostat.Attributes?.FanMode;
        if (string.Equals(currentFanMode, desiredFanMode, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Circulator: Thermostat fan mode is already '{FanMode}'", desiredFanMode);
            return;
        }

        _logger.LogInformation("Circulator: Setting thermostat fan mode from '{Current}' to '{Desired}'", currentFanMode, desiredFanMode);
        _thermostat.SetFanMode(desiredFanMode);
    }

    private void CancelCycleTimer()
    {
        _cycleTimer?.Dispose();
        _cycleTimer = null;
    }

    private void CancelFireplaceTimeout()
    {
        _fireplaceTimeoutTimer?.Dispose();
        _fireplaceTimeoutTimer = null;
    }

    public void Dispose()
    {
        CancelCycleTimer();
        CancelFireplaceTimeout();
        _subscriptions.Dispose();
        GC.SuppressFinalize(this);
    }
}
