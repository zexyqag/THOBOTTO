using THOBOTTO.Members;

namespace THOBOTTO.Panel.Pages;

// A member's data in a server as a .json file, for the member themselves.
public static class MemberDataDownload
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, PanelAccess access, IEnumerable<IMemberData> sources, TimeProvider time)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var taken = time.GetUtcNow();
        return Results.Stream(output => MemberDataFile.WriteAsync(output, guildId, userId, taken, sources.OrderBy(s => s.Name)), "application/json", MemberDataFile.FileName(guildId, userId, taken));
    }
}
