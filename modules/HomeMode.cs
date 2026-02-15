using System.Reactive.Subjects;

namespace HomeAssistantApps.modules;

public enum HomeModeState
{
    Home,
    Vacation,
    Guest,
    Away
}

public class HomeMode
{
    private readonly BehaviorSubject<HomeModeState> _subject;

    public IObservable<HomeModeState> Current { get; }

    public HomeModeState CurrentValue => _subject.Value;

    public HomeMode(Entities entities)
    {
        _subject = new BehaviorSubject<HomeModeState>(Evaluate(entities));

        // Re-evaluate whenever any of the three inputs change
        var zoneChanges = entities.Zone.Home.StateChanges().Select(_ => Evaluate(entities));
        var vacationChanges = entities.InputBoolean.VacationMode.StateChanges().Select(_ => Evaluate(entities));
        var guestChanges = entities.InputBoolean.GuestMode.StateChanges().Select(_ => Evaluate(entities));

        zoneChanges
            .Merge(vacationChanges)
            .Merge(guestChanges)
            .DistinctUntilChanged()
            .Subscribe(_subject);

        Current = _subject.DistinctUntilChanged().Skip(1);
    }

    private static HomeModeState Evaluate(Entities entities)
    {
        if (int.TryParse(entities.Zone.Home.State, out var zoneCount) && zoneCount > 0)
            return HomeModeState.Home;
        if (entities.InputBoolean.VacationMode.IsOn())
            return HomeModeState.Vacation;
        if (entities.InputBoolean.GuestMode.IsOn())
            return HomeModeState.Guest;
        return HomeModeState.Away;
    }
}
