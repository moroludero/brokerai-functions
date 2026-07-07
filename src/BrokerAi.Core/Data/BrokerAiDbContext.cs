using System.Text.Json;
using BrokerAi.Core.Data.Entities;
using BrokerAi.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BrokerAi.Core.Data;

public class BrokerAiDbContext(DbContextOptions<BrokerAiDbContext> options) : DbContext(options)
{
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<AdCampaign> AdCampaigns => Set<AdCampaign>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Enums stored as lowercase strings, matching the old Supabase values
        // (basico, hot, venta, ...) so data stays human-readable.
        var planConv = LowercaseEnum<PlanTier>();
        var leadStatusConv = LowercaseEnum<LeadStatus>();
        var sessionTypeConv = LowercaseEnum<SessionType>();
        var leadGoalConv = LowercaseEnum<LeadGoal>();
        var propertyKindConv = LowercaseEnum<PropertyKind>();
        var listingTypeConv = LowercaseEnum<ListingType>();
        var campaignStatusConv = new ValueConverter<CampaignStatus, string>(
            v => ToSnake(v.ToString()),
            v => Enum.Parse<CampaignStatus>(v.Replace("_", ""), true));

        mb.Entity<Broker>(e =>
        {
            e.Property(b => b.Plan).HasConversion(planConv).HasMaxLength(20);
            e.HasIndex(b => b.PhoneNumberId).IsUnique().HasFilter("[PhoneNumberId] IS NOT NULL");
            e.HasIndex(b => b.WhatsappNumber).IsUnique().HasFilter("[WhatsappNumber] IS NOT NULL");
            e.HasIndex(b => b.AlertNumber);
        });

        mb.Entity<Lead>(e =>
        {
            e.Property(l => l.Status).HasConversion(leadStatusConv).HasMaxLength(20);
            e.Property(l => l.Goal).HasConversion(leadGoalConv!).HasMaxLength(20);
            // FIX vs old design: the upsert-by-(broker_id, phone) the docs relied on
            // was impossible without this unique constraint.
            e.HasIndex(l => new { l.BrokerId, l.Phone }).IsUnique();
            e.HasIndex(l => l.Status);
            e.HasOne(l => l.Broker).WithMany(b => b.Leads)
                .HasForeignKey(l => l.BrokerId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<Session>(e =>
        {
            e.Property(s => s.Type).HasConversion(sessionTypeConv).HasMaxLength(20);
            e.Property(s => s.Step).HasMaxLength(40);
            e.Property(s => s.Context)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOpts),
                    v => JsonSerializer.Deserialize<SessionContext>(v, JsonOpts) ?? new SessionContext())
                .HasColumnType("nvarchar(max)");
            // FIX: one session per person per broker — makes get-or-create race-safe.
            e.HasIndex(s => new { s.BrokerId, s.Phone }).IsUnique();
            e.HasOne(s => s.Broker).WithMany()
                .HasForeignKey(s => s.BrokerId).OnDelete(DeleteBehavior.Cascade);
            // NoAction instead of SetNull: SQL Server rejects multiple cascade paths
            // (Broker→Sessions cascade + Broker→Leads→Sessions). Broker deletion still
            // removes sessions via the direct cascade; leads are never deleted alone.
            e.HasOne(s => s.Lead).WithMany()
                .HasForeignKey(s => s.LeadId).OnDelete(DeleteBehavior.NoAction);
        });

        mb.Entity<Property>(e =>
        {
            e.Property(p => p.Kind).HasConversion(propertyKindConv!).HasMaxLength(20);
            e.Property(p => p.ListingKind).HasConversion(listingTypeConv).HasMaxLength(20);
            e.HasIndex(p => p.ShortCode).IsUnique().HasFilter("[ShortCode] IS NOT NULL");
            e.HasIndex(p => new { p.BrokerId, p.Active });
            e.HasOne(p => p.Broker).WithMany(b => b.Properties)
                .HasForeignKey(p => p.BrokerId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<AdCampaign>(e =>
        {
            e.Property(c => c.Status).HasConversion(campaignStatusConv).HasMaxLength(20);
            e.HasIndex(c => new { c.BrokerId, c.Status });
            e.HasOne(c => c.Broker).WithMany()
                .HasForeignKey(c => c.BrokerId).OnDelete(DeleteBehavior.Cascade);
            // NoAction for the same multiple-cascade-path reason as Sessions→Leads.
            e.HasOne(c => c.Property).WithMany()
                .HasForeignKey(c => c.PropertyId).OnDelete(DeleteBehavior.NoAction);
        });

        mb.Entity<ProcessedMessage>(e =>
        {
            e.Property(m => m.MessageId).HasMaxLength(128);
            e.HasIndex(m => m.MessageId).IsUnique();
        });
    }

    private static ValueConverter<T, string> LowercaseEnum<T>() where T : struct, Enum =>
        new(v => v.ToString().ToLowerInvariant(),
            v => Enum.Parse<T>(v, true));

    private static string ToSnake(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
