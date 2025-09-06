using System.Collections.Generic;
using System.Threading;
using NetDaemon.Client;
using HomeAssistantApps.Extensions;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace HomeAssistantApps;

[NetDaemonApp]
[Focus]
public class Zooz : IAsyncInitializable
{
    private readonly ILogger<Zooz> _logger;
    private readonly IHaContext _ha;
    private readonly Entities _entities;
    private readonly Services _services;
    private readonly IScheduler _scheduler;
    private readonly IHomeAssistantApiManager _apiManager;
    private readonly List<ZoozConfig> _config;

    public Zooz(ILogger<Zooz> logger, IHaContext ha, Entities entities, Services services, IScheduler scheduler, IHomeAssistantApiManager apiManager)
    {
        _logger = logger;
        _entities = entities;
        _apiManager = apiManager;
        _services = services;
        _ha = ha;
        _scheduler = scheduler;

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
                            cfg.Held = false;
                            break;
                        case "KeyHeldDown":
                            if (cfg.LinkedEntity.IsOn())
                            {
                                cfg.Held = true;
                                _scheduler.Schedule(TimeSpan.Zero, (recur) =>
                                {
                                    cfg.LinkedEntity.TurnOn(brightnessStepPct:
                                        (cfg.Invert ? -10 : 10) * (e.Data!.PropertyKey == "002" ? 1 : -1)
                                    );
                                    if (cfg.Held)
                                    {
                                        recur(TimeSpan.FromMilliseconds(500));
                                    }
                                });
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
    public bool Held { get; set; } = false;
}