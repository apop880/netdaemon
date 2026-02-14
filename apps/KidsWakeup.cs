using System.Collections.Generic;
using System.Threading.Tasks;

namespace HomeAssistantApps;

[NetDaemonApp]
public class KidsWakeup
{

    public KidsWakeup(ILogger<KidsWakeup> logger, IHaContext ha, Entities entities, Notify notify)
    {
        var config = new List<KidsWakeupConfig>
        {
            new() {
                Entity = entities.InputBoolean.MorganWakeup,
                LinkedBedtime = entities.InputBoolean.MorganBed,
                LinkedMediaPlayer = entities.MediaPlayer.MorgansRoomSpeaker,
                AudioFiles =
                [
                    "http://192.168.1.7:8754/LionKing.mp3",
                    "http://192.168.1.7:8754/can_you_feel_the_love_tonight.mp3"
                ]
            },
            new() {
                Entity = entities.InputBoolean.MarcyWakeup,
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
                        notify.All("clear_notification", tag: cfg.Entity.EntityId);
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
                    
                    await Task.Delay(3000);
                    
                    // Start with first file
                    int currentFileIndex = 0;
                    cfg.LinkedMediaPlayer.PlayMedia(new {
                        media_content_type = "music",
                        media_content_id = cfg.AudioFiles[currentFileIndex]
                    });

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
}

public class KidsWakeupConfig
{
    public InputBooleanEntity? Entity { get; set; }
    public MediaPlayerEntity? LinkedMediaPlayer { get; set; }
    public InputBooleanEntity? LinkedBedtime { get; set; }
    public required List<string> AudioFiles { get; set; }
}