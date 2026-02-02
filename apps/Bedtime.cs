using System.Collections.Generic;
using System.Linq;

namespace HomeAssistantApps;

[NetDaemonApp]
public class Bedtime
{
    public Bedtime(ILogger<Bedtime> logger, IScheduler scheduler, Entities entities)
    {
        var filename = "http://192.168.1.7:8754/Rain_Sounds_14_Hours.mp3";
        var bedtimeConfigs = new List<BedtimeConfig>
        {
            new()
            {
                Entity = entities.InputBoolean.WhiteNoiseMorgan,
                LinkedMediaPlayer = entities.MediaPlayer.MorgansRoomSpeaker,
                BedtimeEntity = entities.InputBoolean.MorganBed,
                Light = entities.Light.MorganSFunLampGroup
            },
            new()
            {
                Entity = entities.InputBoolean.WhiteNoiseMarcy,
                LinkedMediaPlayer = entities.MediaPlayer.MarcysRoomSpeaker,
                BedtimeEntity = entities.InputBoolean.MarcyBed,
                Light = entities.Light.MarcySFunLampGroup
            },
            new()
            {
                Entity = entities.InputBoolean.WhiteNoise,
                LinkedMediaPlayer = entities.MediaPlayer.MasterBedroomSpeaker
            }
        };

        foreach (var cfg in bedtimeConfigs)
        {
            IDisposable? mediaPlayerSubscription = null;
            cfg.Entity
                .StateChanges()
                .Subscribe(s =>
                {
                    if (s.New?.State == "on")
                    {
                        logger.LogInformation("Starting white noise for {Entity}", cfg.Entity.EntityId);
                        cfg.LinkedMediaPlayer.PlayMedia(new
                        {
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

            cfg.BedtimeEntity?.StateChanges()
                    .Where(s => s.New?.State == "on")
                    .Subscribe(_ =>
                    {
                        logger.LogInformation("Bedtime started for {Entity}", cfg.BedtimeEntity.EntityId);
                        cfg.Entity.TurnOn();
                        cfg.Light?.TurnOff();
                    });
        }
    }
}

public class BedtimeConfig
{
    public required InputBooleanEntity Entity { get; set; }
    public required MediaPlayerEntity LinkedMediaPlayer { get; set; }
    public InputBooleanEntity? BedtimeEntity { get; set; }
    public LightEntity? Light { get; set; }
}