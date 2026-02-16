namespace HomeAssistantApps;

[NetDaemonApp]
public class Circulator
{
    private readonly ILogger<Circulator> _logger;
    private readonly IScheduler _scheduler;
    private readonly InputBooleanEntity circulator;
    private readonly ClimateEntity thermostat;

    private IDisposable? _timer;

    public Circulator(ILogger<Circulator> logger, IScheduler scheduler, Entities entities)
    {
        _logger = logger;
        _scheduler = scheduler;
        circulator = entities.InputBoolean.Circulator;
        thermostat = entities.Climate.Thermostat73Ca44;

        circulator
        .StateChanges()
        .Subscribe(s => CirculatorChanged(s.New?.State, s.Old?.State));

        thermostat
        .StateAllChanges()
        .Where(s => circulator.IsOn() && s.New?.Attributes?.HvacAction != s.Old?.Attributes?.HvacAction)
        .Subscribe(s => ThermostatHvacActionChanged(s.New?.Attributes?.HvacAction, s.Old?.Attributes?.HvacAction));

        _logger.LogInformation("Circulator service is ready");

        if (circulator.IsOn() && (thermostat.Attributes?.HvacAction == "idle" || thermostat.IsOff()))
        {
            StartCycle();
        }
    }

    private void ToggleFan(string fanMode)
    {
        thermostat.SetFanMode(fanMode);
    }

    private void StartCycle()
    {
        _timer = _scheduler.Schedule(TimeSpan.Zero, (recur) =>
        {
            var fanMode = thermostat.Attributes?.FanMode;
            if (fanMode == "auto")
            {
                ToggleFan("on");
                recur(TimeSpan.FromMinutes(20));
            }
            else if (fanMode == "on")
            {
                ToggleFan("auto");
                recur(TimeSpan.FromMinutes(10));
            }
        });
    }

    private void CirculatorChanged(string? newState, string? oldState)
    {
        if (newState == "on" && oldState == "off")
        {
            // Start cycle only if thermostat is idle
            if (thermostat.Attributes?.HvacAction == "idle" || thermostat.IsOff())
            {
                StartCycle();
            }
        }
        else if (newState == "off" && oldState == "on")
        {
            // Clear timers and turn off fan
            _timer?.Dispose();
            ToggleFan("auto");
        }
    }

    private void ThermostatHvacActionChanged(string? newHvacAction, string? oldHvacAction)
    {
        if ((newHvacAction == "cooling" || newHvacAction == "heating") && (oldHvacAction == "idle" || oldHvacAction is null))
        {
            // Turn off fan and cancel timers when cooling/heating starts
            _timer?.Dispose();
            ToggleFan("auto");
        }
        else if ((newHvacAction == "idle" || newHvacAction is null) && (oldHvacAction == "cooling" || oldHvacAction == "heating"))
        {
            // When cooling/heating stops, turn off fan and wait 10 minutes before starting cycle
            _timer?.Dispose();
            ToggleFan("auto");
            _timer = _scheduler.Schedule(TimeSpan.FromMinutes(10), () =>
                {
                    StartCycle();
                });
            }
    }
}