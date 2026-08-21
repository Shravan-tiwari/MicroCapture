using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Models;
using System.IO;
using System;

namespace MicroCapture.Core.Data;

public class AppDbContext : DbContext
{
    public DbSet<Project> Projects { get; set; } = null!;
    public DbSet<Batch> Batches { get; set; } = null!;
    public DbSet<CaptureJob> CaptureJobs { get; set; } = null!;
    public DbSet<CameraCalibration> CameraCalibrations { get; set; } = null!;

    public string DbPath { get; }

    public AppDbContext() : this(DefaultDbPath())
    {
    }

    /// <summary>Points the context at a specific SQLite file — used by tests/tools that must
    /// not touch the operator's real database.</summary>
    public AppDbContext(string dbPath)
    {
        DbPath = dbPath;
    }

    private static string DefaultDbPath()
    {
        var folder = Environment.SpecialFolder.LocalApplicationData;
        var path = Environment.GetFolderPath(folder);
        // Using a persistent local app data path for offline database
        return System.IO.Path.Join(path, "MicroCapture.db");
    }

    // "Default Timeout" sets SQLite's busy_timeout (seconds) on every connection this context
    // opens, so a query here that lands while BackgroundProcessingWorker's separate connection
    // is mid-write waits and retries instead of throwing SQLITE_BUSY immediately. Without this,
    // EnsureColumn's own "PRAGMA busy_timeout=5000" (CaptureQueueService) only ever applied to
    // the one connection instance used during startup migrations — every other query (e.g.
    // RecentBatchesViewModel.LoadAsync) opened its own connection with SQLite's 0-second default,
    // so an unlucky read during a concurrent worker write threw uncaught, silently no-opping
    // whatever command triggered it (confirmed cause of "Recent" appearing to do nothing).
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath};Default Timeout=5");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>()
            .HasMany(p => p.Batches)
            .WithOne(b => b.Project)
            .HasForeignKey(b => b.ProjectId);

        modelBuilder.Entity<Batch>()
            .HasMany(b => b.Captures)
            .WithOne(c => c.Batch)
            .HasForeignKey(c => c.BatchId);

        modelBuilder.Entity<Batch>()
            .HasOne(b => b.CameraCalibration)
            .WithMany()
            .HasForeignKey(b => b.CameraCalibrationId)
            .IsRequired(false);
    }
}
