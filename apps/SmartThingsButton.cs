using System.Collections.Generic;
using HomeAssistantApps.Extensions;
using NetDaemon.Client;
using System.Threading.Tasks;
using System.Threading;

namespace HomeAssistantApps;

[NetDaemonApp]
public class SmartThingsButton : IAsyncInitializable
{
    private readonly ILogger<SmartThingsButton> _logger;
    private readonly IHaContext _ha;
    private readonly IHomeAssistantApiManager _apiManager;
    private readonly List<SmartThingsButtonConfig> _config;
    public SmartThingsButton(ILogger<SmartThingsButton> logger, IHaContext ha, Entities entities, Services services, IHomeAssistantApiManager apiManager)
    {
        _logger = logger;
        _apiManager = apiManager;
        _ha = ha;

        _config = [
            new()
            {
                ButtonEntity = entities.BinarySensor.MarcysButton,
                TapAction = "turn_on",
                TapDevice = entities.InputBoolean.MarcyLullabies,
                HoldAction = "turn_on",
                HoldDevice = entities.InputBoolean.MarcyLullabies,
                DoubleTapAction = "turn_on",
                DoubleTapDevice = entities.InputBoolean.MarcyLullabies
            },
            new()
            {
                ButtonEntity = entities.BinarySensor.MorgansButton,
                TapAction = "toggle",
                TapDevice = entities.Light.MorganCanopy
            }
        ];
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var cfg in _config)
        {
            if (cfg.ButtonEntity is null)
            {
                _logger.LogWarning("Button entity is not configured properly.");
                continue;
            }
            cfg.DeviceId = await _apiManager.GetDeviceId(cfg.ButtonEntity, cancellationToken);

            _ha.Events.Filter<ZhaEventData>("zha_event")
                .Where(e => e.Data?.DeviceId == cfg.DeviceId)
                .Subscribe(e =>
                {
                    switch (e.Data?.Command)
                    {
                        case "single":
                            if (cfg.TapAction is not null && cfg.TapDevice is not null)
                            {
                                cfg.TapDevice.CallService(cfg.TapAction);
                            }
                            break;
                        case "double":
                            if (cfg.DoubleTapAction is not null && cfg.DoubleTapDevice is not null)
                            {
                                cfg.DoubleTapDevice.CallService(cfg.DoubleTapAction);
                            }
                            break;
                        case "hold":
                            if (cfg.HoldAction is not null && cfg.HoldDevice is not null)
                            {
                                cfg.HoldDevice.CallService(cfg.HoldAction);
                            }
                            break;
                        default:
                            _logger.LogInformation("Unhandled command {Command} from button {Button}", e.Data?.Command, cfg.ButtonEntity?.EntityId);
                            break;
                    }
                });
        }
    }
}

record ZhaEventData
{
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
}

public class SmartThingsButtonConfig
{
    public BinarySensorEntity? ButtonEntity { get; set; }
    public string? DeviceId { get; set; }
    public string? TapAction { get; set; }
    public Entity? TapDevice { get; set; }
    public string? HoldAction { get; set; }
    public Entity? HoldDevice { get; set; }
    public string? DoubleTapAction { get; set; }
    public Entity? DoubleTapDevice { get; set; }
}