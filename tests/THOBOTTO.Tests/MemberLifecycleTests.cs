using System.Text.Json;

using Microsoft.Extensions.Logging;

using THOBOTTO.Debugging;
using THOBOTTO.Members;

namespace THOBOTTO.Tests;

// A member who left is forgotten by everyone keeping their preferences, each on its own; a member's data is one file
// with each source under its name.
public class MemberLifecycleTests
{
    private const ulong Server = 123456789012345678;
    private const ulong Member = 42;

    [Fact]
    public async Task A_failing_keeper_stops_neither_the_next_nor_the_handler()
    {
        var log = new DebugLog(TimeProvider.System);
        log.Start();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(new DebugLogProvider(log)));
        var forgotten = new List<string>();
        var leaving = new MemberLeaving(
        [
            new Keeper("first", _ => throw new InvalidOperationException("a feature broke")),
            new Keeper("second", _ => Task.FromException(new InvalidOperationException("another did too"))),
            new Keeper("third", name =>
            {
                forgotten.Add(name);
                return Task.CompletedTask;
            }),
        ], loggers.CreateLogger<MemberLeaving>());

        await leaving.LeftAsync(Server, Member);

        Assert.Equal(["third"], forgotten);
        var entries = log.Entries();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Equal(Server, entry.GuildId);
            Assert.Contains("Keeper", entry.Message);
        });
    }

    [Fact]
    public async Task The_file_names_the_server_the_member_and_the_time_then_each_source_under_its_name()
    {
        var taken = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        using var output = new MemoryStream();
        await MemberDataFile.WriteAsync(output, Server, Member, taken,
        [
            new Source("Songs played", json =>
            {
                json.WriteStartArray();
                json.WriteStringValue("a");
                json.WriteStringValue("b");
                json.WriteEndArray();
            }),
            new Source("Points", json =>
            {
                json.WriteStartObject();
                json.WriteNumber("balance", 12.5);
                json.WriteEndObject();
            }),
        ]);

        using var file = JsonDocument.Parse(output.ToArray());
        var root = file.RootElement;
        Assert.Equal(["server", "member", "taken", "songsPlayed", "points"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("123456789012345678", root.GetProperty("server").GetString());
        Assert.Equal("42", root.GetProperty("member").GetString());
        Assert.Equal(taken, root.GetProperty("taken").GetDateTimeOffset());
        Assert.Equal(["a", "b"], root.GetProperty("songsPlayed").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(12.5, root.GetProperty("points").GetProperty("balance").GetDouble());
    }

    [Theory]
    [InlineData("Points", "points")]
    [InlineData("Songs played", "songsPlayed")]
    [InlineData("Hall of fame", "hallOfFame")]
    [InlineData("Named in Wrapped", "namedInWrapped")]
    public void A_sources_name_is_its_property(string name, string property) => Assert.Equal(property, MemberDataFile.PropertyName(name));

    private sealed class Keeper(string name, Func<string, Task> forget) : IMemberLeft
    {
        public Task MemberLeftAsync(ulong guildId, ulong userId) => forget(name);
    }

    private sealed class Source(string name, Action<Utf8JsonWriter> write) : IMemberData
    {
        public string Name => name;

        public Task<long> CountAsync(ulong guildId, ulong userId) => Task.FromResult(0L);

        public Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
        {
            write(json);
            return Task.CompletedTask;
        }
    }
}
