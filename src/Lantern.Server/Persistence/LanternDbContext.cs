using Microsoft.EntityFrameworkCore;

namespace Lantern.Server.Persistence;

public sealed class LanternDbContext(DbContextOptions<LanternDbContext> options) : DbContext(options)
{
    public DbSet<RoomRow> Rooms => Set<RoomRow>();
    public DbSet<RoomMemberRow> RoomMembers => Set<RoomMemberRow>();
    public DbSet<JournalRow> Journal => Set<JournalRow>();
    public DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RoomRow>().HasKey(r => r.RoomCode);

        b.Entity<RoomMemberRow>().HasKey(m => new { m.RoomCode, m.PlayerId });

        b.Entity<JournalRow>().HasKey(j => new { j.RoomCode, j.Seq });
        b.Entity<JournalRow>()
            .HasIndex(j => new { j.RoomCode, j.ClientIntentId })
            .IsUnique()
            .HasFilter("[ClientIntentId] IS NOT NULL");

        b.Entity<SnapshotRow>().HasKey(s => new { s.RoomCode, s.Seq });
    }
}
