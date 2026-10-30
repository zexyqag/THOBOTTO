namespace THOBOTTO.Helpers;

// The voices a personality can be given, for picking one: the speech engine's, as speaking lists them.
public interface IVoiceCatalogue
{
    // Whether helpers speak at all (an engine is picked).
    bool On { get; }

    // The server's engine; each engine keeps its own pick per personality.
    string Engine { get; }

    // The engines a personality may ask for here (the ones in the image, and the server's pick), with their names.
    IReadOnlyList<(string Id, string Name)> Engines { get; }

    string EngineName(string engine);

    // The engine a personality speaks through here: its preference when that's here, else the server's.
    string EngineFor(Personality? personality);

    string EngineFor(string? preferred);

    // An engine's variants, changing any of its voices after a plus ("en-us+m3"); most engines have none.
    IReadOnlyList<string> VariantsOf(string engine);

    // Whether an engine blends voices ("af_sky+af_bella", weighted "af_sky(2)+af_bella(1)").
    bool Blends(string engine);

    // What a personality without a pick for an engine speaks with.
    string DefaultVoice(string engine);

    // An engine's voices; empty when it can't be asked.
    Task<IReadOnlyList<string>> VoicesAsync(string engine);

    // Where a personality says its hello in a voice (the one asked for in the query, else its own), to listen before picking.
    string PreviewLink(ulong guildId, long personalityId);
}
