using System.Text;

namespace McaGit.Repo;

/// <summary>Thrown when another process already holds the repository lock.</summary>
public sealed class RepoLockedException(string message) : Exception(message);

/// <summary>
/// A coarse inter-process lock over a repository, git's <c>index.lock</c> model: a mutating
/// command (commit / push) takes it for the duration so two concurrent invocations can't race
/// branch advancement (whose write is last-writer-wins) and silently drop a commit. It is
/// <b>fail-fast</b> — a second holder gets <see cref="RepoLockedException"/> rather than blocking,
/// which is exactly what a scheduled backup driver wants (skip the tick, log it).
/// </summary>
/// <remarks>
/// The lock is the OS advisory lock on an open <see cref="FileStream"/> opened with
/// <see cref="FileShare.None"/>, so it is released automatically if the process crashes — a
/// leftover <c>mcagit.lock</c> file never wedges the repo. The file's contents (pid / time /
/// host / operation) are purely informational.
/// </remarks>
public sealed class RepoLock : IDisposable
{
    public const string FileName = "mcagit.lock";
    private FileStream? _stream;

    private RepoLock(FileStream stream) => _stream = stream;

    public static RepoLock Acquire(string repoDir, string operation)
    {
        string path = Path.Combine(repoDir, FileName);
        FileStream stream;
        try
        {
            // FileShare.None ⇒ an OS advisory lock held for this handle's lifetime. A crash releases
            // it (the OS closes the handle), so OpenOrCreate transparently reuses a stale lock file.
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new RepoLockedException(
                "repository is locked by another mcagit process (concurrent commit/push) — retry once it finishes");
        }
        try
        {
            stream.SetLength(0);
            byte[] info = Encoding.UTF8.GetBytes(
                $"{Environment.ProcessId} {DateTimeOffset.Now:o} {Environment.MachineName} {operation}\n");
            stream.Write(info, 0, info.Length);
            stream.Flush();
        }
        catch { /* holding the lock matters; annotating it is best-effort */ }
        return new RepoLock(stream);
    }

    public void Dispose()
    {
        // Closing releases the advisory lock. Deliberately do NOT delete the file: unlinking it
        // could let a process that already opened the inode and a process that then creates a fresh
        // inode each believe they hold the lock. One persistent inode keeps the advisory lock
        // authoritative; an unlocked lock file lying on disk is harmless.
        _stream?.Dispose();
        _stream = null;
    }
}
