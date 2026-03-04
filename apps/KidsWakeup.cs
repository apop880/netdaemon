using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace HomeAssistantApps;

[NetDaemonApp]
public class KidsWakeup
{

    public KidsWakeup(ILogger<KidsWakeup> logger, IHaContext ha, Entities entities, Notify notify, IConfiguration configuration, IHostEnvironment environment)
    {
        var audioConfigByKey = LoadAudioConfig(logger, configuration, environment);

        var config = new List<KidsWakeupConfig>
        {
            new() {
                Entity = entities.InputBoolean.MorganWakeup,
                LinkedBedtime = entities.InputBoolean.MorganBed,
                LinkedMediaPlayer = entities.MediaPlayer.MorgansRoomSpeaker,
                AudioConfig = GetAudioConfig(audioConfigByKey, "MorganWakeup", logger)
            },
            new() {
                Entity = entities.InputBoolean.MarcyWakeup,
                LinkedBedtime = entities.InputBoolean.MarcyBed,
                LinkedMediaPlayer = entities.MediaPlayer.MarcysRoomSpeaker,
                AudioConfig = GetAudioConfig(audioConfigByKey, "MarcyWakeup", logger)
            }
        };

        foreach (var cfg in config)
        {
            if (cfg.Entity is null || cfg.LinkedBedtime is null || cfg.LinkedMediaPlayer is null)
            {
                logger.LogWarning("Wakeup group is not configured properly.");
                continue;
            }

            // Listen for media player state changes to play next file
            IDisposable? mediaPlayerSubscription = null;

            cfg.Entity
                .StateChanges()    
                .SubscribeAsync(async e =>
                {
                    if (e.New?.State != "on")
                    {
                        cfg.LinkedMediaPlayer.MediaStop();
                        mediaPlayerSubscription?.Dispose();
                        cfg.LinkedMediaPlayer.VolumeSet(0.2);
                        notify.Clear(cfg.Entity.EntityId);
                        return;
                    }
                    var actionId = $"TURN_OFF_{cfg.Entity.EntityId}";
                    notify.All(
                        $"{e.Entity.Attributes?.FriendlyName} is on",
                        tag: cfg.Entity.EntityId,
                        actions: [new NotifyAction(actionId, "Turn Off")]
                    );

                    cfg.LinkedBedtime.TurnOff();
                    cfg.LinkedMediaPlayer.VolumeSet(0.5);

                    var selectedAudioFiles = new List<string>();
                    if (!string.IsNullOrWhiteSpace(cfg.AudioConfig.FirstAudioFile))
                    {
                        selectedAudioFiles.Add(cfg.AudioConfig.FirstAudioFile);
                    }

                    var remainingAudioFiles = cfg.AudioConfig.AdditionalAudioFiles
                        .Where(file => !string.Equals(file, cfg.AudioConfig.FirstAudioFile, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (remainingAudioFiles.Count > 0)
                    {
                        var secondTrack = remainingAudioFiles[Random.Shared.Next(remainingAudioFiles.Count)];
                        selectedAudioFiles.Add(secondTrack);
                    }

                    if (selectedAudioFiles.Count == 0)
                    {
                        logger.LogWarning("No audio files configured for {Entity}", cfg.Entity.EntityId);
                        cfg.Entity.TurnOff();
                        return;
                    }
                    
                    await Task.Delay(3000);
                    
                    // Start with first file
                    int currentFileIndex = 0;
                    cfg.LinkedMediaPlayer.PlayMedia(new {
                        media_content_type = "music",
                        media_content_id = selectedAudioFiles[currentFileIndex]
                    });

                    mediaPlayerSubscription = cfg.LinkedMediaPlayer.StateChanges()
                        // Only advance when playback actually went to "idle" (end of track)
                        .Where(state => state.Old?.State == "playing" && state.New?.State == "idle")
                        .Subscribe(_ =>
                        {
                            currentFileIndex++;
                            
                            if (currentFileIndex < selectedAudioFiles.Count)
                            {
                                cfg.LinkedMediaPlayer.PlayMedia(new {
                                    media_content_type = "music",
                                    media_content_id = selectedAudioFiles[currentFileIndex]
                                });
                            }
                            else
                            {
                                cfg.LinkedMediaPlayer.VolumeSet(0.2);
                                mediaPlayerSubscription?.Dispose();
                                cfg.Entity.TurnOff();
                            }
                        });
                });

            // Listen for "Turn Off" action from the notification
            var turnOffActionId = $"TURN_OFF_{cfg.Entity.EntityId}";
            Notify.OnAction(ha, turnOffActionId, () =>
            {
                logger.LogInformation("Turning off {Entity} via notification action", cfg.Entity.EntityId);
                cfg.Entity!.TurnOff();
            });
        }
    }

    private static Dictionary<string, KidsWakeupAudioConfig> LoadAudioConfig(ILogger<KidsWakeup> logger, IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration.GetValue("KidsWakeup:AudioConfigPath", "kidswakeup.audio.json");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            logger.LogWarning("KidsWakeup audio config path is missing.");
            return [];
        }

        var fullPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!File.Exists(fullPath))
        {
            logger.LogWarning("KidsWakeup audio config file not found at {Path}", fullPath);
            return [];
        }

        try
        {
            var json = File.ReadAllText(fullPath);
            var audioConfig = JsonSerializer.Deserialize<Dictionary<string, KidsWakeupAudioConfig>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return audioConfig ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load KidsWakeup audio config from {Path}", fullPath);
            return [];
        }
    }

    private static KidsWakeupAudioConfig GetAudioConfig(Dictionary<string, KidsWakeupAudioConfig> audioConfigByKey, string key, ILogger<KidsWakeup> logger)
    {
        if (audioConfigByKey.TryGetValue(key, out var audioConfig))
        {
            return audioConfig;
        }

        logger.LogWarning("KidsWakeup audio config is missing key {Key}", key);
        return new KidsWakeupAudioConfig
        {
            FirstAudioFile = string.Empty,
            AdditionalAudioFiles = []
        };
    }
}

public class KidsWakeupConfig
{
    public InputBooleanEntity? Entity { get; set; }
    public MediaPlayerEntity? LinkedMediaPlayer { get; set; }
    public InputBooleanEntity? LinkedBedtime { get; set; }
    public required KidsWakeupAudioConfig AudioConfig { get; set; }
}

public class KidsWakeupAudioConfig
{
    public required string FirstAudioFile { get; set; }
    public required List<string> AdditionalAudioFiles { get; set; }
}
