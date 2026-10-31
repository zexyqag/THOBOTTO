using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using THOBOTTO.GameServers;

namespace THOBOTTO.Tests;

// Rust's WebRCON and Palworld's REST API against stand-ins on this machine (password "secret").
public sealed class ConsoleTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private int _port;
    private readonly List<string> _announced = [];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/{password}", async (HttpContext context, string password) =>
        {
            if (password != "secret" || !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 401;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            using var command = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            var id = command.RootElement.GetProperty("Identifier").GetInt32();
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { Identifier = 0, Message = "[event] a log line", Type = "Generic" }), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { Identifier = id, Message = $"ran {command.RootElement.GetProperty("Message").GetString()}", Type = "Generic" }), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        _app.MapGet("/v1/api/info", (HttpContext context) => Authorised(context) ? Results.Json(new { version = "v0.3.11", servername = "Ours" }) : Results.Unauthorized());
        _app.MapPost("/v1/api/announce", async (HttpContext context) =>
        {
            if (!Authorised(context))
                return Results.Unauthorized();
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            _announced.Add(body.RootElement.GetProperty("message").GetString()!);
            return Results.Ok();
        });
        await _app.StartAsync();
        _port = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    private static bool Authorised(HttpContext context)
        => context.Request.Headers.Authorization == $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret"))}";

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static CancellationToken Soon => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Fact]
    public async Task Rusts_answer_is_the_message_with_the_commands_id_past_the_log()
        => Assert.Equal("ran say hi", await WebRcon.RunAsync("127.0.0.1", _port, "secret", "say hi", Soon));

    [Fact]
    public async Task Rust_refuses_a_wrong_password()
        => Assert.Equal("It refused the password.", (await Assert.ThrowsAsync<RconException>(() => WebRcon.RunAsync("127.0.0.1", _port, "guess", "say hi", Soon))).Message);

    [Fact]
    public async Task Palworld_answers_its_operations_and_announces()
    {
        using var http = new HttpClient();
        Assert.Contains("\"servername\": \"Ours\"", await PalworldApi.RunAsync(http, "127.0.0.1", _port, "secret", "info", Soon));
        Assert.Equal("", await PalworldApi.RunAsync(http, "127.0.0.1", _port, "secret", null, Soon));
        await PalworldApi.RunAsync(http, "127.0.0.1", _port, "secret", "announce [Discord] Base raid at 8!", Soon);
        Assert.Equal(["[Discord] Base raid at 8!"], _announced);
        Assert.Equal("It refused the password.", (await Assert.ThrowsAsync<RconException>(() => PalworldApi.RunAsync(http, "127.0.0.1", _port, "guess", "info", Soon))).Message);
        Assert.StartsWith("Palworld's console takes:", (await Assert.ThrowsAsync<RconException>(() => PalworldApi.RunAsync(http, "127.0.0.1", _port, "secret", "give Ana 5 cake", Soon))).Message);
    }

    [Fact]
    public void Palworlds_console_words_become_its_calls()
    {
        Assert.Equal((HttpMethod.Get, "players"), PalworldApi.Request("players") is { } players ? (players.Method, players.Path) : default);
        Assert.Equal("{\"userid\":\"steam_7656\",\"message\":\"too loud\"}", JsonSerializer.Serialize(PalworldApi.Request("kick steam_7656 too loud")!.Value.Body));
        Assert.Equal("{\"waittime\":60,\"message\":\"Restart\"}", JsonSerializer.Serialize(PalworldApi.Request("shutdown 60 Restart")!.Value.Body));
        Assert.Null(PalworldApi.Request("shutdown soon"));
        Assert.Null(PalworldApi.Request("save now"));
    }
}
