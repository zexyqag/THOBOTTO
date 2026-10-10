using System.Collections.Concurrent;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Helpers;
using THOBOTTO.Listening;
using THOBOTTO.Music;

namespace THOBOTTO.Assistant;

// Questions a helper asks about a voice command: is that right, or which one. Answered by saying so (shortly
// after, without the helper's name) or with the buttons, by whoever asked for it; answering does it.
public sealed class VoiceQuestions(MusicService music, RestClient rest, TimeProvider time) : IHelperAware
{
    private static readonly TimeSpan SpokenAnswer = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ButtonAnswer = TimeSpan.FromMinutes(5);
    // Someone just talked about stays "them" this long.
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(1);
    private const string Prefix = "ask";

    private readonly ConcurrentDictionary<string, Question> _open = new();
    // (server, voice channel) → who was last talked about there, and when.
    private readonly ConcurrentDictionary<(ulong, ulong), (ulong Person, DateTimeOffset At)> _mentioned = new();

    private sealed record Question(string Token, Heard Heard, HelperBot Helper, IReadOnlyList<VoiceChoice> Choices, DateTimeOffset AskedAt)
    {
        public (ulong Id, bool ByHelper)? Message { get; set; }

        public int Answered;
    }

    public ulong? Mentioned(ulong guildId, ulong channelId)
        => _mentioned.TryGetValue((guildId, channelId), out var m) && time.GetUtcNow() - m.At < Remembered ? m.Person : null;

    public Task AttachAsync(HelperBot helper)
    {
        helper.Gateway.InteractionCreate += async interaction =>
        {
            if (interaction is ButtonInteraction { Data.CustomId: var id } button && id.StartsWith(Prefix + ":"))
                await PressAsync(button);
        };
        return Task.CompletedTask;
    }

    public Task DetachAsync(HelperBot helper) => Task.CompletedTask;

    public async Task AskAsync(Heard heard, HelperBot helper, VoicePlan plan)
    {
        foreach (var stale in _open.Values.Where(q => time.GetUtcNow() - q.AskedAt > ButtonAnswer).ToList())
            _open.TryRemove(stale.Token, out _);
        var question = new Question(Guid.NewGuid().ToString("N")[..12], heard, helper, plan.Choices!, time.GetUtcNow());
        _open[question.Token] = question;
        question.Message = await music.PostAsync(heard.GuildId, helper, heard.ChannelId, new()
        {
            Content = $"🎙️ <@{heard.UserId}> · {plan.Reply}",
            Components = [new ActionRowProperties([
                .. question.Choices.Select((c, i) => new ButtonProperties($"{Prefix}:{question.Token}:{i}", Trim(c.Label), ButtonStyle.Success)),
                new ButtonProperties($"{Prefix}:{question.Token}:{VoiceAnswers.Cancel}", "Cancel", ButtonStyle.Secondary)])],
            AllowedMentions = AllowedMentionsProperties.None,
        });
    }

    // A spoken answer from whoever was just asked there: the reply once done, or null when it's no answer.
    public async Task<string?> AnswerAsync(Heard heard)
    {
        if (_open.Values.Where(q => q.Heard.UserId == heard.UserId && q.Heard.ChannelId == heard.ChannelId && time.GetUtcNow() - q.AskedAt < SpokenAnswer)
                .MaxBy(q => q.AskedAt) is not { } question
            || VoiceAnswers.Pick(heard.Text, question.Choices.Select(c => c.Label).ToList()) is not { } index)
            return null;
        var reply = await SettleAsync(question, index);
        if (reply is not null && question.Message is { } message)
            await EditAsync(question, message, reply);
        return reply;
    }

    public async Task PressAsync(ButtonInteraction button)
    {
        var parts = button.Data.CustomId.Split(':');
        if (!_open.TryGetValue(parts[1], out var question))
        {
            await button.SendResponseAsync(InteractionCallback.Message(new() { Content = "That question has expired; say it again.", Flags = MessageFlags.Ephemeral }));
            return;
        }
        if (button.User.Id != question.Heard.UserId)
        {
            await button.SendResponseAsync(InteractionCallback.Message(new() { Content = $"Only <@{question.Heard.UserId}> can answer that.", Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None }));
            return;
        }
        // Doing it may take longer than Discord waits for an answer.
        await button.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
        if (await SettleAsync(question, int.Parse(parts[2])) is { } reply)
            await button.ModifyResponseAsync(m => (m.Content, m.Components, m.AllowedMentions) = ($"🎙️ <@{question.Heard.UserId}> · {reply}", [], AllowedMentionsProperties.None));
    }

    // Once per question, however it's answered.
    private async Task<string?> SettleAsync(Question question, int index)
    {
        if (Interlocked.Exchange(ref question.Answered, 1) == 1)
            return null;
        _open.TryRemove(question.Token, out _);
        if (index == VoiceAnswers.Cancel || index < 0 || index >= question.Choices.Count)
            return "Okay, never mind.";
        var choice = question.Choices[index];
        if (choice.About is { } person)
            Remember(question.Heard.GuildId, question.Heard.ChannelId, person);
        return await choice.RunAsync();
    }

    public void Remember(ulong guildId, ulong channelId, ulong person) => _mentioned[(guildId, channelId)] = (person, time.GetUtcNow());

    private async Task EditAsync(Question question, (ulong Id, bool ByHelper) message, string reply)
    {
        var client = message.ByHelper ? question.Helper.Gateway.Rest : rest;
        try
        {
            await client.ModifyMessageAsync(question.Heard.ChannelId, message.Id, m => (m.Content, m.Components, m.AllowedMentions) = ($"🎙️ <@{question.Heard.UserId}> · {reply}", [], AllowedMentionsProperties.None));
        }
        catch (RestException)
        {
            await music.ReplyAsync(question.Heard.GuildId, question.Helper, question.Heard.ChannelId, $"🎙️ <@{question.Heard.UserId}> · {reply}");
        }
    }

    private static string Trim(string label) => label.Length <= 80 ? label : label[..79] + "…";
}

// Buttons on questions the main bot posted for a helper; a helper's own reach VoiceQuestions directly.
public sealed class VoiceQuestionButtons(VoiceQuestions questions) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("ask")]
    public Task AnswerAsync(string token, int index) => questions.PressAsync(Context.Interaction);
}
