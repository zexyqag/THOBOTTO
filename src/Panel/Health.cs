using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;

namespace THOBOTTO.Panel;

// For the container's health check: up when the main bot is connected to Discord and the database answers.
// `--health` asks the running bot, as the image has no curl.
public static class Health
{
    public static void MapHealth(this WebApplication app)
        => app.MapGet("/health", async (GatewayClient gateway, IDbContextFactory<BotDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var problems = new List<string>();
            if (gateway.Status != WebSocketStatus.Ready)
                problems.Add($"Discord: {gateway.Status}");
            if (!await db.Database.CanConnectAsync())
                problems.Add("database unreachable");
            return problems.Count == 0 ? Results.Text("ok") : Results.Text(string.Join("; ", problems), statusCode: 503);
        });

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
