using THOBOTTO.Watch;

namespace THOBOTTO.Tests;

public class YoutubeCookieTests
{
    private static string[] Names(string? cookies)
        => [.. cookies!.Split('\n').Select(l => l.Split('\t')).Where(f => f.Length == 7).Select(f => $"{f[5]}={f[6]}")];

    [Fact]
    public void Chromes_cURL_for_bash_is_read()
    {
        var (cookies, problem) = YoutubeCookies.Read("curl 'https://www.youtube.com/' \\\n  -H 'accept: text/html' \\\n  -b 'PREF=f6=40000000; SID=abc.def; __Secure-3PAPISID=x/y-z' \\\n  -H 'user-agent: Mozilla'");
        Assert.Null(problem);
        Assert.Equal(["PREF=f6=40000000", "SID=abc.def", "__Secure-3PAPISID=x/y-z"], Names(cookies));
    }

    [Fact]
    public void Chromes_cURL_for_cmd_is_read()
    {
        var (cookies, _) = YoutubeCookies.Read("curl ^\"https://www.youtube.com/^\" ^\n  -b ^\"SID=abc; SAPISID=q/r^\" ^\n  -H ^\"accept: */*^\"");
        Assert.Equal(["SID=abc", "SAPISID=q/r"], Names(cookies));
    }

    [Fact]
    public void Firefoxs_cURL_is_read()
    {
        var (cookies, _) = YoutubeCookies.Read("curl 'https://www.youtube.com/' -H 'User-Agent: Mozilla' -H 'Cookie: LOGIN_INFO=AFm; SID=abc' -H 'TE: trailers'");
        Assert.Equal(["LOGIN_INFO=AFm", "SID=abc"], Names(cookies));
    }

    [Fact]
    public void PowerShell_is_read()
    {
        var (cookies, _) = YoutubeCookies.Read("$session.Cookies.Add((New-Object System.Net.Cookie(\"SID\", \"abc\", \"/\", \".youtube.com\")))\n$session.Cookies.Add((New-Object System.Net.Cookie(\"YSC\", \"q\", \"/\", \".youtube.com\")))");
        Assert.Equal(["SID=abc", "YSC=q"], Names(cookies));
    }

    [Fact]
    public void The_header_alone_is_read()
        => Assert.Equal(["SID=abc", "HSID=d"], Names(YoutubeCookies.Read("Cookie: SID=abc; HSID=d").Cookies));

    [Fact]
    public void A_cookies_txt_keeps_youtube_and_google()
    {
        var file = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tSID\tabc\n.example.com\tTRUE\t/\tTRUE\t1893456000\tOTHER\tx\n.google.com\tTRUE\t/\tTRUE\t1893456000\tSAPISID\tq\n";
        Assert.Equal(["SID=abc", "SAPISID=q"], Names(YoutubeCookies.Read(file).Cookies));
    }

    [Fact]
    public void Cookies_without_signing_in_are_refused()
    {
        var (cookies, problem) = YoutubeCookies.Read("Cookie: PREF=f6=4; YSC=abc; VISITOR_INFO1_LIVE=x");
        Assert.Null(cookies);
        Assert.Contains("signed-in", problem);
    }
}
