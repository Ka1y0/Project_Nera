using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace ChipsStudio.Nera.AgentBridge;

public interface INeraControlTransport
{
    Task<NeraCommandResult> SendAsync(
        NeraCommandEnvelope envelope,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class NamedPipeNeraControlTransport : INeraControlTransport, IAsyncDisposable
{
    private readonly string pipeName_;
    private readonly SemaphoreSlim gate_ = new(1, 1);
    private NamedPipeClientStream? pipe_;
    private string? source_;
    private bool disposed_;

    public NamedPipeNeraControlTransport()
        : this(NeraControlProtocol.CurrentUserPipeName())
    {
    }

    public NamedPipeNeraControlTransport(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) ||
            !pipeName.StartsWith("Nera.Control.", StringComparison.Ordinal) ||
            pipeName.Contains('\\') || pipeName.Contains('/'))
        {
            throw new ArgumentException("管道名称不属于 Nera 当前用户命名空间。",
                nameof(pipeName));
        }
        pipeName_ = pipeName;
    }

    public async Task<NeraCommandResult> SendAsync(
        NeraCommandEnvelope envelope,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMilliseconds(NeraControlProtocol.MaximumTimeoutMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ObjectDisposedException.ThrowIf(disposed_, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await gate_.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(envelope.Source, deadline.Token, cancellationToken)
                    .ConfigureAwait(false);
                var request = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSupport.Compact);
                await PipeFraming.WriteFrameAsync(pipe_!, request, deadline.Token).ConfigureAwait(false);
                var response = await PipeFraming.ReadFrameAsync(pipe_!, deadline.Token).ConfigureAwait(false);
                return PipeResponseValidator.Decode(response, envelope.RequestId);
            }
            catch
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate_.Release();
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NeraTransportException(NeraErrorCodes.Timeout,
                "Nera 控制请求超时。", error);
        }
        catch (OperationCanceledException error)
        {
            throw new NeraTransportException(NeraErrorCodes.Cancelled,
                "Nera 控制请求已取消。", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new NeraTransportException(NeraErrorCodes.PermissionDenied,
                "当前用户无权访问 Nera 控制层。", error);
        }
        catch (NeraTransportException)
        {
            throw;
        }
        catch (IOException error)
        {
            throw new NeraTransportException(NeraErrorCodes.ServiceUnavailable,
                "Nera 控制层不可用。", error);
        }
        catch (JsonException error)
        {
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回了格式错误的 JSON。", error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed_) return;
        disposed_ = true;
        await gate_.WaitAsync().ConfigureAwait(false);
        try { await ResetConnectionAsync().ConfigureAwait(false); }
        finally
        {
            gate_.Release();
            gate_.Dispose();
        }
    }

    private async Task EnsureConnectedAsync(
        string source,
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {
        if (source is not (NeraControlProtocol.AgentSource or NeraControlProtocol.CliSource or
            NeraControlProtocol.OrchestratorSource))
            throw new NeraTransportException(NeraErrorCodes.InvalidArgument,
                "Nera 控制来源无效。");
        if (pipe_ is { IsConnected: true })
        {
            if (!string.Equals(source_, source, StringComparison.Ordinal))
                throw new NeraTransportException(NeraErrorCodes.PermissionDenied,
                    "同一管道会话不能更改已授权的控制来源。");
            return;
        }

        await ResetConnectionAsync().ConfigureAwait(false);
        var pipe = new NamedPipeClientStream(
            ".", pipeName_, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification,
            HandleInheritability.None);
        try
        {
            try
            {
                await pipe.ConnectAsync(deadlineToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!callerToken.IsCancellationRequested)
            {
                throw new NeraTransportException(NeraErrorCodes.ServiceUnavailable,
                    "Nera 控制层未运行，或当前用户管道不可用。", error);
            }
            var requestId = Guid.NewGuid().ToString("N");
            var handshake = new NeraCommandEnvelope
            {
                RequestId = requestId,
                Source = source,
                Timestamp = DateTimeOffset.UtcNow,
                Command = "handshake",
                AgentBridgeVersion = NeraControlProtocol.AgentBridgeVersion,
                ExpectedAppVersion = NeraControlProtocol.ExpectedAppVersion
            };
            var request = JsonSerializer.SerializeToUtf8Bytes(handshake, JsonSupport.Compact);
            await PipeFraming.WriteFrameAsync(pipe, request, deadlineToken).ConfigureAwait(false);
            var response = await PipeFraming.ReadFrameAsync(pipe, deadlineToken).ConfigureAwait(false);
            var result = PipeResponseValidator.Decode(response, requestId);
            if (!result.Ok)
            {
                throw new NeraTransportException(result.Code,
                    string.IsNullOrWhiteSpace(result.UserMessage)
                        ? "Nera 控制层拒绝了本机握手。"
                        : result.UserMessage);
            }
            pipe_ = pipe;
            source_ = source;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ResetConnectionAsync()
    {
        source_ = null;
        var pipe = Interlocked.Exchange(ref pipe_, null);
        if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
    }
}

public static class PipeFraming
{
    public static async Task WriteFrameAsync(
        Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length is <= 0 or > NeraControlProtocol.MaximumMessageBytes)
        {
            throw new NeraTransportException(NeraErrorCodes.ResponseTooLarge,
                "Nera 控制消息长度超出允许范围。");
        }
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0)
        {
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回了无效的帧长度。");
        }
        if (length > NeraControlProtocol.MaximumMessageBytes)
        {
            throw new NeraTransportException(NeraErrorCodes.ResponseTooLarge,
                "Nera 控制层返回的帧超过 1 MiB。");
        }
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task ReadExactlyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                    "Nera 控制层在帧接收完成前关闭了管道。");
            }
            offset += read;
        }
    }
}

