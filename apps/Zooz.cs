using System.Collections.Generic;
using System.Threading;
using NetDaemon.Client;
using HomeAssistantApps.Extensions;
using System.Threading.Tasks;
using System.Diagnostics;

namespace HomeAssistantApps;

[NetDaemonApp]
public class Zooz : IAsyncInitializable
{
    private readonly ILogger<Zooz> _logger;
    private readonly IHaContext _ha;
    private readonly Entities _entities;
    private readonly Services _services;
    private readonly IHomeAssistantApiManager _apiManager;
    private readonly List<ZoozConfig> _config;

    public Zooz(ILogger<Zooz> logger, IHaContext ha, Entities entities, Services services, IHomeAssistantApiManager apiManager)
    {
        _logger = logger;
        _entities = entities;
        _apiManager = apiManager;
        _services = services;
        _ha = ha;

        _config =
        [
            new() {
                Entity = _entities.Light.KitchenTableSwitch,
                LinkedEntity = _entities.Light.KitchenTable
            },
            new() {
                Entity = _entities.Light.ZWaveKitchenCanLightSwitch,
                LinkedEntity = _entities.Light.KitchenCanLights,
                Invert = true
            },
            new() {
                Entity = _entities.Light.ZWaveKitchenSinkLightSwitch,
                LinkedEntity = _entities.Light.KitchenSink
            },
            new() {
                Entity = _entities.Switch.ZWaveLivingRoomLightSwitch,
                LinkedEntity = _entities.Light.LivingCanLights,
                Invert = true
            },
            new() {
                Entity = _entities.Light.OfficeMainLights,
                LinkedEntity = _entities.Light.OfficeBulbs,
                Invert = true
            },
            new() {
                Entity = _entities.Light.GuestBedroom,
                LinkedEntity = _entities.Light.GuestBedroomLights,
                Invert = true
            },
            new() {
                Entity = _entities.Light.Bar,
                LinkedEntity = _entities.Light.BarLights,
                Invert = true
            }
        ];
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var cfg in _config)
        {
            if (cfg.Entity is null || cfg.LinkedEntity is null)
            {
                _logger.LogWarning("Zooz configuration has null entity or linked entity, skipping.");
                continue;
            }
            cfg.DeviceId = await _apiManager.GetDeviceId(cfg.Entity, cancellationToken);

            if (string.IsNullOrEmpty(cfg.DeviceId))
            {
                _logger.LogWarning("Could not determine DeviceId for entity {EntityId}, skipping.", cfg.Entity.EntityId);
                continue;
            }

            cfg.LinkedEntity.StateChanges()
                .Where(e => e.New?.State is not null && e.Old?.State is not null)
                .Subscribe(e =>
                {
                    _services.ZwaveJs.SetConfigParameter(new()
                    {
                        DeviceId = [cfg.DeviceId],
                        Parameter = "2",
                        Value = e.New?.State == "on" ? "2" : "3"
                    });
                });

            _ha.Events.Filter<ZWaveValueNotificationData>("zwave_js_value_notification")
                .Where(e => e.Data != null && e.Data.IsValid && e.Data.DeviceId == cfg.DeviceId)
                .Subscribe(e =>
                {
                    var value = e.Data!.Value;
                    switch (value)
                    {
                        case "KeyPressed":
                            cfg.LinkedEntity.Toggle();
                            break;
                        case "KeyReleased":
                            if (cfg.LinkedEntity.IsOn() && cfg.Stopwatch != null)
                            {
                                cfg.Stopwatch.Stop(); // Stop the stopwatch
                                var secondsHeld = cfg.Stopwatch.Elapsed.TotalSeconds; // Get precise elapsed time

                                var rate = (cfg.Goal - cfg.Start) / cfg.Transition; // Brightness change rate per second
                                var estimatedBrightness = cfg.Start + rate * secondsHeld;
                                // Clamp the brightness to valid range (1 to 255)
                                estimatedBrightness = Math.Clamp(estimatedBrightness, 1, 255);

                                cfg.LinkedEntity.TurnOn(brightness: (long)estimatedBrightness);
                            }
                            break;
                        case "KeyHeldDown":
                            if (cfg.LinkedEntity.IsOn())
                            {
                                cfg.Stopwatch = Stopwatch.StartNew(); // Start the stopwatch
                                cfg.Goal = e.Data.PropertyKey == "002" ? 255 : 1;
                                if (cfg.Invert) cfg.Goal = 256 - cfg.Goal;
                                cfg.Start = cfg.LinkedEntity.Attributes?.Brightness ?? 255;
                                var transition = cfg.Delta * 5.0 / 255; // Scale transition time to 5 seconds for full range
                                if (transition < 1) transition = 1; // Minimum transition time of 1s
                                cfg.Transition = (long)transition;
                                cfg.LinkedEntity.TurnOn(brightness: (long)cfg.Goal, transition: cfg.Transition);
                            }
                            break;
                    }
                });
        }
        return;
    }
}

record ZWaveValueNotificationData
{
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("property_key")] public string? PropertyKey { get; set; }

    public bool IsValid =>
        Value is "KeyPressed" or "KeyReleased" or "KeyHeldDown";
}

public class ZoozConfig
{
    public Entity? Entity { get; set; }
    public LightEntity? LinkedEntity { get; set; }
    public bool Invert { get; set; } = false;
    public string DeviceId { get; set; } = string.Empty;
    public double Goal { get; set; }
    public double Start { get; set; }
    public double Delta => Math.Abs(Goal - Start);
    public long Transition { get; set; } = 1;
    public Stopwatch? Stopwatch { get; set; } // Add Stopwatch property
}