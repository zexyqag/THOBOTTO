using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

using NetCord;
using NetCord.Gateway;

namespace THOBOTTO.Panel.Components;

// A page about one server: loads it and the signed-in member, and checks bot permissions.
public abstract class GuildPage : ComponentBase
{
    [Parameter]
    public long GuildId { get; set; }

    [CascadingParameter]
    private Task<AuthenticationState> AuthState { get; set; } = default!;

    [CascadingParameter]
    protected HttpContext HttpContext { get; set; } = default!;

    [Inject]
    protected PanelAccess Access { get; set; } = default!;

    protected Guild? Guild { get; private set; }

    protected GuildUser? Member { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        if (PanelAccess.UserId((await AuthState).User) is { } userId && await Access.InAsync((ulong)GuildId, userId) is { } found)
        {
            (Guild, Member) = found;
            await LoadAsync();
        }
    }

    // Called once the server and member are known.
    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task<bool> CanAsync(string permission) => Guild is not null && await Access.CanAsync(Guild, Member!, permission);

    protected string Posted(string name) => HttpContext.Request.HasFormContentType ? HttpContext.Request.Form[name].ToString() : "";

    protected string Query(string name) => HttpContext.Request.Query[name].ToString();
}
