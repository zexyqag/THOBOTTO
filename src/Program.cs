using Microsoft.EntityFrameworkCore;

using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Modules;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddDbContextFactory<BotDbContext>(options => options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .UseSnakeCaseNamingConvention())
    .AddSingleton(TimeProvider.System)
    .AddSingleton<ModuleState>()
    .AddDiscordGateway()
    .AddApplicationCommands();

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.MigrateAsync();

host.AddModules(typeof(Program).Assembly);

await host.RunAsync();
