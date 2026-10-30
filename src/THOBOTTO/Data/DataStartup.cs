using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using THOBOTTO.Certificates;

namespace THOBOTTO.Data;

public static class DataStartup
{
    public static IServiceCollection AddData(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddDbContextFactory<BotDbContext>(options => options
                .UseNpgsql(new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Postgres"))
                {
                    // The chiseled runtime image has no Kerberos library; without this Npgsql logs its absence.
                    GssEncryptionMode = GssEncryptionMode.Disable,
                }.ConnectionString)
                .UseSnakeCaseNamingConvention())
            .AddSingleton(TimeProvider.System);

        // Encrypts the panel's login cookies, helper tokens and integration secrets. The keys live in the database,
        // encrypted with a certificate kept outside it, so a dump alone reads none of what they protect.
        var keysDirectory = configuration["DataProtection:Directory"] is { Length: > 0 } keys ? keys
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "thobotto", "keys");
        services.AddDataProtection()
            .PersistKeysToDbContext<BotDbContext>()
            .ProtectKeysWithCertificate(KeyCertificate.LoadOrCreate(keysDirectory))
            .SetApplicationName("THOBOTTO");
        return services;
    }

    // Brings the database up to this build's migrations; one from a newer build ends the start instead.
    public static async Task MigrateOrExitAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<BotDbContext>().Database;
        var newer = NewerMigrations(await database.GetAppliedMigrationsAsync(), database.GetMigrations());
        if (newer.Count > 0)
        {
            app.Logger.LogError("The database is newer than this build: {Migrations}; run the image that made it, or a newer one. If that build was abandoned, delete its row from \"__EFMigrationsHistory\" and drop what it added.", string.Join(", ", newer));
            await app.DisposeAsync();
            Environment.Exit(1);
        }
        await database.MigrateAsync();
    }

    // The migrations the database holds that this build doesn't know: an older image on a newer database,
    // which would start and then fail on changed tables.
    public static IReadOnlyList<string> NewerMigrations(IEnumerable<string> applied, IEnumerable<string> known)
        => applied.Except(known).Order().ToList();
}
