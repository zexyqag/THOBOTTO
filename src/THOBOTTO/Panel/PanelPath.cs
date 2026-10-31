namespace THOBOTTO.Panel;

// A path of the panel's own, for a "back to" after a login: never another site, which "//host" and "/\host" reach, and
// so does "/<tab>/host", as browsers drop tabs and line breaks from an address (ASP.NET's own check refuses them too).
public static class PanelPath
{
    public static string? Local(string? path)
        => path is ['/'] or ['/', not '/' and not '\\', ..] && !path.Any(char.IsControl) ? path : null;
}
