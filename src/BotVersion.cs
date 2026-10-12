using System.Globalization;
using System.Reflection;

namespace THOBOTTO;

// The running build's version: a release's number ("0.1.0"), a branch build's prerelease ("0.0.0-main.abc1234")
// or a build from source ("0.0.0+abc1234", the SDK appending the commit).
public static class BotVersion
{
    public static string Running { get; } = Shorten(typeof(BotVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    // A release is a number alone; 0.0.0 is what a build without a tag says.
    public static bool IsRelease(string version)
        => Parse(version) is { Prerelease: "" } parsed && (parsed.Major, parsed.Minor, parsed.Patch) != (0, 0, 0);

    // By the numbers; of the same number, a release is newer than a prerelease.
    public static bool IsNewer(string version, string than)
    {
        if (Parse(version) is not { } a || Parse(than) is not { } b)
            return false;
        var numbers = (a.Major, a.Minor, a.Patch).CompareTo((b.Major, b.Minor, b.Patch));
        return numbers > 0 || (numbers == 0 && a.Prerelease.Length == 0 && b.Prerelease.Length > 0);
    }

    // The commit after + as git shows it: 7 characters.
    public static string Shorten(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }

    public static (int Major, int Minor, int Patch, string Prerelease, string Commit)? Parse(string version)
    {
        var plus = version.IndexOf('+');
        var commit = plus < 0 ? "" : version[(plus + 1)..];
        var core = plus < 0 ? version : version[..plus];
        var dash = core.IndexOf('-');
        var prerelease = dash < 0 ? "" : core[(dash + 1)..];
        return (dash < 0 ? core : core[..dash]).Split('.') is [var major, var minor, var patch]
            && Number(major) is { } first && Number(minor) is { } second && Number(patch) is { } third
            ? (first, second, third, prerelease, commit)
            : null;
    }

    private static int? Number(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}
