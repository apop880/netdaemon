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
                LinkedBedtime = entities.InputBoolean.MorganBed,
                LinkedMediaPlayer = entities.MediaPlayer.MorgansRoomSpeaker,
                AudioFiles =
                [
                    "http://192.168.1.7:8754/LionKing.mp3",
                    "http://192.168.1.7:8754/can_you_feel_the_love_tonight.mp3"
                ]
            },
            new() {
                Entity = entities.InputButton.MarcyWakeup,
                LinkedBedtime = entities.InputBoolean.MarcyBed,
                LinkedMediaPlayer = entities.MediaPlayer.MarcysRoomSpeaker,
                AudioFiles =
                [
                    "http://192.168.1.7:8754/LionKing.mp3",
                    "http://192.168.1.7:8754/DannyGoWakeUp.mp3"
                ]
            }
        };

        foreach (var cfg in config)
        {
            if (cfg.Entity is null || cfg.LinkedBedtime is null || cfg.LinkedMediaPlayer is null)
            {
                logger.LogWarning("Wakeup group is not configured properly.");
                continue;
            }

            cfg.Entity
                .StateChanges()
                .SubscribeAsync(async _ =>
                {
                    cfg.LinkedBedtime.TurnOff();
                    cfg.LinkedMediaPlayer.VolumeSet(0.5);
                    
                    await Task.Delay(3000);
                    
                    // Start with first file
                    int currentFileIndex = 0;
                    cfg.LinkedMediaPlayer.PlayMedia(new {
                        media_content_type = "music",
                        media_content_id = cfg.AudioFiles[currentFileIndex]
                    });

                    // Listen for media player state changes to play next file (only during this wakeup)
                    IDisposable? mediaPlayerSubscription = null;
                    mediaPlayerSubscription = cfg.LinkedMediaPlayer.StateChanges()
                        // Only advance when playback actually went to "idle" (end of track)
                        .Where(state => state.Old?.State == "playing" && state.New?.State == "idle")
                        .Subscribe(_ =>
                        {
                            currentFileIndex++;
                            
                            if (currentFileIndex < cfg.AudioFiles.Count)
                            {
                                cfg.LinkedMediaPlayer.PlayMedia(new {
                                    media_content_type = "music",
                                    media_content_id = cfg.AudioFiles[currentFileIndex]
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
    public MediaPlayerEntity? LinkedMediaPlayer { get; set; }
    public InputBooleanEntity? LinkedBedtime { get; set; }
    public required List<string> AudioFiles { get; set; }
}