using System.Text.Json;
using System.Threading.Channels;

namespace ChipsStudio.Nera.Control;

/// <summary>
/// Coalesces state changes to an atomic Data\State\current.json projection at no more than 4 Hz.
/// The projection contains Nera state only and removes technical error text that may contain local paths.
/// </summary>
public sealed class CurrentStateProjectionWriter : IAsyncDisposable
{
    public static readonly TimeSpan MinimumProjectionInterval = TimeSpan.FromMilliseconds(250);

    private readonly Channel<NeraStateSnapshot> pending_;
    private readonly CancellationTokenSource shutdown_ = new();
    private readonly Task pump_;
    private readonly TimeSpan minimumInterval_;
    private INeraControlClient? attachedClient_;
    private DateTimeOffset lastWriteAt_ = DateTimeOffset.MinValue;
    private long successfulWriteCount_;
    private bool disposed_;

    public CurrentStateProjectionWriter(
        string outputPath,
        TimeSpan? minimumInterval = null)
    {
        OutputPath = ValidateOutputPath(outputPath);
        minimumInterval_ = minimumInterval ?? MinimumProjectionInterval;
        if (minimumInterval_ < MinimumProjectionInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval),
                "current.json 的更新频率不能超过 4 Hz。");
        }
        var directory = Path.GetDirectoryName(OutputPath)!;
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoint(directory);
        pending_ = Channel.CreateBounded<NeraStateSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        pump_ = Task.Run(PumpAsync);
    }

    public string OutputPath { get; }
    public long SuccessfulWriteCount => Interlocked.Read(ref successfulWriteCount_);
    public DateTimeOffset LastWriteAt => lastWriteAt_;
    public Exception? LastWriteError { get; private set; }

    public static CurrentStateProjectionWriter CreateForPortableRoot(string portableRoot) =>
        new(Path.Combine(ValidatePortableRoot(portableRoot), "Data", "State", "current.json"));

    public async Task AttachAsync(
        INeraControlClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ObjectDisposedException.ThrowIf(disposed_, this);
        if (Interlocked.CompareExchange(ref attachedClient_, client, null) is not null)
        {
            throw new InvalidOperationException("状态投影写入器只能连接一个控制客户端。");
        }
        client.StateChanged += OnStateChanged;
        var snapshot = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        Publish(snapshot);
    }

    public bool Publish(NeraStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(disposed_, this);
        NeraStatePolicy.Validate(snapshot);
        return pending_.Writer.TryWrite(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed_)
        {
            return;
        }
        disposed_ = true;
        if (attachedClient_ is { } client)
        {
            client.StateChanged -= OnStateChanged;
        }
        pending_.Writer.TryComplete();
        try
        {
            await pump_.ConfigureAwait(false);
        }
        finally
        {
            shutdown_.Cancel();
            shutdown_.Dispose();
        }
    }

    private void OnStateChanged(object? sender, NeraStateSnapshot snapshot)
    {
        if (!disposed_)
        {
            pending_.Writer.TryWrite(snapshot);
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var snapshot in pending_.Reader.ReadAllAsync(shutdown_.Token).ConfigureAwait(false))
        {
            var delay = lastWriteAt_ + minimumInterval_ - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, shutdown_.Token).ConfigureAwait(false);
            }
            try
            {
                await WriteAtomicAsync(Sanitize(snapshot), shutdown_.Token).ConfigureAwait(false);
                lastWriteAt_ = DateTimeOffset.UtcNow;
                Interlocked.Increment(ref successfulWriteCount_);
                LastWriteError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                               InvalidDataException)
            {
                LastWriteError = error;
                // The state projection is an observer. A disk failure cannot stop processing or Presenter.
            }
        }
    }

    private async Task WriteAtomicAsync(NeraStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(OutputPath)!;
        EnsureNoReparsePoint(directory);
        if (Directory.Exists(OutputPath))
        {
            throw new IOException("current.json 目标是目录。");
        }
        if (File.Exists(OutputPath) &&
            (File.GetAttributes(OutputPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("current.json 目标不能是重解析点。");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, NeraJson.Options);
        if (bytes.Length > NeraControlWireProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("current.json 超过 1 MiB 状态投影限制。");
        }
        var temporary = Path.Combine(directory, ".current." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, OutputPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }

    private static NeraStateSnapshot Sanitize(NeraStateSnapshot snapshot) => snapshot with
    {
        RuntimePath = null,
        LastError = snapshot.LastError is null
            ? null
            : snapshot.LastError with { TechnicalMessage = string.Empty }
    };

    private static string ValidatePortableRoot(string path)
    {
        var root = ValidateLocalAbsolutePath(path, nameof(path));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }
        EnsureNoReparsePoint(root);
        return root;
    }

    private static string ValidateOutputPath(string path)
    {
        var fullPath = ValidateLocalAbsolutePath(path, nameof(path));
        if (!Path.GetFileName(fullPath).Equals("current.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("远程友好状态投影必须命名为 current.json。", nameof(path));
        }
        return fullPath;
    }

    private static string ValidateLocalAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("必须提供完全限定的本地文件系统路径。", parameterName);
        }
        return Path.GetFullPath(path);
    }

    private static void EnsureNoReparsePoint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("状态投影目录不能是重解析点。");
        }
    }
}
