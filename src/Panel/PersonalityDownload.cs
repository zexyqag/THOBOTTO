using System.Text;

using THOBOTTO.Helpers;

namespace THOBOTTO.Panel;

// A personality as a .json file, for anyone in its server: to keep, share, or import elsewhere.
public static class PersonalityDownload
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long id, PanelAccess access, PersonalityBook book)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await book.FindAsync(guildId, id) is not { } personality)
            return Results.NotFound();
        var file = PersonalityFile.From(personality);
        return Results.File(Encoding.UTF8.GetBytes(file.ToJson()), "application/json", file.FileName);
    }
}
