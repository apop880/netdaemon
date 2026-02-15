using System.Collections.Generic;

namespace HomeAssistantApps;

[NetDaemonApp]
public class Presence
{
    public Presence(ILogger<Presence> logger, Entities entities, Services services, Notify notify, HomeMode homeMode, IConfiguration configuration)
    {
        // Logic for tracking presence based on WiFi connection
        var homeSsid = configuration["HomeSSID"] ?? throw new InvalidOperationException("HomeSSID not found in configuration.");
        var config = new List<PresenceConfig>
        {
            new()
            {
                Entity = entities.Sensor.Cph2655WiFiConnection,
                Topic = "location/alex"
            },
            new()
            {
                Entity = entities.Sensor.Pixel8ProWifiConnection,
                Topic = "location/julie"
            }
        };
        foreach (var cfg in config)
        {
            cfg.Entity
                .StateChangesWithCurrent()
                .Subscribe(s =>
            {
                services.Mqtt.Publish(new()
                {
                    Topic = cfg.Topic,
                    Payload = s.New?.State == homeSsid ? "home" : "not_home",
                    Retain = true
                });
            });
        }

        // Logic for mutually exclusive vcation mode and guest mode
        entities.InputBoolean.VacationMode.StateChanges()
            .Where(s => s.New?.State == "on" && entities.InputBoolean.GuestMode.IsOn())
            .Subscribe(_ =>
            {
                entities.InputBoolean.GuestMode.TurnOff();
            });
        entities.InputBoolean.GuestMode.StateChanges()
            .Where(s => s.New?.State == "on" && entities.InputBoolean.VacationMode.IsOn())
            .Subscribe(_ =>
            {
                entities.InputBoolean.VacationMode.TurnOff();
            });

        // Logic for tracking if anyone is home based on zone state/vacation mode/guest mode
        homeMode.Current
            .Skip(1)
            .Subscribe(mode =>
            {
                notify.All($"Mode set to {mode.ToString().ToLowerInvariant()}");
            });
    }
}

public class PresenceConfig
{
    public required SensorEntity Entity { get; set; }
    public required string Topic { get; set; }
}