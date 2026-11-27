using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Text.Json;
using HomeAssistantApps;
using HomeAssistantGenerated;
using Microsoft.Extensions.Logging.Abstractions;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace netdaemon.Tests;

public class CirculatorTests
{
    private const string CirculatorEntityId = "input_select.circulator";
    private const string ThermostatEntityId = "climate.thermostat_73_ca_44";
    private const string HomeZoneEntityId = "zone.home";
    private const string VacationModeEntityId = "input_boolean.vacation_mode";
    private const string GuestModeEntityId = "input_boolean.guest_mode";

    [Fact]
    public void Recirculate_Cycles15MinOnAnd45MinOffIndefinitely()
    {
        var harness = new TestHarness(circulatorState: "Recirculate");
        using var app = harness.CreateCirculator();

        // Starts immediately with fan ON
        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Still on at 14m59s
        harness.AdvanceBy(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        Assert.Equal(["on"], harness.FanModeCalls);

        // Turns auto at 15m
        harness.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "auto", hvacAction: "idle");

        // Still auto at 15m + 44m59s
        harness.AdvanceBy(TimeSpan.FromMinutes(44) + TimeSpan.FromSeconds(59));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);

        // Turns on again at 60m
        harness.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(["on", "auto", "on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Turns auto again at 75m
        harness.AdvanceBy(TimeSpan.FromMinutes(15));
        Assert.Equal(["on", "auto", "on", "auto"], harness.FanModeCalls);
    }

    [Fact]
    public void Recirculate_HeatingStartingFromFanAction_DoesNotDropFanAndWaits45MinAfterHeatingEnds()
    {
        var harness = new TestHarness(circulatorState: "Recirculate");
        using var app = harness.CreateCirculator();

        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Heating starts 5 minutes into the recirculate ON window (transitioning from "fan" -> "heating")
        harness.AdvanceBy(TimeSpan.FromMinutes(5));
        harness.SetThermostat(fanMode: "on", hvacAction: "heating");

        // Should NOT force fan to auto mid-ignition, and old 15m timer must be cancelled
        Assert.Equal(["on"], harness.FanModeCalls);
        harness.AdvanceBy(TimeSpan.FromMinutes(20));
        Assert.Equal(["on"], harness.FanModeCalls);

        // Heating finishes -> transitions to "fan" because fanMode is still "on"
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "auto", hvacAction: "idle");

        // Must wait full 45 min after heating ends
        harness.AdvanceBy(TimeSpan.FromMinutes(44));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);

