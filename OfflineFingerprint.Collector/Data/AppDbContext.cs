using Microsoft.EntityFrameworkCore;
using OfflineFingerprint.Collector.Models;

namespace OfflineFingerprint.Collector.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<FingerprintImage> FingerprintImages => Set<FingerprintImage>();
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Person>().HasIndex(x => x.PersonCode).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<FingerprintImage>()
            .HasOne(x => x.Person).WithMany(x => x.FingerprintImages)
            .HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FingerprintImage>()
            .HasIndex(x => new { x.PersonId, x.FingerCode, x.Position, x.SequenceNo });
    }
}
