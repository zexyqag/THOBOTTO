namespace THOBOTTO.Events;

// Lets modules built on events (games) add lines to an event's message, e.g. a game server's status.
public interface IEventDecorator
{
    Task<IReadOnlyList<string>> LinesAsync(Event e);
}
