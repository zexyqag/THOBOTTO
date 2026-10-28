using THOBOTTO.Panel;

namespace THOBOTTO.Expressions;

// An emoji's or sticker's image, for seeing it on the Emoji page.
public static class ExpressionImages
{
    public static async Task<IResult> ServeAsync(HttpContext context, ulong guildId, long id, PanelAccess access, ExpressionShelf shelf)
    {
        if (PanelAccess.UserId(context.User) is not { } userId || await access.InAsync(guildId, userId) is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (await shelf.FindAsync(guildId, id) is not { } expression)
            return Results.NotFound();
        return Results.File(expression.Image, expression.ContentType);
    }
}
