using System.Collections.Generic;
using System.Threading;
using NetDaemon.Client;
using HomeAssistantApps.Extensions;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;

namespace HomeAssistantApps;

[NetDaemonApp]
public class Inovelli : IAsyncInitializable
{
    private readonly ILogger<Inovelli> _logger;
    private readonly IHaContext _ha;
    private readonly Entities _entities;
    private readonly Services _services;
    private readonly IHomeAssistantApiManager _apiManager;
    private readonly List<InovelliConfig> _config;

    public Inovelli(ILogger<Inovelli> logger, IHaContext ha, Entities entities, Services services, IHomeAssistantApiManager apiManager)
    {
        _logger = logger;
        _entities = entities;
        _apiManager = apiManager;
        _services = services;
        _ha = ha;

        _config =
        [
            new() {
                Entity = _entities.Light.TheaterLights,
                LinkedEntity = _entities.Light.Theater
            }
        ];
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var cfg in _config)
        {
            if (cfg.Entity is null || cfg.LinkedEntity is null)
            {
                _logger.LogWarning("Inovelli configuration has null entity or linked entity, skipping.");
                continue;
            }
            cfg.DeviceId = await _apiManager.GetDeviceId(cfg.Entity, cancellationToken);

            if (string.IsNullOrEmpty(cfg.DeviceId))
            {
                _logger.LogWarning("Could not determine DeviceId for entity {EntityId}, skipping.", cfg.Entity.EntityId);
                continue;
            }
            // Todo: Update the light bar based on the light state
            /*cfg.LinkedEntity.StateChanges()
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
                */

            _ha.Events.Filter<InovelliEventData>("zha_event")
                .Where(e => e.Data != null && e.Data.IsValid && e.Data.DeviceId == cfg.DeviceId)
                .Subscribe(e =>
                {
                    switch (e.Data!.PressType)
                    {
                        case "press":
                            cfg.LinkedEntity.Toggle();
                            break;
                        case "release":
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
                        case "hold":
                            if (cfg.LinkedEntity.IsOn())
                            {
                                cfg.Stopwatch = Stopwatch.StartNew(); // Start the stopwatch
                                cfg.Goal = e.Data.Button == "button_2" ? 255 : 1;
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

record InovelliEventData
{
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("args")] public JsonElement? Args { get; set; }

    public string Button
    {
        get
        {
            if (Args.HasValue && Args.Value.TryGetProperty("button", out var b))
            {
                return b.GetString() ?? "unknown";
            }
            return "unknown";
        }
    }

    public string PressType
    {
        get
        {
            if (Args.HasValue && Args.Value.TryGetProperty("press_type", out var p))
            {
                return p.GetString() ?? "unknown";
            }
            return "unknown";
        }
    }

    public bool IsValid
    {
        get
        {
            if (Args.HasValue && Args.Value.ValueKind == JsonValueKind.Array && Args.Value.GetArrayLength() == 0)
            {
                return false;
            }

            var isValid = (PressType is "hold" or "release" or "press") && (Button is "button_1" or "button_2");
            return isValid;
        }
    }
}

public class InovelliConfig
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