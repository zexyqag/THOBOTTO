using System.Globalization;
using System.Text.RegularExpressions;

using THOBOTTO.Helpers;
using THOBOTTO.Panel;

namespace THOBOTTO.Voice.Speaking;

// A personality saying a line, for the personality page: its hello, or the line, engine, voice and effects the
// page has right now (unsaved), so what's being edited can be heard. Never cached, since the same address plays
// something else once the personality changed.
public static partial class VoicePreview
{
    private const int MostCharacters = 300;

    public static string Link(ulong guildId, long personalityId) => $"/g/{guildId}/helpers/{personalityId}/voice";

    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long id, string? voice, PanelAccess access, PersonalityBook book, HelperVoices voices)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await book.FindAsync(guildId, id) is not { } personality || !voices.On)
            return Results.NotFound();
        var query = context.Request.Query;
        var line = query["line"].ToString() is { Length: > 0 } typed ? typed[..Math.Min(typed.Length, MostCharacters)]
            : (personality.Phrases.GetValueOrDefault(Moments.Joined) ?? PersonalityFile.Plain.Lines[Moments.Joined]).FirstOrDefault() ?? "Hello.";
        line = line.Replace("{channel}", "General").Replace("{user}", "Ana").Replace("{track}", "Thunderstruck").Replace("{helper}", personality.Name);
        var wanted = query["engine"].ToString() is { Length: > 0 } picked && PersonalityFile.Engines.Contains(picked) ? picked : personality.Engine;
        var engine = HelperVoices.Choose(wanted, voices.Engine);
        var chosen = await voices.UsableVoiceAsync(engine, voice is { Length: > 0 } && VoiceName().IsMatch(voice) ? voice : personality.Voices.GetValueOrDefault(engine) ?? voices.DefaultVoice(engine));
        var effects = Effects(query["effects"].ToString()) ?? VoiceEffects.From(personality.Effects);
        context.Response.Headers.CacheControl = "no-store";
        return await voices.SayAsync(line, engine, chosen, effects, context.RequestAborted) is { } spoken
            ? Results.File(Pcm.Wav(spoken), "audio/wav")
            : Results.NotFound();
    }

    // "pitch:12,static:0.2" as the page sends its fields; null when it sent none, or something that isn't an effect.
    public static VoiceEffects? Effects(string query)
    {
        if (query.Length == 0)
            return null;
        var values = new Dictionary<string, double>();
        foreach (var pair in query.Split(','))
        {
            if (pair.Split(':') is not [var name, var text] || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;
            values[name] = value;
        }
        return VoiceEffects.Problem(values) is null ? VoiceEffects.From(values) : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:+()-]{1,64}$")]
    private static partial Regex VoiceName();
}
