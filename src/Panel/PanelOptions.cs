namespace THOBOTTO.Panel;

// The web admin panel. Off unless a client secret is set (or, in development only, DevLogin).
public sealed class PanelOptions
{
    // From the Discord application's OAuth2 page; with the bot's own id as client id.
    public string? ClientSecret { get; set; }

    // Development only: /dev-login?user=<id> signs in as anyone, to test without Discord's login.
    public bool DevLogin { get; set; }
}
