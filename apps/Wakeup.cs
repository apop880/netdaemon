using System.Collections.Generic;
using System.Threading.Tasks;

namespace HomeAssistantApps;

[NetDaemonApp]
public class Wakeup
{

    public Wakeup(ILogger<Wakeup> logger, Entities entities)
    {
        var config = new List<WakeupConfig>
        {
            new() {
                Entity = entities.InputButton.MorganWakeup,
                LinkedLight = entities.Light.MorganFun,
                LinkedMediaPlayer = entities.MediaPlayer.MorgansRoomSpeaker,
                LinkedWhiteNoise = entities.InputBoolean.WhiteNoiseMorgan
            },
            new() {
                Entity = entities.InputButton.MarcyWakeup,
                LinkedLight = entities.Light.MarcySFunLampGroup,
                LinkedMediaPlayer = entities.MediaPlayer.MarcysRoomSpeaker,
                LinkedWhiteNoise = entities.InputBoolean.WhiteNoiseMarcy
            },
        };

        foreach (var cfg in config)
        {
            if (cfg.Entity is null || cfg.LinkedLight is null || cfg.LinkedMediaPlayer is null)
            {
                logger.LogWarning("Wakeup group is not configured properly.");
                continue;
            }

            cfg.Entity
                .StateChanges()
                .SubscribeAsync(async _ =>
                {
                    cfg.LinkedLight.TurnOn();
                    cfg.LinkedWhiteNoise?.TurnOff();
                    
                    await Task.Delay(3000);                    
                    cfg.LinkedMediaPlayer.PlayMedia(new {
                            media_content_type = "music",
                            media_content_id = "http://192.168.1.7:8754/LionKing.mp3"
                    });
                    
                    await Task.Delay(3000);                    
                    cfg.LinkedMediaPlayer.PlayMedia(new {
                            media_content_type = "music",
                            media_content_id = "http://192.168.1.7:8754/DannyGoWakeUp.mp3"
                    });
                });
        }
    }
}

public class WakeupConfig
{
    public InputButtonEntity? Entity { get; set; }
    public LightEntity? LinkedLight { get; set; }
    public MediaPlayerEntity? LinkedMediaPlayer { get; set; }
    public InputBooleanEntity? LinkedWhiteNoise { get; set; }
}