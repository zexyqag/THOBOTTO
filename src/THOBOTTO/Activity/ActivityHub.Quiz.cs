using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace THOBOTTO.Activity;

public sealed partial class ActivityHub
{
    private async Task QuizStateAsync(JsonObject state, Window window, ulong channelId)
    {
        var (guild, member) = (window.Guild, window.Member);
        if (quizzes.State(guild.Id, channelId, member.Id) is not { } quiz)
            return;
        var people = await names.NamesAsync(guild.Id, quiz.Scores.Select(s => s.UserId));
        state["quiz"] = new JsonObject
        {
            ["phase"] = quiz.Phase.ToString().ToLowerInvariant(),
            ["round"] = quiz.Number,
            ["rounds"] = quiz.Rounds,
            ["left"] = quiz.LeftMs,
            ["choices"] = new JsonArray([.. quiz.Choices.Select(c => (JsonNode)c)]),
            ["mine"] = quiz.Mine,
            ["answered"] = quiz.Answered,
            ["players"] = quiz.Players,
            ["correct"] = quiz.Correct,
            ["song"] = quiz.Song is { } song ? $"{song.Title} — {song.Artist}" : null,
            ["art"] = quiz.Art,
            ["right"] = new JsonArray([.. quiz.Picks.Where(p => p.Gain > 0).OrderByDescending(p => p.Gain)
                .Select(p => (JsonNode)new JsonObject { ["name"] = people.GetValueOrDefault(p.UserId) ?? "Someone", ["gain"] = p.Gain })]),
            ["gain"] = quiz.Picks.FirstOrDefault(p => p.UserId == member.Id)?.Gain,
            ["scores"] = new JsonArray([.. quiz.Scores.Take(10).Select(s => (JsonNode)new JsonObject
            {
                ["name"] = people.GetValueOrDefault(s.UserId) ?? "Someone",
                ["score"] = s.Score,
                ["me"] = s.UserId == member.Id,
            })]),
            ["canStop"] = await quizzes.CanStopAsync(guild.Id, channelId, member),
        };
    }

    private async Task<bool> DoQuizAsync(WebSocket socket, Window window, ulong channelId, string? action, JsonElement value, CancellationToken ct)
    {
        if (action is not { } quizzing || !quizzing.StartsWith("quiz-"))
            return false;
        var (guild, member) = (window.Guild, window.Member);
        var answer = quizzing switch
        {
            "quiz-start" when InChannel(guild.Id, member.Id, channelId) => await quizzes.StartAsync(guild.Id, channelId, member),
            "quiz-answer" when value.ValueKind == JsonValueKind.Number && InChannel(guild.Id, member.Id, channelId)
                => await quizzes.AnswerAsync(guild.Id, channelId, member.Id, value.GetInt32()),
            "quiz-stop" => await quizzes.StopAsync(guild.Id, channelId, member),
            _ => "Join the voice channel to play.",
        };
        if (answer is not null)
            await ReplyAsync(socket, guild.Id, answer, ct);
        return true;
    }
}
