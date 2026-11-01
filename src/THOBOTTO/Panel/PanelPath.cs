using Microsoft.AspNetCore.Http.HttpResults;

namespace THOBOTTO.Panel;

// A path of the panel's own, for a "back to": never another site, which "//host" and "/\host" reach, and so does
// "/<tab>/host", as browsers drop tabs and line breaks from an address. ASP.NET's own check, without its "~/".
public static class PanelPath
{
    public static string? Local(string? path) => path is ['/', ..] && RedirectHttpResult.IsLocalUrl(path) ? path : null;
}