public static class PipeResponseValidator
{
    public static NeraCommandResult Decode(ReadOnlySpan<byte> payload, string expectedRequestId)
    {
        NeraCommandResult? result;
        try
        {
            result = JsonSerializer.Deserialize<NeraCommandResult>(payload, JsonSupport.Compact);
        }
        catch (JsonException error)
        {
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回了格式错误的 JSON。", error);
        }
        if (result is null || string.IsNullOrWhiteSpace(result.RequestId) ||
            result.State is null || string.IsNullOrWhiteSpace(result.Code) ||
            string.IsNullOrWhiteSpace(result.UserMessage))
        {
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回结果缺少必需字段。");
        }
        if (result.ProtocolVersion != NeraControlProtocol.Version)
        {
            throw new NeraTransportException(NeraErrorCodes.ProtocolMismatch,
                $"不支持 Nera 控制协议版本“{result.ProtocolVersion}”。");
        }
        if (!string.Equals(result.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回的 requestId 与请求不一致。");
        }
        if (!result.PreviousRevision.HasValue || !result.CurrentRevision.HasValue ||
            result.PreviousRevision.Value < 0 || result.CurrentRevision.Value < 0 ||
            result.CurrentRevision.Value < result.PreviousRevision.Value)
        {
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回了无效的修订号变化。");
        }
        if (result.State.Revision != result.CurrentRevision.Value)
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "Nera 控制层返回的状态修订号与响应修订号不一致。");
        return result with { Code = NormalizeCode(result.Code) };
    }

    internal static string NormalizeCode(string code) => code switch
    {
        "OK" => "OK",
        "INVALID_ARGUMENT" => NeraErrorCodes.InvalidArgument,
        "STATE_CHANGED" => NeraErrorCodes.RevisionConflict,
        "PERMISSION_DENIED" => NeraErrorCodes.PermissionDenied,
        "RUNTIME_MISSING" or "RUNTIME_UNVERIFIED" => NeraErrorCodes.RuntimeUnavailable,
        "PROTOCOL_MISMATCH" or "VERSION_MISMATCH" or "HANDSHAKE_REQUIRED" =>
            NeraErrorCodes.ProtocolMismatch,
        "MALFORMED_JSON" => NeraErrorCodes.MalformedResponse,
        "MESSAGE_TOO_LARGE" => NeraErrorCodes.ResponseTooLarge,
        "TIMEOUT" => NeraErrorCodes.Timeout,
        _ when code.StartsWith("NERA_", StringComparison.Ordinal) => code,
        _ => code
    };
}
