namespace HomeAssistantApps;

[NetDaemonApp]
public class AppleTvHarmony
{
    public AppleTvHarmony(ILogger<AppleTvHarmony> logger, Entities entities)
    {
        var appleTv = entities.MediaPlayer.BasementBasement;
        var harmony = entities.Remote.HarmonyHub;

        appleTv.StateChanges()
            .Subscribe(s =>
            {
                if (s.Old?.State == "off" && s.New?.State != "off")
                {
                    var current = harmony.Attributes?.CurrentActivity;
                    if (current != "Watch Apple TV")
                    {
                        logger.LogInformation("Apple TV changed from off to {NewState}, starting Harmony Apple TV activity (was {Current})", s.New?.State, current);
                        harmony.TurnOn(activity: "Watch Apple TV");
                    }
                }
                else if (s.Old?.State != "off" && s.New?.State == "off")
                {
                    var currentActivity = harmony.Attributes?.CurrentActivity;
                    if (currentActivity == "Watch Apple TV")
                    {
                        logger.LogInformation("Apple TV turned off while on Harmony Apple TV activity, powering off");
                        harmony.TurnOff();
                    }
                    else
                    {
                        logger.LogInformation("Apple TV turned off but Harmony activity is {CurrentActivity}, not powering off", currentActivity);
                    }
                }
            });
    }
}
