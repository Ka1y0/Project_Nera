using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChipsStudio.Nera.Control;

public sealed record NeraControlPipeServerOptions
{
    public string? PipeName { get; init; }
    public int MaximumInstances { get; init; } = 4;
    public string? ExpectedAgentBridgeVersion { get; init; } = "0.4.1";
    public bool AllowTestSource { get; init; }
}

/// <summary>
/// Current-user-only, local Named Pipe server. It never creates a TCP/HTTP listener.
/// </summary>
public sealed class NeraControlPipeServer : IAsyncDisposable
{
    private readonly NeraControlPipeServerOptions options_;
    private readonly NeraControlWireHandler handler_;
    private readonly CancellationTokenSource shutdown_ = new();
    private readonly ConcurrentDictionary<long, Task> clients_ = new();
    private readonly object lifecycleGate_ = new();
    private Task? acceptLoop_;
    private long clientSequence_;

    public NeraControlPipeServer(
        INeraControlClient control,
        INeraAgentConnectionAuthorizer? authorizer = null,
        NeraControlPipeServerOptions? options = null)
    {
        options_ = options ?? new NeraControlPipeServerOptions();
        if (options_.MaximumInstances is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumInstances));
        }
        PipeName = string.IsNullOrWhiteSpace(options_.PipeName)
            ? NeraPipeIdentity.GetCurrentUserPipeName()
            : ValidatePipeName(options_.PipeName);
        handler_ = new NeraControlWireHandler(control, authorizer,
            options_.ExpectedAgentBridgeVersion, options_.AllowTestSource);
    }

    public string PipeName { get; }
    public string FullPipePath => @"\\.\pipe\" + PipeName;
    public bool UsesCurrentUserOnlyAcl => true;
    public bool RejectsRemoteClients => true;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate_)
        {
            ObjectDisposedException.ThrowIf(shutdown_.IsCancellationRequested, this);
            acceptLoop_ ??= Task.Run(AcceptLoopAsync);
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? acceptLoop;
        lock (lifecycleGate_)
        {
            if (shutdown_.IsCancellationRequested)
            {
                return;
            }
            shutdown_.Cancel();
            acceptLoop = acceptLoop_;
        }
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        var clients = clients_.Values.ToArray();
        if (clients.Length > 0)
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
        shutdown_.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!shutdown_.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(shutdown_.Token).ConfigureAwait(false);
                var clientId = Interlocked.Increment(ref clientSequence_);
                var connectedPipe = pipe;
                pipe = null;
                var task = HandleClientAsync(connectedPipe, shutdown_.Token);
                clients_[clientId] = task;
                _ = task.ContinueWith(
                    completedTask => clients_.TryRemove(clientId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (shutdown_.IsCancellationRequested)
            {
                pipe?.Dispose();
                return;
            }
            catch (IOException) when (!shutdown_.IsCancellationRequested)
            {
                pipe?.Dispose();
                // A failed instance does not stop the local control plane.
            }
        }
    }

    private NamedPipeServerStream CreatePipe() => NativeLocalPipeFactory.Create(
        PipeName,
        options_.MaximumInstances,
        64 * 1024,
        64 * 1024);

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var session = new NeraWireSession();
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var frame = await NeraControlWireProtocol.ReadFrameAsync(pipe, cancellationToken)
                        .ConfigureAwait(false);
                    NeraWireRequest request;
                    try
                    {
                        request = NeraControlWireProtocol.DeserializeRequest(frame);
                    }
                    catch (NeraWireProtocolException error)
                    {
                        var snapshot = await GetFallbackSnapshotAsync(cancellationToken).ConfigureAwait(false);
                        var malformed = NeraControlWireHandler.Failure(string.Empty, snapshot, error.Code,
                            "消息格式无效", error.Message);
                        await NeraControlWireProtocol.WriteFrameAsync(pipe,
                            NeraControlWireProtocol.SerializeResponse(malformed), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var response = await handler_.HandleAsync(request, session, cancellationToken).ConfigureAwait(false);
                    await NeraControlWireProtocol.WriteFrameAsync(pipe,
                        NeraControlWireProtocol.SerializeResponseForPeer(
                            response, request.ProtocolVersion), cancellationToken).ConfigureAwait(false);
                }
                catch (NeraWireProtocolException error) when (error.Code == NeraControlCodes.MessageTooLarge)
                {
                    // Reject before allocation. Closing the connection is the stable oversized-message behavior.
                    return;
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task<NeraStateSnapshot> GetFallbackSnapshotAsync(CancellationToken cancellationToken)
    {
        // The handler owns the client reference; a malformed frame cannot name a command. A minimal
        // valid state is returned only if the control client has already been disposed unexpectedly.
        try
        {
            var request = new NeraWireRequest
            {
                RequestId = Guid.NewGuid().ToString("D"),
                Source = NeraCommandSource.Test,
                Timestamp = DateTimeOffset.UtcNow,
                Command = NeraWireCommand.GetStatus
            };
            var response = await handler_.HandleAsync(request, new NeraWireSession(), cancellationToken)
                .ConfigureAwait(false);
            return response.State;
        }
        catch
        {
            return NeraStateSnapshot.CreateInitial();
        }
    }

    private static string ValidatePipeName(string name)
    {
        if (name.Length is < 1 or > 200 || name.Any(character => char.IsControl(character) || character is '\\' or '/'))
        {
            throw new ArgumentException("PipeName 必须是简单的本机管道名称。", nameof(name));
        }
        return name;
    }
}

public static class NeraPipeIdentity
{
    public static string GetCurrentUserPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("无法获取当前 Windows 用户 SID。");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        // The SID itself is never placed in the public pipe name.
        return "Nera.Control." + Convert.ToHexString(digest.AsSpan(0, 16));
    }
}

internal static class NativeLocalPipeFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SecurityDescriptorRevision = 1;

    internal static NamedPipeServerStream Create(
        string pipeName,
        int maximumInstances,
        int inputBufferSize,
        int outputBufferSize)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("无法获取当前 Windows 用户 SID。");
        // .NET 10 NamedPipeClientStream(CurrentUserOnly) currently compares the pipe owner against
        // WindowsIdentity.Owner rather than WindowsIdentity.User. Keep the protected DACL bound only
        // to User, while materializing Owner exactly as the framework client expects.
        var ownerSid = identity.Owner?.Value ?? sid;
        // Protected DACL with exactly one ACE: current user, GENERIC_ALL. No Everyone, Anonymous,
        // Administrators, or remote principals are implicitly added.
        var sddl = $"O:{ownerSid}G:{sid}D:P(A;;GA;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "无法创建仅限当前用户的管道安全描述符。");
        }

        try
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped | FileFlagWriteThrough,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                checked((uint)maximumInstances),
                checked((uint)outputBufferSize),
                checked((uint)inputBufferSize),
                0,
                ref securityAttributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "CreateNamedPipeW 无法创建本机 Nera 控制管道。");
            }

            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                LocalFree(descriptor);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
