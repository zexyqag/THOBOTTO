using THOBOTTO.Discord;

namespace THOBOTTO.Sounds;

// What a look at Discord's soundboard means for the library: linked sounds gone there, Discord sounds the library
// has no link for (to link to a sound of the same name and length, or to import), and names changed on Discord.
public sealed record SyncPlan(IReadOnlyList<long> TakenOff, IReadOnlyList<SoundboardSound> Unknown, IReadOnlyList<(long SoundId, string Name)> Renames);

public static class SoundSync
{
    // Discord re-encodes what it's given, so lengths are compared with a little room rather than the bytes.
    public const int LengthSlack = 300;

    public static SyncPlan Plan(IReadOnlyList<SoundboardSound> remote, IReadOnlyList<Sound> rows)
    {
        var remoteIds = remote.Select(r => r.Id).ToHashSet();
        var takenOff = rows.Where(s => s.State == SoundStates.Library && s.DiscordId is { } id && !remoteIds.Contains(id)).Select(s => s.Id).ToList();
        // Links on removed and rejected sounds count too: a sound taken out of the library doesn't come back by import.
        var known = rows.Where(s => s.DiscordId is not null).Select(s => s.DiscordId!.Value).ToHashSet();
        var unknown = remote.Where(r => !known.Contains(r.Id)).ToList();
        var inUse = rows.Where(s => s.State is SoundStates.Voting or SoundStates.Library).Select(s => s.Name.ToLowerInvariant()).ToHashSet();
        var renames = new List<(long, string)>();
        foreach (var sound in rows.Where(s => s.State == SoundStates.Library && s.DiscordId is not null))
        {
            if (remote.FirstOrDefault(r => r.Id == sound.DiscordId) is not { } linked || linked.Name == sound.Name || !SoundBoard.IsValidName(linked.Name))
                continue;
            var sameButCase = string.Equals(linked.Name, sound.Name, StringComparison.OrdinalIgnoreCase);
            if (sameButCase || !inUse.Contains(linked.Name.ToLowerInvariant()))
                renames.Add((sound.Id, linked.Name));
        }
        return new(takenOff, unknown, renames);
    }

    // A Discord sound with the name and about the length of an unlinked library sound is that sound.
    public static Sound? Match(string name, int milliseconds, IReadOnlyList<Sound> rows)
        => rows.FirstOrDefault(s => s.State == SoundStates.Library && s.DiscordId is null
            && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && Math.Abs(s.Milliseconds - milliseconds) <= LengthSlack);
}

// How the last look at Discord's soundboard went, for the page and the command.
public sealed record SyncReport(int Remote, int Slots, int Imported, int Linked, int Renamed, int TakenOff, bool NoAccess, DateTimeOffset At)
{
    public string Describe()
    {
        if (NoAccess)
            return "Discord didn't list its soundboard: I need the Manage Expressions permission.";
        var changes = new List<string>();
        if (Imported > 0)
            changes.Add($"{Imported} imported");
        if (Linked > 0)
            changes.Add($"{Linked} linked to a sound already here");
        if (Renamed > 0)
            changes.Add($"{Renamed} renamed as on Discord");
        if (TakenOff > 0)
            changes.Add($"{TakenOff} taken off there, kept here");
        return $"{Remote} of {Slots} Discord soundboard slots used; {(changes.Count == 0 ? "in step" : string.Join(", ", changes))}.";
    }
}
