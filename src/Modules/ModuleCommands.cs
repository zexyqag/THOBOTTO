using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;

namespace THOBOTTO.Modules;

[SlashCommand("modules", "Turn bot modules on or off", Contexts = [InteractionContextType.Guild])]
[RequirePermission(BotPermissions.ManageModules)]
public sealed class ModuleCommands(ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Interaction.GuildId!.Value;

    [SubSlashCommand("list", "Show every module and whether it's on")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        if (ModuleRegistry.All.Count == 0)
            return Replies.Ephemeral("No modules exist yet.");

        var lines = new List<string>();
        foreach (var module in ModuleRegistry.All)
        {
            var mark = await modules.IsEnabledAsync(GuildId, module.Id) ? "on" : "off";
            lines.Add($"`{module.Id}` ({mark}): {module.Description}");
        }

        return Replies.Ephemeral(string.Join('\n', lines));
    }

    [SubSlashCommand("enable", "Turn a module on")]
    public Task<InteractionMessageProperties> EnableAsync(
        [SlashCommandParameter(Description = "Module", AutocompleteProviderType = typeof(ModuleAutocomplete))] string module)
        => SetAsync(module, true);

    [SubSlashCommand("disable", "Turn a module off")]
    public Task<InteractionMessageProperties> DisableAsync(
        [SlashCommandParameter(Description = "Module", AutocompleteProviderType = typeof(ModuleAutocomplete))] string module)
        => SetAsync(module, false);

    private async Task<InteractionMessageProperties> SetAsync(string id, bool enabled)
    {
        if (ModuleRegistry.Find(id) is null)
            return Replies.Ephemeral($"There is no module called `{id}`.");

        var state = enabled ? "on" : "off";
        return Replies.Ephemeral(await modules.SetEnabledAsync(GuildId, id, enabled, Context.User.Id)
            ? $"`{id}` is now {state}."
            : $"`{id}` is already {state}.");
    }
}
