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
        var audioFiles = new List<string>
        {
            "http://192.168.1.7:8754/LionKing.mp3",
            "http://192.168.1.7:8754/DannyGoWakeUp.mp3"
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
                    cfg.LinkedMediaPlayer.VolumeSet(0.5);
                    
                    await Task.Delay(3000);
                    
                    // Start with first file
                    int currentFileIndex = 0;
                    cfg.LinkedMediaPlayer.PlayMedia(new {
                        media_content_type = "music",
                        media_content_id = audioFiles[currentFileIndex]
                    });

                    // Listen for media player state changes to play next file (only during this wakeup)
                    IDisposable? mediaPlayerSubscription = null;
                    mediaPlayerSubscription = cfg.LinkedMediaPlayer.StateChanges()
                        .Where(state => state.Old?.State == "playing" && state.New?.State != "playing")
                        .Subscribe(_ =>
                        {
                            currentFileIndex++;
                            
                            if (currentFileIndex < audioFiles.Count)
                            {
                                cfg.LinkedMediaPlayer.PlayMedia(new {
                                    media_content_type = "music",
                                    media_content_id = audioFiles[currentFileIndex]
                                });
                            }
                            else
                            {
                                cfg.LinkedMediaPlayer.VolumeSet(0.2);
                                mediaPlayerSubscription?.Dispose();
                            }
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