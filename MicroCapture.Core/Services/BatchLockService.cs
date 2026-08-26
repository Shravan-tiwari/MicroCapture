using System.Text.Json;
using MicroCapture.Core.Models;

namespace MicroCapture.Core.Services;

/// <summary>Who currently has a batch open. Advisory only.</summary>
public class BatchLockInfo
{
    public string Machine { get; set; } = Environment.MachineName;
    public string User { get; set; } = Environment.UserName;
    public DateTime AcquiredUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Refreshed while the batch stays open. Its age is what separates "someone is
    /// working in here right now" from "a machine died holding this".</summary>
    public DateTime HeartbeatUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Advisory batch locking for shared folders.
///
/// <para>Deliberately advisory: the operator is told who else has the batch open and may proceed
/// anyway. Hard locking would strand a batch whenever someone left it open and walked away, and a
/// lock file on a USB stick is left behind by definition — the drive gets unplugged, never
/// "released" — so a stale lock has to read as routine rather than as an error.</para>
///
/// <para>This does not prevent two machines capturing into one batch simultaneously; nothing here
/// can. It makes the situation visible. See <see cref="BatchManifestService.NextPageNumber"/> for
/// the page-numbering safety net that goes with it.</para></summary>
public class BatchLockService
{
    /// <summary>Beyond this, a lock is treated as abandoned. Comfortably longer than the heartbeat
    /// interval so a briefly-busy machine is never mistaken for a dead one.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>How often a holder should refresh its heartbeat.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>The current holder, or null if the batch is free. A lock older than
    /// <see cref="StaleAfter"/> is reported as stale via <paramref name="isStale"/> rather than
    /// hidden, so the caller can word the prompt as a takeover rather than a conflict.</summary>
    public BatchLockInfo? Read(string batchFolder, out bool isStale)
    {
        isStale = false;
        try
        {
            var path = BatchFolder.LockPath(batchFolder);
            if (!File.Exists(path)) return null;
            var info = JsonSerializer.Deserialize<BatchLockInfo>(File.ReadAllText(path), SerializerOptions);
            if (info == null) return null;
            isStale = DateTime.UtcNow - info.HeartbeatUtc > StaleAfter;
            return info;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when the lock is held by a different machine and still being refreshed.
    /// This machine's own leftover lock never counts as held by someone else — reopening a batch
    /// you had open yourself must not prompt.</summary>
    public bool IsHeldByAnother(string batchFolder, out BatchLockInfo? holder)
    {
        holder = Read(batchFolder, out var isStale);
        if (holder == null || isStale) return false;
        return !string.Equals(holder.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Claims the batch for this machine, overwriting any existing lock. Failure is
    /// ignored: a read-only share or a permissions problem must not stop an operator opening a
    /// batch, since the lock is advisory to begin with.</summary>
    public void Acquire(string batchFolder)
    {
        try
        {
            BatchFolder.EnsureLayout(batchFolder);
            var info = new BatchLockInfo();
            File.WriteAllText(BatchFolder.LockPath(batchFolder), JsonSerializer.Serialize(info, SerializerOptions));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Refreshes this machine's heartbeat, so other machines can tell the batch is still
    /// actively in use. No-op when the lock has since been taken over by someone else — quietly
    /// stealing it back would defeat the takeover the other operator was told had succeeded.</summary>
    public void Heartbeat(string batchFolder)
    {
        try
        {
            var current = Read(batchFolder, out _);
            if (current != null && !string.Equals(current.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return;
            var info = current ?? new BatchLockInfo();
            info.HeartbeatUtc = DateTime.UtcNow;
            File.WriteAllText(BatchFolder.LockPath(batchFolder), JsonSerializer.Serialize(info, SerializerOptions));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Releases this machine's claim on a clean close. Leaves another machine's lock
    /// alone.</summary>
    public void Release(string batchFolder)
    {
        try
        {
            var current = Read(batchFolder, out _);
            if (current != null && !string.Equals(current.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return;
            var path = BatchFolder.LockPath(batchFolder);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Operator-facing description of a conflict, for the warning shown before an
    /// override.</summary>
    public static string DescribeHolder(BatchLockInfo holder)
    {
        var age = DateTime.UtcNow - holder.HeartbeatUtc;
        var when = age < TimeSpan.FromMinutes(2) ? "just now" : $"{(int)age.TotalMinutes} minutes ago";
        return $"{holder.User} on {holder.Machine} (last active {when})";
    }
}
