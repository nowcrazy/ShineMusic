namespace ShineMusic.Model.Ports;

/// <summary>A temporary AAC stream whose open handle protects it from local cache cleanup.</summary>
public sealed class TemporaryPlaybackLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _disposed;

    public TemporaryPlaybackLease(
        FileStream content,
        long length,
        DateTimeOffset lastModifiedUtc,
        string entityTag,
        Func<ValueTask> release)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Length = length;
        LastModifiedUtc = lastModifiedUtc;
        EntityTag = entityTag ?? throw new ArgumentNullException(nameof(entityTag));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public FileStream Content { get; }
    public long Length { get; }
    public DateTimeOffset LastModifiedUtc { get; }
    public string EntityTag { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Content.DisposeAsync();
        await _release();
    }
}

/// <summary>Creates or reuses a bounded temporary AAC rendition of a stored original.</summary>
public interface ITemporaryPlaybackService
{
    Task<TemporaryPlaybackLease> GetAacAsync(
        Guid trackId,
        string originalFilePath,
        string sourceIdentity,
        CancellationToken cancellationToken);
}
