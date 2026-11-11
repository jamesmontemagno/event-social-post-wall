using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Data;

public sealed class EventWallDbContext(DbContextOptions<EventWallDbContext> options) : DbContext(options)
{
    public DbSet<EventSettings> Events => Set<EventSettings>();
    public DbSet<EventPost> Posts => Set<EventPost>();
    public DbSet<ModerationAction> ModerationActions => Set<ModerationAction>();
    public DbSet<ConnectorDailyUsage> ConnectorDailyUsage => Set<ConnectorDailyUsage>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<BlockedAuthor> BlockedAuthors => Set<BlockedAuthor>();
    public DbSet<ConnectorCheckpoint> ConnectorCheckpoints => Set<ConnectorCheckpoint>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<EventPromo> Promos => Set<EventPromo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventSettings>(entity =>
        {
            entity.HasKey(item => item.Slug);
            entity.Property(item => item.Slug).HasMaxLength(80);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Revision).IsConcurrencyToken();
            entity.Property(item => item.PostFiltersJson).HasMaxLength(14000).HasDefaultValue("{}").IsRequired();
            entity.Property(item => item.FilterSettingsVersion).IsConcurrencyToken();
            entity.Property(item => item.YouTubeCollectionRunning).HasDefaultValue(false).IsConcurrencyToken();
        });

        modelBuilder.Entity<EventPost>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EventSlug).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Source).HasConversion<string>().HasMaxLength(24);
            entity.Property(item => item.ExternalId).HasMaxLength(240).IsRequired();
            entity.Property(item => item.Text).HasMaxLength(2000).IsRequired();
            entity.Property(item => item.AuthorName).HasMaxLength(120).IsRequired();
            entity.Property(item => item.AuthorExternalId).HasMaxLength(200);
            entity.Property(item => item.AuthorProfileUrl).HasMaxLength(2048);
            entity.Property(item => item.AuthorAvatarUrl).HasMaxLength(2048);
            entity.Property(item => item.ImagesJson).HasMaxLength(14000).HasDefaultValue("[]").IsRequired();
            entity.Property(item => item.SourceUrl).HasMaxLength(2048);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.FilterReasonsJson).HasMaxLength(500).HasDefaultValue("[]").IsRequired();
            entity.HasIndex(item => new { item.EventSlug, item.Source, item.ExternalId }).IsUnique();
            entity.HasIndex(item => new { item.EventSlug, item.Status, item.ReceivedAtUtc });
            entity.HasIndex(item => new { item.EventSlug, item.Status, item.PublishedRevision });
        });

        modelBuilder.Entity<EventPromo>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EventSlug).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Url).HasMaxLength(2048);
            entity.Property(item => item.Active).HasDefaultValue(true);
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => new { item.EventSlug, item.CreatedAtUtc, item.Id });
        });

        modelBuilder.Entity<ModerationAction>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FromStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(item => item.ToStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(item => item.StaffSessionId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.StaffUsername).HasMaxLength(40);
            entity.HasIndex(item => item.StaffMemberId);
            entity.Property(item => item.Reason).HasMaxLength(250);
            entity.HasIndex(item => new { item.PostId, item.CreatedAtUtc });
        });

        modelBuilder.Entity<ConnectorDailyUsage>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(64);
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ProviderCredential>(entity =>
        {
            entity.HasKey(item => item.Provider);
            entity.Property(item => item.Provider).HasMaxLength(24);
            entity.Property(item => item.ProtectedRefreshToken).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<BlockedAuthor>(entity =>
        {
            entity.HasKey(item => new { item.EventSlug, item.Source, item.AuthorExternalId });
            entity.Property(item => item.EventSlug).HasMaxLength(80);
            entity.Property(item => item.Source).HasConversion<string>().HasMaxLength(24);
            entity.Property(item => item.AuthorExternalId).HasMaxLength(200);
            entity.Property(item => item.StaffSessionId).HasMaxLength(64).IsRequired();
            entity.Property(item => item.StaffUsername).HasMaxLength(40);
        });

        modelBuilder.Entity<StaffMember>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Username).HasMaxLength(40).IsRequired();
            entity.Property(item => item.NormalizedUsername).HasMaxLength(40).IsRequired();
            entity.Property(item => item.AccessCodeHash).HasMaxLength(512).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => item.NormalizedUsername).IsUnique();
        });

        modelBuilder.Entity<ConnectorCheckpoint>(entity =>
        {
            entity.HasKey(item => item.Provider);
            entity.Property(item => item.Provider).HasMaxLength(24);
        });
    }
}
