using System.Collections.Generic;

namespace HomeAssistantApps;

[NetDaemonApp]
public class HarmonyAutomation
{
    private const string PowerOffActivity = "power_off";
    private const string AppleTvActivity = "Watch Apple TV";
    private const string ScreenDeviceName = "Elite Screens Home Appliance";
    private const string ScreenUpCommand = "DirectionUp";
    private const string ScreenDownCommand = "DirectionDown";
    private const string ProjectorDeviceName = "Epson Projector";
    private const string ProjectorPowerOffCommand = "PowerOff";
    private const long ProjectorPowerOffRepeats = 2;
    private const double ProjectorPowerOffRepeatDelaySecs = 2;

    private static readonly HashSet<string> ScreenUpActivities = ["Listen to Music"];
    private static readonly TimeSpan ScreenUpDebounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProjectorPowerOffDelay = TimeSpan.FromSeconds(15);

    private readonly ILogger<HarmonyAutomation> _logger;
    private readonly RemoteEntity _harmonyRemote;
    private readonly SelectEntity _harmonyActivities;
    private readonly MediaPlayerEntity _appleTv;

    public HarmonyAutomation(ILogger<HarmonyAutomation> logger, IScheduler scheduler, Entities entities)
    {
        _logger = logger;
        _harmonyRemote = entities.Remote.HarmonyHub;
        _harmonyActivities = entities.Select.HarmonyHubActivities;
        _appleTv = entities.MediaPlayer.BasementBasement;

        WatchAppleTv();
        WatchProjectorScreen(scheduler);
    }

    private void WatchAppleTv()
    {
        _appleTv.StateChanges()
            .Subscribe(s =>
            {
                if (s.Old?.State == "off" && s.New?.State is "on" or "idle" or "paused")
                {
                    var current = _harmonyActivities.State;
                    if (current != AppleTvActivity)
                    {
                        _logger.LogInformation("Apple TV changed from off to {NewState}, starting Harmony Apple TV activity (was {Current})", s.New?.State, current);
                        _harmonyActivities.SelectOption(AppleTvActivity);
                    }
                }
                else if (s.Old?.State is "on" or "idle" or "paused" && s.New?.State == "off")
                {
                    var currentActivity = _harmonyActivities.State;
                    if (currentActivity == AppleTvActivity)
                    {
                        _logger.LogInformation("Apple TV turned off while on Harmony Apple TV activity, powering off");
                        _harmonyActivities.SelectOption(PowerOffActivity);
                    }
                    else
                    {
                        _logger.LogInformation("Apple TV turned off but Harmony activity is {CurrentActivity}, not powering off", currentActivity);
                    }
                }
            });
    }

    private void WatchProjectorScreen(IScheduler scheduler)
    {
        _harmonyActivities.StateChanges()
            .Where(s => s.Old?.State is { } previous && previous != s.New?.State && s.New?.State != PowerOffActivity)
            .Subscribe(s => OnActivityChanged(s.Old!.State!, s.New!.State!));

        _harmonyActivities.StateChanges()
            .Where(s => s.Old?.State is not null)
            .WhenStateIsFor(s => s?.State == PowerOffActivity, ScreenUpDebounce, scheduler)
            .Subscribe(s => OnActivityChanged(s.Old!.State!, s.New!.State!));

        _harmonyActivities.StateChanges()
            .Where(s => s.Old?.State is not null)
            .WhenStateIsFor(s => s?.State == PowerOffActivity, ProjectorPowerOffDelay, scheduler)
            .Subscribe(_ => SendProjectorPowerOff());
    }

    private void OnActivityChanged(string previous, string current)
    {
        if (current == PowerOffActivity)
        {
            _logger.LogInformation("Activity changed from {Previous} to power_off, raising the projector screen", previous);
            SendScreenCommand(ScreenUpCommand);
            return;
        }

        if (ScreenUpActivities.Contains(current))
        {
            _logger.LogInformation("Activity changed from {Previous} to {Current}, leaving the projector screen up", previous, current);
            return;
        }

        if (previous == PowerOffActivity || ScreenUpActivities.Contains(previous))
        {
            _logger.LogInformation("Activity changed from {Previous} to {Current}, lowering the projector screen", previous, current);
            SendScreenCommand(ScreenDownCommand);
            return;
        }

        _logger.LogInformation("Activity changed from {Previous} to {Current}, projector screen already down", previous, current);
    }

    private void SendProjectorPowerOff()
    {
        _logger.LogInformation(
            "Sending {Command} to {Device} {Repeats} times after power_off",
            ProjectorPowerOffCommand,
            ProjectorDeviceName,
            ProjectorPowerOffRepeats);
        _harmonyRemote.SendCommand(new RemoteSendCommandParameters
        {
            Device = ProjectorDeviceName,
            Command = ProjectorPowerOffCommand,
            NumRepeats = ProjectorPowerOffRepeats,
            DelaySecs = ProjectorPowerOffRepeatDelaySecs
        });
    }

    private void SendScreenCommand(string command)
    {
        _logger.LogInformation("Sending {Command} to {Device}", command, ScreenDeviceName);
        _harmonyRemote.SendCommand(new RemoteSendCommandParameters
        {
            Device = ScreenDeviceName,
            Command = command
        });
    }
}