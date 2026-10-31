using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using THOBOTTO.GameServers;
using THOBOTTO.Steam;

namespace THOBOTTO.DatabaseTests;

[Collection(DatabaseCollection.Name)]
public class WhitelistTests(TestDatabase database)
{
    private const ulong Role = 5;

    // A Minecraft server's RCON on this machine (password "pw"), a connection for each command, as the bot makes them.
    private sealed class Minecraft : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public Minecraft()
        {
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Commands { get; } = [];

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                using (client)
                {
                    var stream = client.GetStream();
                    try
                    {
                        while (true)
                        {
                            var head = new byte[4];
                            await stream.ReadExactlyAsync(head);
                            var packet = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
                            await stream.ReadExactlyAsync(packet);
                            var (id, type, body) = (BinaryPrimitives.ReadInt32LittleEndian(packet), BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)), Encoding.UTF8.GetString(packet.AsSpan(8, packet.Length - 10)));
                            if (type == 3)
                                await SendAsync(stream, body == "pw" ? id : -1, 2, "");
                            else
                            {
                                lock (Commands)
                                    Commands.Add(body);
                                await SendAsync(stream, id, 0, body.StartsWith("whitelist add ", StringComparison.Ordinal) ? $"Added {body[14..]} to the whitelist" : $"Removed {body[17..]} from the whitelist");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is EndOfStreamException or IOException)
                    {
                    }
                }
            }
        }

        private static async Task SendAsync(NetworkStream stream, int id, int type, string body)
        {
            var text = Encoding.UTF8.GetBytes(body);
            var packet = new byte[14 + text.Length];
            BinaryPrimitives.WriteInt32LittleEndian(packet, 10 + text.Length);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
            text.CopyTo(packet, 12);
            await stream.WriteAsync(packet);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serving;
        }
    }

    private static readonly ServerDetails Details = new(new EphemeralDataProtectionProvider());

    private ServerWhitelist Whitelist() => new(database, new ServerControl(database, Details, new HttpClient(), NullLogger<ServerControl>.Instance),
        new SteamLibrary(database, null!, TimeProvider.System, NullLogger<SteamLibrary>.Instance), TimeProvider.System, NullLogger<ServerWhitelist>.Instance);

    private async Task<GameServer> ServerAsync(ulong guildId, int port)
    {
        await using var db = database.CreateDbContext();
        var server = new GameServer
        {
            GuildId = guildId,
            Game = "minecraft",
            Host = "mc.example",
            Name = "Survival",
            OwnerId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            RconAddress = $"127.0.0.1:{port}",
            RconPassword = Details.Protect("pw"),
            RconInside = true,
            WhitelistFor = WhitelistOpen.Role,
            WhitelistRoleId = Role,
        };
        db.GameServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private async Task<List<WhitelistEntry>> EntriesAsync(long serverId)
    {
        await using var db = database.CreateDbContext();
        return await db.WhitelistEntries.AsNoTracking().Where(w => w.ServerId == serverId).ToListAsync();
    }

    [Fact]
    public async Task A_member_is_added_by_name_renamed_and_taken_off_when_they_leave()
    {
        await using var minecraft = new Minecraft();
        var server = await ServerAsync(951, minecraft.Port);
        var whitelist = Whitelist();
        Assert.Equal("You're on **Survival**'s whitelist as **Sprig**.", await whitelist.AddAsync(server, 41, "Sprig", null));
        Assert.Equal("You're on it already, as **Sprig**.", await whitelist.AddAsync(server, 41, "Sprig", null));
        Assert.Equal("That isn't a Minecraft name: 3 to 16 letters, digits and underscores.", await whitelist.AddAsync(server, 41, "not a name", null));
        Assert.Equal("You're on **Survival**'s whitelist as **Sprig2**.", await whitelist.AddAsync(server, 41, "Sprig2", null));
        Assert.Equal("Sprig2", Assert.Single(await EntriesAsync(server.Id)).Name);

        await whitelist.MemberLeftAsync(951, 41);
        Assert.Empty(await EntriesAsync(server.Id));
        Assert.Equal(["whitelist add Sprig", "whitelist remove Sprig", "whitelist add Sprig2", "whitelist remove Sprig2"], minecraft.Commands);
    }

    [Fact]
    public async Task Losing_the_role_takes_a_member_off_and_keeping_it_doesnt()
    {
        await using var minecraft = new Minecraft();
        var server = await ServerAsync(952, minecraft.Port);
        var whitelist = Whitelist();
        await whitelist.AddAsync(server, 42, "Ana", null);
        await whitelist.AddAsync(server, 43, "Mads", null);

        await whitelist.RolesChangedAsync(952, 42, [Role, 9]);
        await whitelist.RolesChangedAsync(952, 43, [9]);
        Assert.Equal("Ana", Assert.Single(await EntriesAsync(server.Id)).Name);
        Assert.Contains("whitelist remove Mads", minecraft.Commands);
        Assert.DoesNotContain("whitelist remove Ana", minecraft.Commands);
        Assert.Equal("That whitelist is for <@&5>.", ServerWhitelist.Refused(server, [9]));
        Assert.Null(ServerWhitelist.Refused(server, [Role]));
    }

    [Fact]
    public async Task A_member_who_leaves_while_the_game_server_is_down_comes_off_once_it_answers()
    {
        GameServer server;
        var whitelist = Whitelist();
        await using (var minecraft = new Minecraft())
        {
            server = await ServerAsync(953, minecraft.Port);
            await whitelist.AddAsync(server, 44, "Sprig", null);
        }
        await whitelist.MemberLeftAsync(953, 44);
        var stuck = Assert.Single(await EntriesAsync(server.Id));
        Assert.True(stuck.Removing);

        await using var back = new Minecraft();
        await using (var db = database.CreateDbContext())
            await db.GameServers.Where(s => s.Id == server.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RconAddress, $"127.0.0.1:{back.Port}"));
        Assert.Null(await whitelist.RemoveAsync(stuck.Id));
        Assert.Empty(await EntriesAsync(server.Id));
        Assert.Equal(["whitelist remove Sprig"], back.Commands);
    }
}
