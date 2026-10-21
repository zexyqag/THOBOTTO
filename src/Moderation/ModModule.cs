using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// What /mod's commands share: who acts, the checks before acting on someone, and replying.
public abstract class ModModule(ModuleState modules) : GuildCommandModule
{
    protected const string ModuleId = CaseBook.ModuleId;

    protected GuildUser Actor => (GuildUser)Context.User;

    protected Actor Me => new(GuildId, Actor.Id, Actor.Username);

    // Discord calls take a moment, so the reply is deferred once the checks pass.
    protected async Task RunAsync(string? refusal, Func<Task<string>> action)
    {
        if (refusal is not null)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await action();
        await ModifyResponseAsync(m =>
        {
            m.Content = result;
            m.AllowedMentions = AllowedMentionsProperties.None;
        });
    }

    protected async Task<string?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, ModuleId) ? null : ModuleRegistry.Off(ModuleId);

    // Not on yourself, and only on members ranked below you; also for the module being off.
    protected async Task<string?> RefusalAsync(GuildUser? member, ulong? targetId = null)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if ((member?.Id ?? targetId) == Actor.Id)
            return "Not on yourself.";
        if (member is not null && !AccessControl.Outranks(Guild, Actor, member, allowEqual: false))
            return $"<@{member.Id}> doesn't rank below you.";
        return null;
    }

    protected static string Dm(bool dmed) => dmed ? " They got a DM." : " They didn't get a DM (closed, or DMs are off in `/setup moderation general`).";
}
