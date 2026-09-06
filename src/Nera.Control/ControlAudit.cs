using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ChipsStudio.Nera.Control;

/// <summary>Allowlisted state only: no target titles, paths, command payloads, screenshots or error text.</summary>
public sealed record NeraControlAuditState
{
    public long Revision { get; init; }
    public NeraDldrActualState ActualState { get; init; }
    public bool Requested { get; init; }
    public bool On { get; init; }
    public bool Processing { get; init; }
    public bool OffGate { get; init; }
    public bool WarmStandby { get; init; }
    public bool WarmOffGate { get; init; }
    public bool OverlayBypass { get; init; }
    public bool BrokerOwned { get; init; }
    public bool BrokerReady { get; init; }
    public bool HostConnected { get; init; }
    public bool CaptureCreated { get; init; }
    public bool FeatureCreated { get; init; }
    public bool PresenterSucceeded { get; init; }
    public bool ForegroundPreserved { get; init; }
    public bool HudVisible { get; init; }
    public bool AppVisible { get; init; }
    public NeraRecoveryState Recovery { get; init; }

    public static NeraControlAuditState From(NeraStateSnapshot state) => new()
    {
        Revision = state.Revision, ActualState = state.DldrActualState,
        Requested = state.DldrRequested, On = state.DldrOn, Processing = state.ProcessingActive,
        OffGate = state.OffGateSatisfied, WarmStandby = state.WarmStandby,
        WarmOffGate = state.WarmOffGateSatisfied, OverlayBypass = state.OverlayBypass,
        BrokerOwned = state.BrokerProcessOwned, BrokerReady = state.BrokerConnected,
        HostConnected = state.HostConnected, CaptureCreated = state.MonitorCaptureCreated,
        FeatureCreated = state.FeatureCreated, PresenterSucceeded = state.PresenterSucceeded,
        ForegroundPreserved = state.ForegroundPreserved, HudVisible = state.HudVisible,
        AppVisible = state.AppVisible, Recovery = state.RecoveryState
    };
}

