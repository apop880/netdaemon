using System.Collections.Generic;

namespace HomeAssistantApps;

[NetDaemonApp]
public class WhiteNoise
{
    public WhiteNoise(ILogger<WhiteNoise> logger, IScheduler scheduler, Entities entities)
    {
        var filename = "http://192.168.1.7:8754/Rain_Sounds_14_Hours.mp3";
        var config = new List<WhiteNoiseConfig>
        {
            new() {
                Entity = entities.InputBoolean.WhiteNoiseMorgan,
                LinkedMediaPlayer = entities.MediaPlayer.MorgansRoomSpeaker
            },
            new() {
                Entity = entities.InputBoolean.WhiteNoiseMarcy,
                LinkedMediaPlayer = entities.MediaPlayer.MarcysRoomSpeaker
            },
            new() {
                Entity = entities.InputBoolean.WhiteNoise,
                LinkedMediaPlayer = entities.MediaPlayer.MasterBedroomSpeaker
            }
        };

        foreach (var cfg in config)
        {
            IDisposable? mediaPlayerSubscription = null;
            cfg.Entity
                .StateChanges()
                .Subscribe(s =>
                {
                    if (s.New?.State == "on")
                    {
                        logger.LogInformation("Starting white noise for {Entity}", cfg.Entity.EntityId);
                        cfg.LinkedMediaPlayer.PlayMedia(new {
                            media_content_type = "music",
                            media_content_id = filename
                        });
                        cfg.LinkedMediaPlayer.VolumeSet(0.2);
                        mediaPlayerSubscription = cfg.LinkedMediaPlayer.StateChanges()
                            .WhenStateIsFor(s => s?.State != "playing", TimeSpan.FromSeconds(30), scheduler)
                            .Subscribe(_ => cfg.Entity.TurnOff());
                    }
                    else if (s.New?.State == "off")
                    {
                        logger.LogInformation("Stopping white noise for {Entity}", cfg.Entity.EntityId);
                        cfg.LinkedMediaPlayer.MediaStop();
                        mediaPlayerSubscription?.Dispose();
                    }
                });
        }
    }
        
}

public class WhiteNoiseConfig
{
    public required InputBooleanEntity Entity { get; set; }
    public required MediaPlayerEntity LinkedMediaPlayer { get; set; }
}