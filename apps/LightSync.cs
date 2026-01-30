using System.Collections.Generic;

namespace HomeAssistantApps;

[NetDaemonApp]
public class LightSync
{
    public LightSync(ILogger<LightSync> logger, LightEntities entities)
    {
        var config = new List<LightSyncConfig>
        {
            new() {
                Entity = entities.OfficeBulbs,
                LinkedEntity = entities.OfficeLamp
            }
        };
        foreach (var cfg in config)
        {
            cfg.Entity
                .StateChanges()
                .Subscribe(s =>
                {
                    if (s.New?.State == "on")
                    {
                        logger.LogInformation("Syncing {LinkedEntity} to ON because {Entity} turned ON", cfg.LinkedEntity.EntityId, cfg.Entity.EntityId);
                        cfg.LinkedEntity.TurnOn();
                    }
                    else if (s.New?.State == "off")
                    {
                        logger.LogInformation("Syncing {LinkedEntity} to OFF because {Entity} turned OFF", cfg.LinkedEntity.EntityId, cfg.Entity.EntityId);
                        cfg.LinkedEntity.TurnOff();
                    }
                });
        }
    }
}

public class LightSyncConfig
{
    public required LightEntity Entity { get; set; }
    public required LightEntity LinkedEntity { get; set; }
}