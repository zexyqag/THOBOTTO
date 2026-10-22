using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Voice.Relay;

namespace THOBOTTO.Panel;

// For the container's health check: up when the main bot is connected to Discord, the database answers, the
// voice relay runs if it's turned on, and Lavalink's plugins have been seen to (Lavalink waits for that before it
// starts). `--health` asks the running bot, as the image has no curl.
public static class Health
{
    public static void MapHealth(this WebApplication app)
        => app.MapGet("/health", async (GatewayClient gateway, IDbContextFactory<BotDbContext> dbFactory, IntegrationStore store, VoiceRelay relay, LavalinkPlugins plugins) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var problems = Problems(gateway.Status, await db.Database.CanConnectAsync(), store.Get(IntegrationStore.VoiceRelay) == "on", relay.Running, plugins.Tried);
            return problems.Count == 0 ? Results.Text($"ok {BotVersion.Running}") : Results.Text(string.Join("; ", problems), statusCode: 503);
        });

    public static IReadOnlyList<string> Problems(WebSocketStatus discord, bool database, bool relayOn, bool relayRunning, bool pluginsTried)
    {
        var problems = new List<string>();
        if (discord != WebSocketStatus.Ready)
            problems.Add($"Discord: {discord}");
        if (!database)
            problems.Add("database unreachable");
        if (relayOn && !relayRunning)
            problems.Add("voice relay not running");
        if (!pluginsTried)
            problems.Add("Lavalink plugins not seen to yet");
        return problems;
    }

    public static int Probe()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
            using var answer = http.GetAsync($"http://localhost:{port}/health").GetAwaiter().GetResult();
            Console.WriteLine(answer.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return answer.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }
}
