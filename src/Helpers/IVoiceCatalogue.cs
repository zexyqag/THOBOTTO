namespace THOBOTTO.Helpers;

// The voices a personality can be given, for picking one: the speech engine's, as speaking lists them.
public interface IVoiceCatalogue
{
    // Whether helpers speak at all (an engine is picked).
    bool On { get; }

    // The engine's name; each engine keeps its own pick per personality.
    string Engine { get; }

    // What a personality without a pick for this engine speaks with.
    string DefaultVoice { get; }

    // The engine's voices; empty when it can't be asked.
    Task<IReadOnlyList<string>> VoicesAsync();

    // Where a personality says its hello in a voice (the one asked for in the query, else its own), to listen before picking.
    string PreviewLink(ulong guildId, long personalityId);
}
