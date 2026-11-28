using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Text.Json;
using HomeAssistantApps;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging.Abstractions;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace netdaemon.Tests;

public class AutoClimateHoldTests
{
    private const string ThermostatEntityId = "climate.thermostat_73_ca_44";
    private const string HoldSwitchEntityId = "switch.thermostat_73_ca_44_hold";
    private const string AutoClimateEntityId = "input_boolean.auto_climate";
    private const string HomeZoneEntityId = "zone.home";
    private const string VacationModeEntityId = "input_boolean.vacation_mode";
    private const string GuestModeEntityId = "input_boolean.guest_mode";

    [Fact]
    public void Startup_WhenAutoClimateOnAndHoldOff_TurnsHoldOnFirstThenFixesOutOfRangeHeatSetpoint()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "off",
            thermostatState: "heat",
            temperature: 55);

        using var app = harness.CreateApp();

        // Hold turns on immediately; setpoint is NOT changed yet
        Assert.Equal(1, harness.HoldTurnOnCount);
        Assert.Empty(harness.SetTemperatureCalls);

        // Hold confirms on
        harness.SetHold("on");

        // Still no setpoint call right before delay elapses
        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay - TimeSpan.FromSeconds(1));
        Assert.Empty(harness.SetTemperatureCalls);

        // At SetpointCheckDelay, out-of-range heat setpoint is restored to DefaultHeatTemp
        harness.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal([ThermostatConstants.DefaultHeatTemp], harness.SetTemperatureCalls);
    }

    [Fact]
    public void HoldTurnsOff_WhenSetpointUpdatesBeforeDelay_CatchesDelayedScheduleTempJump()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "on",
            thermostatState: "heat",
            temperature: 68);

        using var app = harness.CreateApp();
        Assert.Equal(0, harness.HoldTurnOnCount);

        // Hold drops off while temperature attribute is still stale (68)
        harness.SetHold("off");
        Assert.Equal(1, harness.HoldTurnOnCount);
        Assert.Empty(harness.SetTemperatureCalls);

        // 2 seconds later, HA receives the thermostat's wonky schedule temp (55) and hold=on
        harness.AdvanceBy(TimeSpan.FromSeconds(2));
        harness.SetThermostat("heat", 55);
        harness.SetHold("on");

        // Remaining delay elapses -> catches 55 < 64 and restores to 68
        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay - TimeSpan.FromSeconds(2));
        Assert.Equal([ThermostatConstants.DefaultHeatTemp], harness.SetTemperatureCalls);
    }

    [Fact]
    public void HoldTurnsOff_InCoolModeAboveMax_RestoresDefaultCoolTemp()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "on",
            thermostatState: "cool",
            temperature: 82);

        using var app = harness.CreateApp();
        harness.SetHold("off");
        harness.SetHold("on");

        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay);
        Assert.Equal(1, harness.HoldTurnOnCount);
        Assert.Equal([ThermostatConstants.DefaultCoolTemp], harness.SetTemperatureCalls);
    }

    [Fact]
    public void HoldTurnsOff_WhenInVacationMode_PreservesVacationSetpoints()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "on",
            thermostatState: "heat",
            temperature: ThermostatConstants.VacationHeatTemp,
            homeZoneCount: 0,
            vacationMode: "on");

        using var app = harness.CreateApp();

        harness.SetHold("off");
        harness.SetHold("on");
        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay);

        Assert.Equal(1, harness.HoldTurnOnCount);
        Assert.Empty(harness.SetTemperatureCalls);
    }

    [Fact]
    public void HoldTurnsOff_WhenInVacationModeAndBelowVacationTemp_RestoresVacationTemp()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "on",
            thermostatState: "heat",
            temperature: 55,
            homeZoneCount: 0,
            vacationMode: "on");

        using var app = harness.CreateApp();

        harness.SetHold("off");
        harness.SetHold("on");
        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay);

        Assert.Equal(1, harness.HoldTurnOnCount);
        Assert.Equal([ThermostatConstants.VacationHeatTemp], harness.SetTemperatureCalls);
    }

    [Fact]
    public void HoldTurnsOff_WhenInVacationCoolMode_PreservesVacationCoolTempAndFixesHigher()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "on",
            thermostatState: "cool",
            temperature: ThermostatConstants.VacationCoolTemp,
            homeZoneCount: 0,
            vacationMode: "on");

        using var app = harness.CreateApp();

        // At 80°F (VacationCoolTemp) -> preserved
        harness.SetHold("off");
        harness.SetHold("on");
        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay);
        Assert.Empty(harness.SetTemperatureCalls);

        // Above 80°F -> restored to 80°F
        harness.SetThermostat("cool", 85);
        harness.SetHold("off");
        harness.SetHold("on");
        harness.AdvanceBy(AutoClimateHold.SetpointCheckDelay);
        Assert.Equal([ThermostatConstants.VacationCoolTemp], harness.SetTemperatureCalls);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    public void AutoClimateNotOn_DoesNotEnforceHoldOrTurnHoldOff(string autoClimateState)
    {
        var harness = new TestHarness(
            autoClimateState: autoClimateState,
            holdState: "off",
            thermostatState: "heat",
            temperature: 55);

        using var app = harness.CreateApp();
        harness.AdvanceBy(TimeSpan.FromMinutes(5));

        Assert.Equal(0, harness.HoldTurnOnCount);
        Assert.Equal(0, harness.HoldTurnOffCount);
        Assert.Empty(harness.SetTemperatureCalls);
    }

    [Fact]
    public void AutoClimateTurnsOff_LeavesHoldSwitchUntouchedAndCancelsPendingCheck()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "on",
            thermostatState: "heat",
            temperature: 55);

        using var app = harness.CreateApp();

        harness.SetHold("off");
        Assert.Equal(1, harness.HoldTurnOnCount);

        // Turn auto_climate off before SetpointCheckDelay fires
        harness.SetAutoClimate("off");
        harness.AdvanceBy(TimeSpan.FromMinutes(2));

        Assert.Equal(0, harness.HoldTurnOffCount);
        Assert.Empty(harness.SetTemperatureCalls);
        Assert.Equal(1, harness.HoldTurnOnCount);
    }

    [Fact]
    public void HoldSwitchUnavailable_DoesNotCallTurnOn_RetriesAndRecovers()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "unavailable",
            thermostatState: "heat",
            temperature: 68);

        using var app = harness.CreateApp();
        Assert.Equal(0, harness.HoldTurnOnCount);

        // Still unavailable at retry
        harness.AdvanceBy(AutoClimateHold.HoldRetryDelay);
        Assert.Equal(0, harness.HoldTurnOnCount);

        // Switch recovers as off -> immediately turns hold on
        harness.SetHold("off");
        Assert.Equal(1, harness.HoldTurnOnCount);
    }

    [Fact]
    public void HoldTurnOnDropped_RetriesAfterRetryDelay_AndCooldownPreventsDuplicateImmediateCalls()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "off",
            thermostatState: "heat",
            temperature: 68);

        using var app = harness.CreateApp();
        Assert.Equal(1, harness.HoldTurnOnCount);

        // Trigger another event within cooldown while hold is still off -> ignored
        harness.AdvanceBy(TimeSpan.FromSeconds(2));
        harness.SetThermostat("cool", 73);
        Assert.Equal(1, harness.HoldTurnOnCount);

        // Retry fires after HoldRetryDelay because hold switch never turned on
        harness.AdvanceBy(AutoClimateHold.HoldRetryDelay - TimeSpan.FromSeconds(2));
        Assert.Equal(2, harness.HoldTurnOnCount);

        // Once hold confirms on, no further retries happen
        harness.SetHold("on");
        harness.AdvanceBy(TimeSpan.FromMinutes(5));
        Assert.Equal(2, harness.HoldTurnOnCount);
    }

    [Fact]
    public void ThermostatOff_SkipsHoldEnforcement_EnforcesWhenSwitchedToHeat()
    {
        var harness = new TestHarness(
            autoClimateState: "on",
            holdState: "off",
            thermostatState: "off",
            temperature: 55);

        using var app = harness.CreateApp();
        Assert.Equal(0, harness.HoldTurnOnCount);

        harness.SetThermostat("heat", 68);
        Assert.Equal(1, harness.HoldTurnOnCount);
    }

    private sealed class TestHarness
    {
        private readonly FakeHaContext _ha = new();
        private readonly HistoricalScheduler _scheduler = new();

        public int HoldTurnOnCount { get; private set; }
        public int HoldTurnOffCount { get; private set; }
        public List<double> SetTemperatureCalls { get; } = [];

        public TestHarness(
            string autoClimateState = "on",
            string holdState = "on",
            string thermostatState = "heat",
            double? temperature = 68,
            int homeZoneCount = 1,
            string vacationMode = "off",
            string guestMode = "off")
        {
            _ha.OnServiceCall = (domain, service, target, data) =>
            {
                if (domain == "switch" && service == "turn_on" && target?.EntityIds?.Contains(HoldSwitchEntityId) == true)
                {
                    HoldTurnOnCount++;
                }
                else if (domain == "switch" && service == "turn_off" && target?.EntityIds?.Contains(HoldSwitchEntityId) == true)
                {
                    HoldTurnOffCount++;
                }
                else if (domain == "climate" && service == "set_temperature" && data is ClimateSetTemperatureParameters tempParams && tempParams.Temperature.HasValue)
                {
                    SetTemperatureCalls.Add(tempParams.Temperature.Value);
                }
            };

            _ha.SetState(HomeZoneEntityId, homeZoneCount.ToString());
            _ha.SetState(VacationModeEntityId, vacationMode);
            _ha.SetState(GuestModeEntityId, guestMode);
            _ha.SetState(AutoClimateEntityId, autoClimateState);
            _ha.SetState(HoldSwitchEntityId, holdState);
            SetThermostat(thermostatState, temperature);
        }

        public AutoClimateHold CreateApp()
        {
            var entities = new Entities(_ha);
            var homeMode = new HomeMode(entities);
            return new AutoClimateHold(NullLogger<AutoClimateHold>.Instance, _scheduler, entities, homeMode);
        }

        public void AdvanceBy(TimeSpan delta) => _scheduler.AdvanceBy(delta);

        public void SetAutoClimate(string state) => _ha.SetState(AutoClimateEntityId, state);

        public void SetHold(string state) => _ha.SetState(HoldSwitchEntityId, state);

        public void SetThermostat(string state, double? temperature)
        {
            object? attrs = temperature.HasValue ? new { temperature = temperature.Value } : null;
            _ha.SetState(ThermostatEntityId, state, attrs);
        }
    }

    private sealed class FakeHaContext : IHaContext
    {
        private readonly Dictionary<string, EntityState> _states = new();
        private readonly Subject<StateChange> _stateChanges = new();
        private readonly Subject<Event> _events = new();

        public Action<string, string, ServiceTarget?, object?>? OnServiceCall { get; set; }

        public IObservable<Event> Events => _events;

        public IObservable<StateChange> StateAllChanges() => _stateChanges;

        public EntityState? GetState(string entityId) => _states.GetValueOrDefault(entityId);

        public IReadOnlyList<Entity> GetAllEntities() =>
            _states.Keys.Select(id => new Entity(this, id)).ToList();

        public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) =>
            OnServiceCall?.Invoke(domain, service, target, data);

        public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null)
        {
            OnServiceCall?.Invoke(domain, service, target, data);
            return Task.FromResult<JsonElement?>(null);
        }

        public Area? GetAreaFromEntityId(string entityId) => null;

        public Entity Entity(string entityId) => new(this, entityId);

        public EntityRegistration? GetEntityRegistration(string entityId) => null;

        public void SendEvent(string eventType, object? data = null) { }

        public void SetState(string entityId, string state, object? attributes = null)
        {
            var oldState = _states.GetValueOrDefault(entityId);
            JsonElement? attrElement = attributes is null
                ? null
                : JsonSerializer.SerializeToElement(attributes);

            var newState = new EntityState
            {
                EntityId = entityId,
                State = state,
                AttributesJson = attrElement
            };

            _states[entityId] = newState;
            _stateChanges.OnNext(new StateChange(new Entity(this, entityId), oldState, newState));
        }
    }
}
