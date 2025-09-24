using System.Collections.Generic;

namespace HomeAssistantApps;

[NetDaemonApp]
public class ShellySmartBulbs
{

    public ShellySmartBulbs(ILogger<ShellySmartBulbs> logger, IHaContext ha, Entities entities)
    {
        var config = new List<ShellyConfig>
        {
            new() {
                Entity = entities.BinarySensor.TheaterShellyInput,
                LinkedEntity = entities.Light.Theater
            }
        };

        foreach (var cfg in config)
        {
            if (cfg.Entity is null || cfg.LinkedEntity is null)
            {
                logger.LogWarning("Shelly entity is not configured properly.");
                continue;
            }

            cfg.Entity
                .StateChanges()
                .Subscribe(_ =>
                {
                    cfg.LinkedEntity.Toggle();
                });
        }
    }
}

public class ShellyConfig
{
    public BinarySensorEntity? Entity { get; set; }
    public LightEntity? LinkedEntity { get; set; }
}