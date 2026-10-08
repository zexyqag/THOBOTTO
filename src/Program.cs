using Microsoft.EntityFrameworkCore;

using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.GameServers;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<GameDigOptions>()
    .BindConfiguration("GameDig")
    .ValidateOnStart();

builder.Services
    .AddDbContextFactory<BotDbContext>(options => options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .UseSnakeCaseNamingConvention())
    .AddSingleton(TimeProvider.System)
    .AddSingleton<ModuleState>()
    .AddSingleton<AccessControl>()
    .AddSingleton<GameDig>()
    .AddSingleton<ServerBoardService>()
    .AddHostedService(services => services.GetRequiredService<ServerBoardService>())
    .AddSingleton<DynamicVoice>()
    .AddHostedService(services => services.GetRequiredService<DynamicVoice>())
    .AddDiscordGateway()
    .AddApplicationCommands()
    .AddGatewayHandlers(typeof(Program).Assembly);

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.MigrateAsync();

host.AddModules(typeof(Program).Assembly);

await host.RunAsync();
