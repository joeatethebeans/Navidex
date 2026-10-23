using Microsoft.EntityFrameworkCore;

namespace Navidex.Core.Data;

public class NavidexDbContext(DbContextOptions<NavidexDbContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<EditionTrack> EditionTracks => Set<EditionTrack>();
    public DbSet<ProviderIdMapping> Crosswalk => Set<ProviderIdMapping>();
    public DbSet<LibraryFile> LibraryFiles => Set<LibraryFile>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<ListenNowItem> ListenNowItems => Set<ListenNowItem>();
    public DbSet<PluginInstance> PluginInstances => Set<PluginInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Artist>(e =>
        {
            e.HasIndex(a => a.Name);
        });

        modelBuilder.Entity<Album>(e =>
        {
            e.HasOne(a => a.Artist).WithMany(a => a.Albums).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => a.Title);
        });

        modelBuilder.Entity<Edition>(e =>
        {
            e.HasOne(ed => ed.Album).WithMany(a => a.Editions).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Track>(e =>
        {
            e.HasOne(t => t.Artist).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.Title);
        });

        modelBuilder.Entity<EditionTrack>(e =>
        {
            e.HasKey(et => new { et.EditionId, et.Disc, et.Position });
            e.HasOne(et => et.Edition).WithMany(ed => ed.Tracks).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(et => et.Track).WithMany(t => t.Appearances).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(et => et.TrackId);
        });

        modelBuilder.Entity<ProviderIdMapping>(e =>
        {
            e.ToTable("Crosswalk");
            // A provider ID identifies exactly one entity of a kind; an entity can have many provider IDs.
            e.HasIndex(m => new { m.EntityKind, m.Provider, m.Value }).IsUnique();
            e.HasIndex(m => new { m.EntityKind, m.EntityId });
        });

        modelBuilder.Entity<LibraryFile>(e =>
        {
            e.HasIndex(f => f.Path).IsUnique();
            e.HasIndex(f => f.TrackId);
            e.HasIndex(f => f.EditionId);
            e.HasOne(f => f.Track).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(f => f.Edition).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Request>(e =>
        {
            e.HasIndex(r => new { r.TargetKind, r.TargetId, r.Mode });
            e.HasIndex(r => r.Status);
            e.HasOne<Request>().WithMany().HasForeignKey(r => r.ParentRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.HasOne(j => j.Request).WithMany(r => r.Jobs).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(j => j.PluginInstance).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(j => j.Status);
        });

        modelBuilder.Entity<ListenNowItem>(e =>
        {
            e.HasOne(l => l.Request).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Track).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.State, l.ExpiresAt });
        });

        modelBuilder.Entity<PluginInstance>(e =>
        {
            e.HasIndex(p => new { p.Type, p.Priority });
        });

        // Store enums as their names so the database stays readable and reordering members is safe.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (type.IsEnum)
                property.SetProviderClrType(typeof(string));
        }
    }
}
