using NetCord;

using Npgsql;

namespace THOBOTTO;

// What's checked before the bot starts, so a bad deployment stops with one plain line instead of a stack
// trace from wherever the problem is first met.
public static class StartCheck
{

    // The settings the bot can't run without, and those that must be right when set; a sentence per problem.
    public static IReadOnlyList<string> Settings(IConfiguration config)
    {
        var problems = new List<string>();
        if (config["Discord:Token"] is not { Length: > 0 } token)
            problems.Add("Discord:Token is missing.");
        else if (!IsBotToken(token))
            problems.Add("Discord:Token isn't a bot token.");
        if (config.GetConnectionString("Postgres") is not { Length: > 0 } postgres)
            problems.Add("ConnectionStrings:Postgres is missing.");
        else if (ConnectionStringProblem(postgres) is { } problem)
            problems.Add($"ConnectionStrings:Postgres isn't a connection string: {problem}");
        if (config["Lavalink:BaseAddress"] is { Length: > 0 } lavalink && !IsHttpUrl(lavalink))
            problems.Add($"Lavalink:BaseAddress ({lavalink}) isn't an absolute http(s) URL.");
        if (config["GameDig:Path"] is not { Length: > 0 } gameDig)
            problems.Add("GameDig:Path is missing.");
        else if (!Directory.Exists(gameDig))
            problems.Add($"GameDig:Path ({gameDig}) isn't a folder.");
        if (config["DataProtection:Directory"] is { Length: > 0 } keys && !IsWritableFolder(keys))
            problems.Add($"DataProtection:Directory ({keys}) isn't a writable folder.");
        return problems;
    }

    private static bool IsBotToken(string token)
    {
        try
        {
            _ = new BotToken(token);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? ConnectionStringProblem(string connectionString)
    {
        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static bool IsHttpUrl(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // A folder not there yet is made for the probe and removed again, so the bot makes it its own way (owner-only) later.
    private static bool IsWritableFolder(string directory)
    {
        var made = !Directory.Exists(directory);
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, Path.GetRandomFileName());
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            if (made)
                Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
