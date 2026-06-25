using HD_AMR.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD_AMR.Data;

public class HdAmrDbContext : DbContext
{
    public HdAmrDbContext(DbContextOptions<HdAmrDbContext> options) : base(options)
    {
    }

    public DbSet<Drawing> Drawings => Set<Drawing>();
    public DbSet<DrawingSegment> DrawingSegments => Set<DrawingSegment>();
    public DbSet<ExcludedRegion> ExcludedRegions => Set<ExcludedRegion>();
    public DbSet<InspectionProfile> InspectionProfiles => Set<InspectionProfile>();
    public DbSet<TeachingPosition> TeachingPositions => Set<TeachingPosition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Drawing>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.Name).IsRequired().HasMaxLength(200);
            b.Property(d => d.FileName).IsRequired().HasMaxLength(260);
            b.Property(d => d.FilePath).IsRequired().HasMaxLength(500);
            b.Property(d => d.DxfPath).HasMaxLength(500);
            b.Property(d => d.ConversionError).HasMaxLength(1000);
            b.HasMany(d => d.Segments)
                .WithOne(s => s.Drawing!)
                .HasForeignKey(s => s.DrawingId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasMany(d => d.ExcludedRegions)
                .WithOne(r => r.Drawing!)
                .HasForeignKey(r => r.DrawingId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasMany<InspectionProfile>()
                .WithOne(p => p.Drawing!)
                .HasForeignKey(p => p.DrawingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DrawingSegment>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.DrawingId, s.Number });
        });

        modelBuilder.Entity<ExcludedRegion>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.DrawingId);
        });

        modelBuilder.Entity<InspectionProfile>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired().HasMaxLength(200);
            b.Property(p => p.WaypointsJson).IsRequired();
            b.HasIndex(p => p.DrawingId);
        });

        modelBuilder.Entity<TeachingPosition>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Key).IsRequired().HasMaxLength(100);
            b.Property(p => p.Name).IsRequired().HasMaxLength(200);
            b.HasIndex(p => p.Key).IsUnique();
            b.Ignore(p => p.IsTaught);
        });
    }
}