public sealed record NeraControlAuditRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public long Sequence { get; init; }
    public string Event { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string LifecycleReasonCode { get; init; } = "STATE_OBSERVED";
    public string? ExceptionType { get; init; }
    public int? ExceptionHResult { get; init; }
    public string? ExceptionFingerprint { get; init; }
    public ulong HostSessionGeneration { get; init; }
    public NeraNativeLifecycleHealth NativeLifecycleHealth { get; init; } = NeraNativeLifecycleHealth.Unknown;
    public bool ProtectedRecoveryPending { get; init; }
    public bool RuntimeVerified { get; init; }
    public bool HdrEnabled { get; init; }
    public bool DisplayConnected { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public ulong LastFeature18FrameId { get; init; }
    public ulong LastDldrFrameId { get; init; }
    public ulong LastPresentedFrameId { get; init; }
    public int? OriginProcess { get; init; }
    public long? OriginHwnd { get; init; }
    public uint? OriginThread { get; init; }
    public uint? OriginMessage { get; init; }
    public int? RegistrationId { get; init; }
    public uint? VirtualKey { get; init; }
    public uint? Modifiers { get; init; }
    public uint? MouseMessage { get; init; }
    public string CommandHandler { get; init; } = string.Empty;
    public string CallSiteCategory { get; init; } = string.Empty;
    public string? ControlId { get; init; }
    public required NeraControlAuditState PreviousState { get; init; }
    public required NeraControlAuditState NextState { get; init; }
    public bool? Ok { get; init; }
    public bool RollbackPerformed { get; init; }
    public ulong FrameId { get; init; }
    public ulong AppliedParameterRevision { get; init; }
    public ulong AppliedParameterFrameId { get; init; }
    /// <summary>Authoritative gate evidence, not an invented Win32 ShowWindow return value.</summary>
    public string PresenterShowHideResult { get; init; } = "UNCONFIRMED";
    public string AppShowHideResult { get; init; } = "UNCHANGED";
    public string HudShowHideResult { get; init; } = "UNCHANGED";
    public long DroppedBefore { get; init; }
}

public interface INeraControlAuditSink
{
    /// <summary>Must not block the authoritative lane. False means the bounded sink dropped this record.</summary>
    bool TryWrite(NeraControlAuditRecord record);
}

internal static class NeraControlAuditPolicy
{
    internal static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= 128 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-' or ':'))
            return value;
        return "sha256-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static bool HasTransition(NeraStateSnapshot before, NeraStateSnapshot after) =>
        NeraControlAuditState.From(before) with { Revision = 0 } !=
            NeraControlAuditState.From(after) with { Revision = 0 } ||
        before.AppliedParameterRevision != after.AppliedParameterRevision ||
        before.HostSessionGeneration != after.HostSessionGeneration ||
        NeraNativeLifecycleHealth.HasDiagnosticTransition(before.NativeLifecycleHealth, after.NativeLifecycleHealth) ||
        before.ProtectedRecoveryPending != after.ProtectedRecoveryPending ||
        before.LastError?.ReasonCode != after.LastError?.ReasonCode ||
        before.LastError?.Code != after.LastError?.Code;

    internal static string ObservationReason(NeraStateSnapshot before, NeraStateSnapshot after) =>
        !before.OverlayBypass && after.OverlayBypass ? "OverlayYield" :
        before.OverlayBypass && !after.OverlayBypass ? "OverlayResume" :
        after.RecoveryState == NeraRecoveryState.Recovering || after.DldrActualState == NeraDldrActualState.Failed
            ? "RuntimeFailureRecovery" :
        before.WarmStandby && !after.WarmStandby && after.OffGateSatisfied ? "WarmIdleRelease" :
        before.TargetDisplay?.DisplayId != after.TargetDisplay?.DisplayId ? "DisplayChangeRecovery" : "InternalObservation";

    internal static NeraControlAuditRecord Create(long sequence, string eventName,
        NeraCommandEnvelope? command, NeraStateSnapshot before, NeraStateSnapshot after,
        string reason, NeraCommandResult? result = null)
    {
        NeraInputOrigin? origin = command?.InputOrigin;
        return new()
        {
            Sequence = sequence, Event = eventName, RequestId = Token(command?.RequestId),
            Source = command?.Source switch
            {
                NeraCommandSource.Ui => "UI", NeraCommandSource.Hotkey => "REGISTERED_HOTKEY",
                NeraCommandSource.Cli => "CLI", NeraCommandSource.Agent => "MCP",
                NeraCommandSource.Orchestrator => "AUTOMATION", NeraCommandSource.Test => "TEST",
                _ => reason is "OverlayYield" or "OverlayResume" ? "OVERLAY_COMPATIBILITY" : "RECOVERY"
            },
            Reason = Token(reason), LifecycleReasonCode = NeraLifecycleReasonPolicy.Transition(command, before, after, result),
            HostSessionGeneration = after.HostSessionGeneration,
            NativeLifecycleHealth = after.NativeLifecycleHealth ?? NeraNativeLifecycleHealth.Unknown,
            ProtectedRecoveryPending = after.ProtectedRecoveryPending,
            RuntimeVerified = after.RuntimeVerified, HdrEnabled = after.HdrSystemEnabled,
            DisplayConnected = after.TargetDisplay?.Connected == true,
            DisplayWidth = after.OutputWidth, DisplayHeight = after.OutputHeight,
            LastFeature18FrameId = after.LastFeature18FrameId, LastDldrFrameId = after.LastDldrFrameId,
            LastPresentedFrameId = after.LastPresentedFrameId,
            OriginProcess = origin?.SourcePid, OriginHwnd = origin?.SourceHwnd,
            OriginThread = origin?.SourceThreadId, OriginMessage = origin?.Message,
            RegistrationId = origin?.RegistrationId, VirtualKey = origin?.Vk, Modifiers = origin?.Modifiers,
            MouseMessage = origin?.Message is { } message && NeraInputOriginPolicy.IsNonCommandInputMessage(message)
                ? message : null,
            CommandHandler = command?.Kind.ToString() ?? "ObserveAuthoritativeState",
            CallSiteCategory = origin?.InputKind.ToString() ?? (command is null ? "InternalObservation" : "CommandApi"),
            ControlId = origin?.ControlId is { } controlId ? Token(controlId) : null,
            PreviousState = NeraControlAuditState.From(before), NextState = NeraControlAuditState.From(after),
            Ok = result?.Ok, RollbackPerformed = result?.RollbackPerformed ?? false,
            FrameId = after.LastSuccessfulFrameId, AppliedParameterRevision = after.AppliedParameterRevision,
            AppliedParameterFrameId = after.AppliedParameterFrameId,
            PresenterShowHideResult = after.PresenterSucceeded && after.DldrOn ? "PRESENTER_FIRST_FRAME_CONFIRMED" :
                after.WarmOffGateSatisfied ? "WARM_OFF_CONFIRMED" :
                after.OffGateSatisfied && !after.BrokerProcessOwned ? "FULL_RELEASE_CONFIRMED" : "UNCONFIRMED",
            AppShowHideResult = before.AppVisible == after.AppVisible ? "UNCHANGED" : after.AppVisible ? "SHOWN" : "HIDDEN",
            HudShowHideResult = before.HudVisible == after.HudVisible ? "UNCHANGED" : after.HudVisible ? "SHOWN" : "HIDDEN"
        };
    }
}

/// <summary>
/// Bounded asynchronous local audit, at most four 1 MiB files by default. No disk IO on
/// the command lane. Only this sink's exact filenames are rotated; failures never alter DLDR.
/// </summary>
public sealed class RollingNeraControlAuditSink : INeraControlAuditSink, IAsyncDisposable
{
    private readonly string dataDirectory_;
    private readonly string logDirectory_;
    private readonly long maxFileBytes_;
    private readonly int fileCount_;
    private readonly Channel<NeraControlAuditRecord> records_;
    private readonly CancellationTokenSource shutdown_ = new();
    private readonly Task worker_;
    private long dropped_;
    private long failed_;
    private int disposed_;

    public RollingNeraControlAuditSink(string absoluteDataDirectory, long maxFileBytes = 1024 * 1024,
        int fileCount = 4, int queueCapacity = 512)
    {
        if (!NeraStatePolicy.IsLocalAbsolutePath(absoluteDataDirectory))
            throw new ArgumentException("Audit data directory must be absolute and local.", nameof(absoluteDataDirectory));
        if (maxFileBytes is < 1024 or > 16 * 1024 * 1024 || fileCount is < 1 or > 8 || queueCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        dataDirectory_ = Path.GetFullPath(absoluteDataDirectory);
        logDirectory_ = Path.Combine(dataDirectory_, "Logs");
        maxFileBytes_ = maxFileBytes;
        fileCount_ = fileCount;
        records_ = Channel.CreateBounded<NeraControlAuditRecord>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        worker_ = Task.Run(WriteLoopAsync);
    }

    public long DroppedRecords => Interlocked.Read(ref dropped_);
    public long FailedRecords => Interlocked.Read(ref failed_);

    public bool TryWrite(NeraControlAuditRecord record)
    {
        if (Volatile.Read(ref disposed_) == 0 && records_.Writer.TryWrite(record)) return true;
        Interlocked.Increment(ref dropped_);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed_, 1) != 0) return;
        records_.Writer.TryComplete();
        shutdown_.CancelAfter(TimeSpan.FromSeconds(2));
        try { await worker_.ConfigureAwait(false); }
        catch (OperationCanceledException) when (shutdown_.IsCancellationRequested) { }
        finally { shutdown_.Dispose(); }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (NeraControlAuditRecord record in records_.Reader.ReadAllAsync(shutdown_.Token).ConfigureAwait(false))
        {
            try
            {
                RequireNotReparsePoint(dataDirectory_);
                RequireNotReparsePoint(logDirectory_);
                Directory.CreateDirectory(logDirectory_);
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    record with { DroppedBefore = DroppedRecords + FailedRecords }, NeraJson.Options) + "\n");
                string path = PathFor(0);
                RequireNotReparsePoint(path);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxFileBytes_)
                    Rotate();
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, shutdown_.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { Interlocked.Increment(ref failed_); }
        }
    }

    private string PathFor(int index) => Path.Combine(logDirectory_, index == 0
        ? "control-audit.jsonl" : $"control-audit.{index}.jsonl");

    private void Rotate()
    {
        for (int index = fileCount_ - 1; index >= 0; index--) RequireNotReparsePoint(PathFor(index));
        string oldest = PathFor(fileCount_ - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int index = fileCount_ - 2; index >= 0; index--)
            if (File.Exists(PathFor(index))) File.Move(PathFor(index), PathFor(index + 1), overwrite: false);
    }

    private static void RequireNotReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Audit path cannot be a reparse point.");
    }
}
