using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Backups;
using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Fame;
using THOBOTTO.GameServers;
using THOBOTTO.Gate;
using THOBOTTO.Games;
using THOBOTTO.Helpers;
using THOBOTTO.Integrations;
using THOBOTTO.Lastfm;
using THOBOTTO.Listening;
using THOBOTTO.Mischief;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Notifications;
using THOBOTTO.Points;
using THOBOTTO.Quotes;
using THOBOTTO.Sounds;
using THOBOTTO.Stats;
using THOBOTTO.Voice;

namespace THOBOTTO.Data;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<EnabledModule> EnabledModules => Set<EnabledModule>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<ModuleSettings> ModuleSettings => Set<ModuleSettings>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // The web panel's cookie keys, so logins survive a redeploy.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

    public DbSet<HelperAccount> HelperAccounts => Set<HelperAccount>();

    public DbSet<Personality> Personalities => Set<Personality>();

    public DbSet<HelperAssignment> HelperAssignments => Set<HelperAssignment>();

    public DbSet<SavedMusicPlayer> SavedMusicPlayers => Set<SavedMusicPlayer>();

    public DbSet<SavedPlaylist> SavedPlaylists => Set<SavedPlaylist>();

    public DbSet<LastfmLink> LastfmLinks => Set<LastfmLink>();

    public DbSet<PlayRecord> PlayRecords => Set<PlayRecord>();

    public DbSet<PlayListener> PlayListeners => Set<PlayListener>();

    public DbSet<VoiceSession> VoiceSessions => Set<VoiceSession>();

    public DbSet<ArtistGenres> ArtistGenres => Set<ArtistGenres>();

    public DbSet<WrappedOptIn> WrappedOptIns => Set<WrappedOptIn>();

    public DbSet<IntegrationSetting> IntegrationSettings => Set<IntegrationSetting>();

    public DbSet<WrappedPost> WrappedPosts => Set<WrappedPost>();

    public DbSet<StoredBackup> StoredBackups => Set<StoredBackup>();

    public DbSet<VoicePreference> VoicePreferences => Set<VoicePreference>();

    public DbSet<TalkTime> TalkTimes => Set<TalkTime>();

    public DbSet<PointAccount> PointAccounts => Set<PointAccount>();

    public DbSet<PointEntry> PointEntries => Set<PointEntry>();

    public DbSet<VoiceHub> VoiceHubs => Set<VoiceHub>();

    public DbSet<DynamicVoiceChannel> DynamicVoiceChannels => Set<DynamicVoiceChannel>();

    public DbSet<ModAppeal> ModAppeals => Set<ModAppeal>();

    public DbSet<GateHold> GateHolds => Set<GateHold>();

    public DbSet<GateRaid> GateRaids => Set<GateRaid>();

    public DbSet<Sound> Sounds => Set<Sound>();

    public DbSet<SoundVote> SoundVotes => Set<SoundVote>();

    public DbSet<JoinSound> JoinSounds => Set<JoinSound>();

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
        modelBuilder.Entity<HelperAccount>(e =>
        {
            e.HasKey(a => a.UserId);
            e.Property(a => a.UserId).ValueGeneratedNever();
        });
        modelBuilder.Entity<HelperAssignment>().HasKey(a => new { a.GuildId, a.HelperId });
        modelBuilder.Entity<SavedMusicPlayer>(e =>
        {
            e.HasKey(p => new { p.GuildId, p.VoiceChannelId });
            e.Property(p => p.State).HasColumnType("jsonb");
        });
        modelBuilder.Entity<LastfmLink>().HasKey(l => l.UserId);
        modelBuilder.Entity<PlayRecord>().HasIndex(p => new { p.GuildId, p.StartedAt });
        modelBuilder.Entity<PlayListener>(e =>
        {
            e.HasKey(l => new { l.PlayId, l.UserId });
            e.HasIndex(l => l.UserId);
            e.HasOne<PlayRecord>().WithMany().HasForeignKey(l => l.PlayId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ArtistGenres>().HasKey(g => g.Artist);
        modelBuilder.Entity<IntegrationSetting>().HasKey(s => s.Key);
        modelBuilder.Entity<WrappedPost>().HasKey(p => new { p.GuildId, p.Key });
        modelBuilder.Entity<StoredBackup>().HasIndex(b => new { b.GuildId, b.CreatedAt });
        modelBuilder.Entity<VoicePreference>().HasKey(p => new { p.GuildId, p.UserId });
        modelBuilder.Entity<TalkTime>().HasKey(t => new { t.GuildId, t.UserId, t.Day });
        modelBuilder.Entity<WrappedOptIn>().HasKey(o => new { o.GuildId, o.UserId });
        modelBuilder.Entity<VoiceSession>(e =>
        {
            e.HasIndex(s => new { s.GuildId, s.JoinedAt });
            e.HasIndex(s => new { s.GuildId, s.UserId });
        });
        modelBuilder.Entity<SavedPlaylist>(e =>
        {
            e.HasIndex(p => new { p.GuildId, p.Key }).IsUnique();
            e.Property(p => p.Tracks).HasColumnType("jsonb");
        });
        modelBuilder.Entity<HelperAssignment>().HasOne<Personality>().WithMany().HasForeignKey(a => a.PersonalityId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Personality>(e =>
        {
            e.HasIndex(p => p.GuildId);
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
            e.Property(p => p.Voices)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => VoicesJson(v),
                    v => ParseVoices(v),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<Dictionary<string, string>>(
                        (a, b) => VoicesJson(a!) == VoicesJson(b!),
                        v => VoicesJson(v).GetHashCode(),
                        v => ParseVoices(VoicesJson(v))));
        });
        modelBuilder.Entity<ModAppeal>().HasIndex(a => new { a.GuildId, a.UserId });
        modelBuilder.Entity<GateHold>().HasKey(h => new { h.GuildId, h.UserId });
        modelBuilder.Entity<Sound>().HasIndex(s => new { s.GuildId, s.State });
        modelBuilder.Entity<SoundVote>().HasKey(v => new { v.SoundId, v.UserId });
        modelBuilder.Entity<JoinSound>().HasKey(j => new { j.GuildId, j.UserId });
        modelBuilder.Entity<GateRaid>(e =>
        {
            e.HasKey(r => r.GuildId);
            e.Property(r => r.GuildId).ValueGeneratedNever();
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

    private static string VoicesJson(Dictionary<string, string> v) => System.Text.Json.JsonSerializer.Serialize(v, System.Text.Json.JsonSerializerOptions.Web);

    private static Dictionary<string, string> ParseVoices(string v)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, System.Text.Json.JsonSerializerOptions.Web)!;
}
