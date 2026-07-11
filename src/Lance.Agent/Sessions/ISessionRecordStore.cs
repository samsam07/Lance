using System.Text.Json;
using Lance.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace Lance.Agent.Sessions;

// Persists session records to disk, one file per session, written atomically so a
// crash mid-write never leaves a torn record. LoadAll feeds startup reconciliation
// (Slice 6.5). See SPEC "Session record".
internal interface ISessionRecordStore
{
    Task SaveAsync(SessionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionRecord>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}

internal sealed class FileSessionRecordStore : ISessionRecordStore
{
    private readonly string _directory;
    private readonly ILogger<FileSessionRecordStore> _logger;

    public FileSessionRecordStore(AgentConfig config, ILogger<FileSessionRecordStore> logger)
    {
        _directory = config.Sessions.RecordDir;
        _logger = logger;
    }

    public async Task SaveAsync(SessionRecord record, CancellationToken cancellationToken = default)
    {
        if (!IsValidSessionId(record.SessionId))
        {
            throw new ArgumentException($"Session id '{record.SessionId}' is not a valid record name.", nameof(record));
        }

        Directory.CreateDirectory(_directory);

        string finalPath = PathFor(record.SessionId);
        string tempPath = finalPath + ".tmp";

        // Write to a temp file then rename over the target: the record file is either
        // the old content or the complete new content, never a half-written mix.
        string json = JsonSerializer.Serialize(record, SessionJsonContext.Default.SessionRecord);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public async Task<IReadOnlyList<SessionRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        List<SessionRecord> records = [];
        if (!Directory.Exists(_directory))
        {
            return records;
        }

        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            SessionRecord? record = await TryReadAsync(path, cancellationToken);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!IsValidSessionId(sessionId))
        {
            return Task.CompletedTask;
        }

        string path = PathFor(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private async Task<SessionRecord?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Ignoring an unreadable session record at {Path}.", path);
            return null;
        }
    }

    private string PathFor(string sessionId)
    {
        return Path.Combine(_directory, sessionId + ".json");
    }

    // Session ids become file names, so constrain them to a safe character set — this
    // also blocks path traversal via a hostile --session-id. The connect handshake
    // (Slice 6.4) enforces the same set when vetting the id.
    private static bool IsValidSessionId(string sessionId)
    {
        if (sessionId.Length is 0 or > 64)
        {
            return false;
        }

        foreach (char c in sessionId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
