using System.Text.Json;

namespace NetworkOptimizer.Core;

public sealed class FileStateStore : IStateStore
{
    private readonly AppEnvironment _env;
    public string Root => _env.Root;

    public FileStateStore(AppEnvironment env)
    {
        _env = env;
    }

    public Task SaveOriginalSnapshotAsync(NetworkSnapshot snapshot, CancellationToken ct) =>
        WriteAsync(_env.OriginalSnapshotFile, snapshot, ct);

    public Task<NetworkSnapshot?> LoadOriginalSnapshotAsync(CancellationToken ct) =>
        ReadAsync<NetworkSnapshot>(_env.OriginalSnapshotFile, ct);

    public Task SavePendingAsync(PendingOperation pending, CancellationToken ct) =>
        WriteAsync(_env.PendingFile, pending, ct);

    public Task<PendingOperation?> LoadPendingAsync(CancellationToken ct) =>
        ReadAsync<PendingOperation>(_env.PendingFile, ct);

    public Task ClearPendingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(_env.PendingFile))
        {
            File.Delete(_env.PendingFile);
        }

        return Task.CompletedTask;
    }

    public Task SaveWorkingAsync(WorkingConfiguration working, CancellationToken ct) =>
        WriteAsync(_env.WorkingFile, working, ct);

    public Task<WorkingConfiguration?> LoadWorkingAsync(CancellationToken ct) =>
        ReadAsync<WorkingConfiguration>(_env.WorkingFile, ct);

    public Task ClearWorkingAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(_env.WorkingFile))
        {
            File.Delete(_env.WorkingFile);
        }

        return Task.CompletedTask;
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }
}
