using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stay.Admin.Domain.Auditing;

namespace Stay.Admin.Infrastructure.Persistence;

/// <summary>EF Core context for the admin bounded context (owns the <c>admin</c> schema).</summary>
public sealed class AdminDbContext(DbContextOptions<AdminDbContext> options) : DbContext(options)
{
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("admin");

        modelBuilder.Entity<AuditLogEntry>(b =>
        {
            b.ToTable("audit_log");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(a => a.ActorSub).HasColumnName("actor_sub");
            b.Property(a => a.Action).HasColumnName("action");
            b.Property(a => a.EntityType).HasColumnName("entity_type");
            b.Property(a => a.EntityId).HasColumnName("entity_id");
            b.Property(a => a.Before).HasColumnName("before").HasColumnType("jsonb");
            b.Property(a => a.After).HasColumnName("after").HasColumnType("jsonb");
            b.Property(a => a.Reason).HasColumnName("reason");
            b.Property(a => a.CreatedAt).HasColumnName("created_at")
                .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<ProcessedEvent>(b =>
        {
            b.ToTable("processed_event");
            b.HasKey(e => e.EventId);
            b.Property(e => e.EventId).HasColumnName("event_id");
            b.Property(e => e.ProcessedAt).HasColumnName("processed_at")
                .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });
    }
}
