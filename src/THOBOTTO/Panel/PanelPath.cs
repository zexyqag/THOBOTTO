namespace THOBOTTO.Panel;

// A path of the panel's own, for a "back to" after a login: never another site, which "//host" and "/\host" reach.
public static class PanelPath
{
    public static string? Local(string? path) => path is ['/'] or ['/', not '/' and not '\\', ..] ? path : null;
}
