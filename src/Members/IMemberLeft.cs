namespace THOBOTTO.Members;

// A member left a server (or was kicked, or banned): whoever keeps a member's preferences for a server forgets
// that member's there. Records (points, quotes, plays, voice time, the archive, cases) are history and stay.
public interface IMemberLeft
{
    Task MemberLeftAsync(ulong guildId, ulong userId);
}
