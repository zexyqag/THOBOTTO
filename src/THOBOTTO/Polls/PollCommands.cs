using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

public enum PollLength
{
    [SlashCommandChoice(Name = "5 minutes")] FiveMinutes,
    [SlashCommandChoice(Name = "15 minutes")] FifteenMinutes,
    [SlashCommandChoice(Name = "1 hour")] OneHour,
    [SlashCommandChoice(Name = "6 hours")] SixHours,
    [SlashCommandChoice(Name = "1 day")] OneDay,
    [SlashCommandChoice(Name = "3 days")] ThreeDays,
    [SlashCommandChoice(Name = "1 week")] OneWeek,
}

public sealed class PollCommand(PollBook book, ModuleState modules) : GuildCommandModule
{
    [SlashCommand("poll", "Ask this channel a question: everyone votes with buttons, the result shows when it closes", Contexts = [InteractionContextType.Guild])]
    public async Task PollAsync(
        [SlashCommandParameter(Description = "The question", MaxLength = 200)] string question,
        [SlashCommandParameter(Description = "2 to 6 answers, separated by commas", MaxLength = 500)] string answers,
        [SlashCommandParameter(Name = "open-for", Description = "How long it takes votes (default a day)")] PollLength openFor = PollLength.OneDay)
    {
        string? refusal = null;
        if (!await modules.IsEnabledAsync(GuildId, PollBook.ModuleId))
            refusal = ModuleRegistry.Off(PollBook.ModuleId);
        else if (Context.User is GuildInteractionUser member && !member.Permissions.HasFlag(Permissions.SendPolls) && !member.Permissions.HasFlag(Permissions.Administrator))
            refusal = "You can't post polls in this channel.";
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        var (live, problem) = await book.CreateAsync(GuildId, Context.Channel.Id, Context.User.Id, question, answers.Split(','), Length(openFor), inVoice: false);
        if (live is null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(problem!)));
            return;
        }
        var (embed, components) = PollBook.Card(live);
        await RespondAsync(InteractionCallback.Message(new() { Embeds = [embed], Components = components, AllowedMentions = AllowedMentionsProperties.None }));
        await book.SetMessageAsync(live, (await GetResponseAsync()).Id);
    }

    private static TimeSpan Length(PollLength length) => length switch
    {
        PollLength.FiveMinutes => TimeSpan.FromMinutes(5),
        PollLength.FifteenMinutes => TimeSpan.FromMinutes(15),
        PollLength.OneHour => TimeSpan.FromHours(1),
        PollLength.SixHours => TimeSpan.FromHours(6),
        PollLength.ThreeDays => TimeSpan.FromDays(3),
        PollLength.OneWeek => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };
}

public sealed class PollButtons(PollBook book) : GuildButtonModule
{
    [ComponentInteraction("pollvote")]
    public async Task<InteractionCallbackProperties> VoteAsync(long pollId, int option)
        => InteractionCallback.Message(Replies.Ephemeral(await book.VoteAsync(pollId, Context.User.Id, option)
            ?? $"🗳️ You picked **{book.Find(pollId)?.Poll.Options[option]}**. Pick another to change it."));

    [ComponentInteraction("pollclose")]
    public async Task<InteractionCallbackProperties> CloseAsync(long pollId)
        => InteractionCallback.Message(Replies.Ephemeral(await book.CloseAsync(pollId, Guild, (GuildUser)Context.User)));
}
