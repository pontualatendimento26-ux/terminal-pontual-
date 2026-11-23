using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Pontual.Server.Data;

public sealed class PontualDb(DbContextOptions<PontualDb> options) : DbContext(options)
{
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<AdminSession> Sessions => Set<AdminSession>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();
    public DbSet<ObservedHost> ObservedHosts => Set<ObservedHost>();
    public DbSet<ChromeSite> ChromeSites => Set<ChromeSite>();
    public DbSet<ChromeDeviceSite> ChromeDeviceSites => Set<ChromeDeviceSite>();
    public DbSet<AppDecision> AppDecisions => Set<AppDecision>();
    public DbSet<DeviceApp> DeviceApps => Set<DeviceApp>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditRecord> Audit => Set<AuditRecord>();
    public DbSet<TextSetting> TextSettings => Set<TextSetting>();
    public DbSet<AssistantScript> AssistantScripts => Set<AssistantScript>();
    public DbSet<AssistantProfile> AssistantProfiles => Set<AssistantProfile>();
    public DbSet<AssistantMessage> AssistantMessages => Set<AssistantMessage>();
    public DbSet<KnowledgeLink> KnowledgeLinks => Set<KnowledgeLink>();
    public DbSet<KnowledgeSuggestion> KnowledgeSuggestions => Set<KnowledgeSuggestion>();
    public DbSet<AssistantFeedback> AssistantFeedback => Set<AssistantFeedback>();
    public DbSet<AttendantInsight> AttendantInsights => Set<AttendantInsight>();
    public DbSet<WhatsAppCapture> WhatsAppCaptures => Set<WhatsAppCapture>();
    public DbSet<CoverageReply> CoverageReplies => Set<CoverageReply>();
    public DbSet<ScreenRecording> ScreenRecordings => Set<ScreenRecording>();
    public DbSet<CrmEvent> CrmEvents => Set<CrmEvent>();
    public DbSet<CrmReply> CrmReplies => Set<CrmReply>();
    public DbSet<AssistantUsage> AssistantUsage => Set<AssistantUsage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // O SQLite devolve DateTime sem fuso; marcar como UTC faz o JSON sair com "Z".
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Admin>(e =>
        {
            e.HasIndex(a => a.Email).IsUnique();
            e.Property(a => a.Role).HasConversion<string>();
        });

        modelBuilder.Entity<AdminSession>().HasIndex(s => s.TokenHash).IsUnique();

        modelBuilder.Entity<Device>(e =>
        {
            e.HasIndex(d => d.TokenHash).IsUnique();
            e.Ignore(d => d.Label);
            e.Ignore(d => d.AppsInUse);
        });

        modelBuilder.Entity<EnrollmentCode>().HasIndex(c => c.CodeHash).IsUnique();

        modelBuilder.Entity<Item>(e =>
        {
            e.Ignore(i => i.ExtraHosts);
            e.Ignore(i => i.Audience);
            e.Ignore(i => i.Status);
        });

        modelBuilder.Entity<Alert>().HasIndex(a => a.DeviceId);

        modelBuilder.Entity<HistoryEntry>().HasIndex(h => new { h.DeviceId, h.At });

        modelBuilder.Entity<ObservedHost>().HasIndex(o => o.Host).IsUnique();

        modelBuilder.Entity<ChromeSite>().HasIndex(c => c.Host).IsUnique();

        modelBuilder.Entity<ChromeDeviceSite>().HasIndex(c => new { c.DeviceId, c.Host }).IsUnique();

        modelBuilder.Entity<AppDecision>().HasIndex(a => a.Exe).IsUnique();

        modelBuilder.Entity<DeviceApp>().HasIndex(a => new { a.DeviceId, a.Exe }).IsUnique();

        modelBuilder.Entity<Setting>().HasKey(s => s.Key);

        modelBuilder.Entity<TextSetting>().HasKey(s => s.Key);

        modelBuilder.Entity<AssistantProfile>(e =>
        {
            e.HasKey(p => p.DeviceId);
            e.Property(p => p.DeviceId).ValueGeneratedNever();
        });

        modelBuilder.Entity<AssistantMessage>().HasIndex(m => new { m.DeviceId, m.Id });

        modelBuilder.Entity<AssistantUsage>().HasIndex(u => u.At);

        modelBuilder.Entity<ScreenRecording>(e =>
        {
            e.HasIndex(r => new { r.DeviceId, r.StartedAt });
            e.HasIndex(r => r.StartedAt);
        });

        modelBuilder.Entity<KnowledgeLink>().HasIndex(l => new { l.FromId, l.ToId }).IsUnique();

        modelBuilder.Entity<KnowledgeSuggestion>().HasIndex(s => s.Status);

        modelBuilder.Entity<AssistantFeedback>().HasIndex(f => new { f.DeviceId, f.MessageId }).IsUnique();

        modelBuilder.Entity<WhatsAppCapture>().HasIndex(c => new { c.DeviceId, c.Contact }).IsUnique();
        modelBuilder.Entity<CrmEvent>().HasIndex(e => new { e.ConversationId, e.Id });
        modelBuilder.Entity<CrmReply>(e =>
        {
            e.HasIndex(r => r.ConversationId);
            e.HasIndex(r => new { r.Status, r.CreatedAt });
        });

        modelBuilder.Entity<CoverageReply>(e =>
        {
            e.HasIndex(r => new { r.DeviceId, r.Status });
            e.HasIndex(r => new { r.DeviceId, r.Contact });
        });

        modelBuilder.Entity<AttendantInsight>(e =>
        {
            e.HasKey(i => i.DeviceId);
            e.Property(i => i.DeviceId).ValueGeneratedNever();
        });

        modelBuilder.Entity<AuditRecord>(e =>
        {
            e.HasKey(a => a.Sequence);
            e.Property(a => a.Sequence).ValueGeneratedNever();
            e.Property(a => a.ActorType).HasConversion<string>();
            e.Ignore(a => a.OccurredAt);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
