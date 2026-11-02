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

    [Fact]
    public void Buttons_load() => new ComponentInteractionService<ButtonInteractionContext>().AddModules(Bot);

    [Fact]
    public void Modals_load() => new ComponentInteractionService<ModalInteractionContext>().AddModules(Bot);
}
