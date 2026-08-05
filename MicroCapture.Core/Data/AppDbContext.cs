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

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={DbPath}");

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
    }
}
