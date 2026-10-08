using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Access;
using THOBOTTO.Expressions;
using THOBOTTO.Events;
using THOBOTTO.Fame;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Music;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Games;
using THOBOTTO.Moderation;
using THOBOTTO.GameServers;
using THOBOTTO.Voice;

namespace THOBOTTO.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<ModuleSettings> ModuleSettings => Set<ModuleSettings>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<ModCase> ModCases => Set<ModCase>();

    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();

    public DbSet<Rename> Renames => Set<Rename>();

    public DbSet<MischiefEffect> MischiefEffects => Set<MischiefEffect>();

    public DbSet<FameReaction> FameReactions => Set<FameReaction>();

    public DbSet<FameEntry> FameEntries => Set<FameEntry>();

    public DbSet<Bet> Bets => Set<Bet>();

    public DbSet<BetStake> BetStakes => Set<BetStake>();

    public DbSet<BetPayout> BetPayouts => Set<BetPayout>();

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();

    public DbSet<Expression> Expressions => Set<Expression>();

    public DbSet<ExpressionVote> ExpressionVotes => Set<ExpressionVote>();

    public DbSet<ArchivedMessage> ArchivedMessages => Set<ArchivedMessage>();

    public DbSet<MessageVersion> MessageVersions => Set<MessageVersion>();

    public DbSet<ArchivedAttachment> ArchivedAttachments => Set<ArchivedAttachment>();

    public DbSet<BackfillChannel> BackfillChannels => Set<BackfillChannel>();

    public DbSet<BackfillRun> BackfillRuns => Set<BackfillRun>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<EventRsvp> EventRsvps => Set<EventRsvp>();

    public DbSet<MemberTimeZone> MemberTimeZones => Set<MemberTimeZone>();

    public DbSet<EventTimeOption> EventTimeOptions => Set<EventTimeOption>();

    public DbSet<EventTimeVote> EventTimeVotes => Set<EventTimeVote>();

    public DbSet<EventSeries> EventSeries => Set<EventSeries>();

    public DbSet<Game> Games => Set<Game>();

    public DbSet<GamePicker> GamePickers => Set<GamePicker>();

    public DbSet<GameMode> GameModes => Set<GameMode>();

    public DbSet<HelperProfile> HelperProfiles => Set<HelperProfile>();

    public DbSet<PointAccount> PointAccounts => Set<PointAccount>();

    public DbSet<PointEntry> PointEntries => Set<PointEntry>();

    public DbSet<VoiceHub> VoiceHubs => Set<VoiceHub>();

    public DbSet<DynamicVoiceChannel> DynamicVoiceChannels => Set<DynamicVoiceChannel>();

    public DbSet<GameServer> GameServers => Set<GameServer>();

    public DbSet<ServerSettings> ServerSettings => Set<ServerSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnabledModule>().HasKey(m => new { m.GuildId, m.Module });
        modelBuilder.Entity<NotificationPreference>().HasKey(p => new { p.GuildId, p.UserId, p.Topic });
        modelBuilder.Entity<ModuleSettings>(e =>
        {
            e.HasKey(s => new { s.GuildId, s.Module });
            e.Property(s => s.Json).HasColumnType("jsonb");
        });
        modelBuilder.Entity<AuditEntry>().HasIndex(e => new { e.GuildId, e.CreatedAt });
        modelBuilder.Entity<PermissionGrant>().HasKey(g => new { g.GuildId, g.RoleId, g.Permission });
        modelBuilder.Entity<Rename>().HasIndex(r => new { r.GuildId, r.TargetId, r.CreatedAt });
        modelBuilder.Entity<MischiefEffect>().HasIndex(e => new { e.GuildId, e.TargetId, e.Kind, e.EndsAt });
        modelBuilder.Entity<FameReaction>().HasKey(r => new { r.MessageId, r.ReactorId, r.Emoji });
        modelBuilder.Entity<Bet>().HasIndex(b => new { b.GuildId, b.State });
        modelBuilder.Entity<Quote>(e =>
        {
            e.HasIndex(q => q.GuildId);
            e.HasMany(q => q.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<QuoteLine>().HasIndex(l => l.SpeakerId);
        modelBuilder.Entity<Expression>().HasIndex(e => new { e.GuildId, e.Kind, e.State });
        modelBuilder.Entity<ExpressionVote>().HasKey(v => new { v.ExpressionId, v.UserId });
        modelBuilder.Entity<ArchivedMessage>(e =>
        {
            e.Property(m => m.Id).ValueGeneratedNever();
            e.Property(m => m.Raw).HasColumnType("jsonb");
            e.HasIndex(m => new { m.GuildId, m.ChannelId, m.Id });
            e.HasIndex(m => new { m.GuildId, m.AuthorId });
        });
        modelBuilder.Entity<MessageVersion>(e =>
        {
            e.Property(v => v.Raw).HasColumnType("jsonb");
            e.HasIndex(v => v.MessageId);
        });
        modelBuilder.Entity<Event>().HasIndex(e => new { e.GuildId, e.State, e.StartsAt });
        modelBuilder.Entity<EventRsvp>().HasKey(r => new { r.EventId, r.UserId });
        modelBuilder.Entity<EventTimeOption>().HasIndex(o => o.EventId);
        modelBuilder.Entity<EventTimeVote>().HasKey(v => new { v.OptionId, v.UserId });
        modelBuilder.Entity<Event>().HasIndex(e => new { e.SeriesId, e.StartsAt });
        modelBuilder.Entity<Game>().HasIndex(g => new { g.GuildId, g.RoleId });
        modelBuilder.Entity<GameMode>().HasIndex(m => new { m.GameId, m.Name }).IsUnique();
        modelBuilder.Entity<ModCase>().HasIndex(c => new { c.GuildId, c.Number }).IsUnique();
        modelBuilder.Entity<ModCase>().HasIndex(c => new { c.GuildId, c.TargetId });
        modelBuilder.Entity<GameMode>().HasOne<Game>().WithMany().HasForeignKey(m => m.GameId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<HelperProfile>(e =>
        {
            e.HasKey(p => p.UserId);
            e.Property(p => p.UserId).ValueGeneratedNever();
            // Compared and snapshotted as JSON, so changes inside the dictionary are noticed.
            e.Property(p => p.Phrases)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => PhrasesJson(v),
                    v => ParsePhrases(v),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<Dictionary<string, List<string>>>(
                        (a, b) => PhrasesJson(a!) == PhrasesJson(b!),
                        v => PhrasesJson(v).GetHashCode(),
                        v => ParsePhrases(PhrasesJson(v))));
        });
        modelBuilder.Entity<GamePicker>(e =>
        {
            e.HasKey(p => p.MessageId);
            e.Property(p => p.MessageId).ValueGeneratedNever();
        });
        modelBuilder.Entity<MemberTimeZone>(e =>
        {
            e.HasKey(z => z.UserId);
            e.Property(z => z.UserId).ValueGeneratedNever();
        });
        modelBuilder.Entity<BackfillChannel>(e =>
        {
            e.HasKey(c => c.ChannelId);
            e.Property(c => c.ChannelId).ValueGeneratedNever();
            e.HasIndex(c => c.GuildId);
        });
        modelBuilder.Entity<BackfillRun>(e =>
        {
            e.HasKey(r => r.GuildId);
            e.Property(r => r.GuildId).ValueGeneratedNever();
        });
        modelBuilder.Entity<ArchivedAttachment>(e =>
        {
            e.Property(a => a.Id).ValueGeneratedNever();
            e.HasIndex(a => a.MessageId);
        });
        modelBuilder.Entity<BetStake>().HasIndex(s => new { s.BetId, s.UserId });
        modelBuilder.Entity<BetPayout>().HasIndex(p => p.BetId);
        modelBuilder.Entity<FameEntry>(e =>
        {
            e.HasKey(f => f.MessageId);
            e.Property(f => f.MessageId).ValueGeneratedNever();
            e.HasIndex(f => new { f.GuildId, f.InductedAt });
        });
        modelBuilder.Entity<PointAccount>().HasKey(a => new { a.GuildId, a.UserId });
        modelBuilder.Entity<PointEntry>().HasIndex(e => new { e.GuildId, e.UserId, e.CreatedAt });

        modelBuilder.Entity<VoiceHub>(e =>
        {
            e.HasKey(h => h.ChannelId);
            e.Property(h => h.ChannelId).ValueGeneratedNever();
            e.HasIndex(h => h.GuildId);
        });
        modelBuilder.Entity<DynamicVoiceChannel>(e =>
        {
            e.HasKey(c => c.ChannelId);
            e.Property(c => c.ChannelId).ValueGeneratedNever();
            e.HasIndex(c => c.GuildId);
        });
        modelBuilder.Entity<GameServer>(e =>
        {
            e.HasIndex(s => s.GuildId);
            e.Ignore(s => s.Address);
        });
        modelBuilder.Entity<ServerSettings>(e =>
        {
            e.HasKey(s => s.GuildId);
            e.Property(s => s.GuildId).ValueGeneratedNever();
        });
    }

    private static string PhrasesJson(Dictionary<string, List<string>> v) => System.Text.Json.JsonSerializer.Serialize(v, System.Text.Json.JsonSerializerOptions.Web);

    private static Dictionary<string, List<string>> ParsePhrases(string v)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, System.Text.Json.JsonSerializerOptions.Web)!;
}