        harness.AdvanceBy(TimeSpan.FromMinutes(1));
        Assert.Equal(["on", "auto", "on"], harness.FanModeCalls);
    }

    [Fact]
    public void Recirculate_PostCoolingCooldown_WaitsFull60Minutes()
    {
        var harness = new TestHarness(circulatorState: "Recirculate", thermostatState: "cool");
        using var app = harness.CreateCirculator();

        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(state: "cool", fanMode: "on", hvacAction: "fan");

        // Cooling starts -> fan returned to auto immediately
        harness.SetThermostat(state: "cool", fanMode: "on", hvacAction: "cooling");
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        harness.SetThermostat(state: "cool", fanMode: "auto", hvacAction: "cooling");

        // Cooling ends
        harness.AdvanceBy(TimeSpan.FromMinutes(10));
        harness.SetThermostat(state: "cool", fanMode: "auto", hvacAction: "idle");

        // Must NOT turn on at 45m or 59m
        harness.AdvanceBy(TimeSpan.FromMinutes(59));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);

        // Turns on at 60m
        harness.AdvanceBy(TimeSpan.FromMinutes(1));
        Assert.Equal(["on", "auto", "on"], harness.FanModeCalls);
    }

    [Fact]
    public void Recirculate_PausesOnAwayAndVacation_ResumesOnHome()
    {
        var harness = new TestHarness(circulatorState: "Recirculate");
        using var app = harness.CreateCirculator();

        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Home -> Away
        harness.SetHomeZoneCount(0);
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "auto", hvacAction: "idle");

        // Dropdown must NOT be changed to Off, and fan must remain auto while away
        Assert.Empty(harness.SelectOptionCalls);
        harness.AdvanceBy(TimeSpan.FromHours(3));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);

        // Away -> Home -> resumes recirculating
        harness.SetHomeZoneCount(1);
        Assert.Equal(["on", "auto", "on"], harness.FanModeCalls);
    }

    [Fact]
    public void Fireplace_RunsContinuouslyAndAutoTurnsOffWithNotificationAfter4Hours()
    {
        var harness = new TestHarness(circulatorState: "Off");
        using var app = harness.CreateCirculator();

        Assert.Empty(harness.FanModeCalls);

        harness.SetCirculator("Fireplace");
        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Still running continuously at 3h59m
        harness.AdvanceBy(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(59));
        Assert.Equal(["on"], harness.FanModeCalls);
        Assert.Empty(harness.SelectOptionCalls);
        Assert.Empty(harness.NotifyAllCalls);

        // At 4h -> reverts to Off, fan auto, sends Notify.All
        harness.AdvanceBy(TimeSpan.FromMinutes(1));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        Assert.Equal(["Off"], harness.SelectOptionCalls);
        Assert.Single(harness.NotifyAllCalls);
        Assert.Contains("4-hour timeout", harness.NotifyAllCalls[0].Message);
    }

    [Fact]
    public void Fireplace_AutoTurnsOffWithNotificationWhenHouseGoesAway()
    {
        var harness = new TestHarness(circulatorState: "Fireplace");
        using var app = harness.CreateCirculator();

        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        harness.SetHomeZoneCount(0);

        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        Assert.Equal(["Off"], harness.SelectOptionCalls);
        Assert.Single(harness.NotifyAllCalls);
        Assert.Contains("Away", harness.NotifyAllCalls[0].Message);
    }

    [Fact]
    public void TransitionFromFireplaceToRecirculate_CancelsTimeoutAndRestsFanFor45Min()
    {
        var harness = new TestHarness(circulatorState: "Fireplace");
        using var app = harness.CreateCirculator();

        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Switch to Recirculate after 2 hours
        harness.AdvanceBy(TimeSpan.FromHours(2));
        harness.SetCirculator("Recirculate");

        // Should rest fan to auto immediately and NOT trigger Fireplace 4h timeout later
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "auto", hvacAction: "idle");

        harness.AdvanceBy(TimeSpan.FromMinutes(44));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);

        harness.AdvanceBy(TimeSpan.FromMinutes(1));
        Assert.Equal(["on", "auto", "on"], harness.FanModeCalls);

        // Advance well past original 4h fireplace timeout -> no auto-off or notification
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");
        harness.AdvanceBy(TimeSpan.FromHours(3));
        Assert.Empty(harness.SelectOptionCalls);
        Assert.Empty(harness.NotifyAllCalls);
    }

    [Fact]
    public void Recirculate_ThermostatUnavailableAtOffBoundary_RetriesAndTurnsFanAuto()
    {
        var harness = new TestHarness(circulatorState: "Recirculate");
        using var app = harness.CreateCirculator();

        Assert.Equal(["on"], harness.FanModeCalls);
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Thermostat drops offline right before the 15-min ON phase ends
        harness.AdvanceBy(TimeSpan.FromMinutes(14));
        harness.SetThermostatUnavailable();

        // At 15m, thermostat is unavailable -> skips call and schedules 1m retry
        harness.AdvanceBy(TimeSpan.FromMinutes(1));
        Assert.Equal(["on"], harness.FanModeCalls);

        // Thermostat recovers with fan still stuck on
        harness.SetThermostat(fanMode: "on", hvacAction: "fan");

        // Retry fires at +1m -> turns fan back to auto and resumes cycle
        harness.AdvanceBy(TimeSpan.FromMinutes(1));
        Assert.Equal(["on", "auto"], harness.FanModeCalls);
    }

    private sealed class TestHarness
    {
        private readonly FakeHaContext _ha = new();
        private readonly HistoricalScheduler _scheduler = new();

        public List<string> FanModeCalls { get; } = [];
        public List<string> SelectOptionCalls { get; } = [];
        public List<(string Message, string? Title)> NotifyAllCalls { get; } = [];

        public TestHarness(
            string circulatorState = "Off",
            string thermostatState = "heat",
            string fanMode = "auto",
            string hvacAction = "idle",
            int homeZoneCount = 1)
        {
            _ha.OnServiceCall = (domain, service, _, data) =>
            {
                if (domain == "climate" && service == "set_fan_mode" && data is ClimateSetFanModeParameters fanParams)
                {
                    FanModeCalls.Add(fanParams.FanMode?.ToString() ?? string.Empty);
                }
                else if (domain == "input_select" && service == "select_option" && data is InputSelectSelectOptionParameters selectParams)
                {
                    SelectOptionCalls.Add(selectParams.Option?.ToString() ?? string.Empty);
                }
                else if (domain == "notify" && service == "all" && data is NotifyAllParameters notifyParams)
                {
                    NotifyAllCalls.Add((notifyParams.Message ?? string.Empty, notifyParams.Title));
                }
            };

            _ha.SetState(HomeZoneEntityId, homeZoneCount.ToString());
            _ha.SetState(VacationModeEntityId, "off");
            _ha.SetState(GuestModeEntityId, "off");
            _ha.SetState(CirculatorEntityId, circulatorState, new { options = new[] { "Off", "Recirculate", "Fireplace" } });
            _ha.SetState(ThermostatEntityId, thermostatState, new { fan_mode = fanMode, hvac_action = hvacAction });
        }

        public Circulator CreateCirculator()
        {
            var entities = new Entities(_ha);
            var services = new Services(_ha);
            var homeMode = new HomeMode(entities);
            var notify = new Notify(NullLogger<Notify>.Instance, services);
            return new Circulator(NullLogger<Circulator>.Instance, _scheduler, entities, homeMode, notify);
        }

        public void AdvanceBy(TimeSpan delta) => _scheduler.AdvanceBy(delta);

        public void SetCirculator(string option) =>
            _ha.SetState(CirculatorEntityId, option, new { options = new[] { "Off", "Recirculate", "Fireplace" } });

        public void SetThermostat(string state = "heat", string fanMode = "auto", string hvacAction = "idle") =>
            _ha.SetState(ThermostatEntityId, state, new { fan_mode = fanMode, hvac_action = hvacAction });

        public void SetThermostatUnavailable() =>
            _ha.SetState(ThermostatEntityId, "unavailable", null);

        public void SetHomeZoneCount(int count) =>
            _ha.SetState(HomeZoneEntityId, count.ToString());
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
