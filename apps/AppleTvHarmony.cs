namespace HomeAssistantApps;

[NetDaemonApp]
public class AppleTvHarmony
{
    public AppleTvHarmony(ILogger<AppleTvHarmony> logger, Entities entities)
    {
        var appleTv = entities.MediaPlayer.BasementBasement;
        var harmonyActivities = entities.Select.HarmonyHubActivities;

        appleTv.StateChanges()
            .Subscribe(s =>
            {
                if (s.Old?.State == "off" && s.New?.State is "on" or "idle" or "paused")
                {
                    var current = harmonyActivities.State;
                    if (current != "Watch Apple TV")
                    {
                        logger.LogInformation("Apple TV changed from off to {NewState}, starting Harmony Apple TV activity (was {Current})", s.New?.State, current);
                        harmonyActivities.SelectOption("Watch Apple TV");
                    }
                }
                else if (s.Old?.State is "on" or "idle" or "paused" && s.New?.State == "off")
                {
                    var currentActivity = harmonyActivities.State;
                    if (currentActivity == "Watch Apple TV")
                    {
                        logger.LogInformation("Apple TV turned off while on Harmony Apple TV activity, powering off");
                        harmonyActivities.SelectOption("Power Off");
                    }
                    else
                    {
                        logger.LogInformation("Apple TV turned off but Harmony activity is {CurrentActivity}, not powering off", currentActivity);
                    }
                }
            });
    }
}
