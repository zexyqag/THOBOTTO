namespace THOBOTTO.Helpers;

// What the helpers' work (playing music) lets the ears have: which helpers are taken, one given up to listen,
// and the music lowered where people talk.
public interface IHelperWork
{
    // Whether the helper works in that server.
    bool Busy(ulong helperId, ulong guildId);

    // A helper for listening in that channel, taken off its work elsewhere in the server when the server's rules
    // put listening first; null when none may be.
    Task<HelperBot?> YieldAsync(ulong guildId, ulong channelId);

    // Whether people talk in that channel right now, said on every tick while a helper hears it: the music there
    // goes a step toward lowered or back up, as the server's rules want.
    Task TalkingAsync(ulong guildId, ulong channelId, bool talking);
}
