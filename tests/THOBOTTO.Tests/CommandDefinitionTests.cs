using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Games;

namespace THOBOTTO.Tests;

// The bot's commands, buttons and modals load as at start, where a definition NetCord refuses stops the bot.
public class CommandDefinitionTests
{
    private static readonly System.Reflection.Assembly Bot = typeof(GameDirectory).Assembly;

    [Fact]
    public void Commands_load() => new ApplicationCommandService<ApplicationCommandContext, AutocompleteInteractionContext>().AddModules(Bot);

    // A command run from a member's own install has no server when the bot isn't in it: server modules' commands are
    // the servers' only.
    [Fact]
    public void Server_modules_are_never_installed_on_accounts()
    {
        var wrong = Bot.GetTypes().Where(t => t.IsSubclassOf(typeof(Discord.GuildCommandModule)))
            .SelectMany(t => t.GetMethods().Cast<System.Reflection.MemberInfo>().Prepend(t))
            .SelectMany(m => m.GetCustomAttributes(inherit: false).OfType<ApplicationCommandAttribute>().Select(a => (Member: m, Attribute: a)))
            .Where(x => x.Attribute.IntegrationTypes?.Contains(NetCord.ApplicationIntegrationType.UserInstall) == true)
            .Select(x => $"{x.Member.DeclaringType?.Name ?? x.Member.Name}.{x.Member.Name}")
            .ToList();
        Assert.True(wrong.Count == 0, "Server modules' commands installed on accounts:\n" + string.Join('\n', wrong));
    }

    // Discord refuses every command at start when one description or name is too long.
    [Fact]
    public void Descriptions_and_names_fit_discord()
    {
        var members = Bot.GetTypes().SelectMany(t => t.GetMethods().Cast<System.Reflection.MemberInfo>().Prepend(t));
        var texts = members.SelectMany(m => m.GetCustomAttributes(inherit: false)).OfType<ApplicationCommandAttribute>().Select(a => (a.Name, Description: (a as SlashCommandAttribute)?.Description))
            .Concat(members.SelectMany(m => m.GetCustomAttributes(inherit: false)).OfType<SubSlashCommandAttribute>().Select(a => (a.Name, Description: (string?)a.Description)))
            .Concat(Bot.GetTypes().SelectMany(t => t.GetMethods()).SelectMany(m => m.GetParameters())
                .Select(p => (Parameter: p, Attribute: p.GetCustomAttributes(inherit: false).OfType<SlashCommandParameterAttribute>().FirstOrDefault()))
                .Where(p => p.Attribute is not null)
                .Select(p => (Name: p.Attribute!.Name ?? p.Parameter.Name!, p.Attribute.Description)))
            .ToList();
        Assert.NotEmpty(texts);
        var wrong = texts.Where(t => t.Description is { Length: > 100 } || t.Name.Length > 32).Select(t => $"{t.Name}: {t.Description}").ToList();
        Assert.True(wrong.Count == 0, "Too long for Discord:\n" + string.Join('\n', wrong));
    }

    [Fact]
    public void Buttons_load() => new ComponentInteractionService<ButtonInteractionContext>().AddModules(Bot);

    [Fact]
    public void Modals_load() => new ComponentInteractionService<ModalInteractionContext>().AddModules(Bot);
}
