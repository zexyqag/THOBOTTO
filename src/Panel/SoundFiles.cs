using THOBOTTO.Sounds;

namespace THOBOTTO.Panel;

// A sound's file, for hearing it on the Sounds page.
public static class SoundFiles
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long id, PanelAccess access, SoundBoard board)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await board.FileAsync(guildId, id) is not { } sound)
            return Results.NotFound();
        var type = Path.GetExtension(sound.FileName).ToLowerInvariant() switch { ".ogg" => "audio/ogg", ".wav" => "audio/wav", _ => "audio/mpeg" };
        return Results.File(sound.Audio, type);
    }
}
