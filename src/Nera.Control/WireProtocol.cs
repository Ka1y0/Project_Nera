using System.Buffers.Binary;
using System.Text.Json;

namespace ChipsStudio.Nera.Control;

public enum NeraWireCommand
{
    Handshake,
    GetStatus,
    GetDisplays,
    SetDisplay,
    EnableDldr,
    DisableDldr,
    SetMode,
    SetStrength,
    SetPerformanceMode,
    SetUiProtection,
    SetFeature18Tuning,
    SetDldrTuning,
    ShowHud,
    HideHud,
    RunSelfTest,
    EmergencyStop
}

public sealed record NeraWireRequest
{
    public int ProtocolVersion { get; init; } = NeraControlWireProtocol.CurrentProtocolVersion;
    public required string RequestId { get; init; }
    public required NeraCommandSource Source { get; init; }
    public long? ExpectedRevision { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required NeraWireCommand Command { get; init; }
    public NeraCommandPayload Payload { get; init; } = NeraCommandPayload.Empty;
    public string? AgentBridgeVersion { get; init; }
    public string? ExpectedAppVersion { get; init; }
}

public sealed record NeraWireResponse
{
    public int ProtocolVersion { get; init; } = NeraControlWireProtocol.CurrentProtocolVersion;
    public required bool Ok { get; init; }
    public required string RequestId { get; init; }
    public required long PreviousRevision { get; init; }
    public required long CurrentRevision { get; init; }
    public required NeraStateSnapshot State { get; init; }
    public required string Code { get; init; }
    public required string UserMessage { get; init; }
    public string TechnicalMessage { get; init; } = string.Empty;
    public bool RollbackPerformed { get; init; }
    public IReadOnlyList<NeraDisplaySummary>? Displays { get; init; }
    public string? OperationId { get; init; }
    public JsonElement? Data { get; init; }

    public static NeraWireResponse FromCommandResult(NeraCommandResult result) => new()
    {
        Ok = result.Ok,
        RequestId = result.RequestId,
        PreviousRevision = result.PreviousRevision,
        CurrentRevision = result.CurrentRevision,
        State = result.State,
        Code = result.Code,
        UserMessage = result.UserMessage,
        TechnicalMessage = result.TechnicalMessage,
        RollbackPerformed = result.RollbackPerformed,
        OperationId = result.OperationId,
        Data = result.Data
    };
}

public sealed class NeraWireProtocolException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

public static class NeraControlWireProtocol
{
    // v4 adds SkinStructureStrength and publishes the user-facing Clear mode as
    // the wire token "clear". Treat that response-shape change as a protocol
    // boundary so a stale strict AgentBridge is rejected during handshake.
    // v6 adds explicit language preferences and verified Presenter telemetry.
    // Older strict-schema bridges must fail the handshake instead of misreading state.
    // v7 adds native lifecycle health and host-generation/recovery evidence.
    public const int CurrentProtocolVersion = 7;
    internal const int PreviousStrictProtocolVersion = 6;
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int LengthPrefixBytes = 4;

    public static JsonSerializerOptions CreateSerializerOptions() => new(NeraJson.Options);

    public static byte[] SerializeRequest(NeraWireRequest request) => SerializeBounded(request);
    public static byte[] SerializeResponse(NeraWireResponse response) => SerializeBounded(response);

    /// <summary>
    /// A v3 AgentBridge rejects unknown JSON members before it can inspect the
    /// protocolVersion field. A protocol-mismatch reply therefore uses a small
    /// v3-compatible negotiation envelope instead of serializing the complete v4
    /// state snapshot (which contains fields that v3 cannot decode).
    /// </summary>
    public static byte[] SerializeResponseForPeer(
        NeraWireResponse response,
        int peerProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (peerProtocolVersion is 3 or 4 or 5 or PreviousStrictProtocolVersion &&
            response.ProtocolVersion == CurrentProtocolVersion &&
            string.Equals(response.Code, NeraControlCodes.ProtocolMismatch,
                StringComparison.Ordinal))
        {
            return SerializeBounded(new ProtocolMismatchResponseV3Compatibility
            {
                ProtocolVersion = response.ProtocolVersion,
                Ok = false,
                RequestId = response.RequestId,
                PreviousRevision = response.PreviousRevision,
                CurrentRevision = response.CurrentRevision,
                State = new ProtocolMismatchStateV3Compatibility
                {
                    Revision = response.CurrentRevision,
                    UpdatedAt = response.State.UpdatedAt,
                    AppVersion = response.State.AppVersion,
                    RuntimeAdapterVersion = response.State.RuntimeAdapterVersion,
                    UiProtection = peerProtocolVersion <= 4 ? "auto" : null
                },
                Code = NeraControlCodes.ProtocolMismatch,
                UserMessage = response.UserMessage
            });
        }
        return SerializeResponse(response);
    }

    public static NeraWireRequest DeserializeRequest(ReadOnlySpan<byte> utf8Json) =>
        DeserializeBounded<NeraWireRequest>(utf8Json);

    public static NeraWireResponse DeserializeResponse(ReadOnlySpan<byte> utf8Json) =>
        DeserializeBounded<NeraWireResponse>(utf8Json);

    public static async ValueTask<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[LengthPrefixBytes];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0)
        {
            throw new NeraWireProtocolException(NeraControlCodes.MalformedJson,
                "帧长度必须大于零。");
        }
        if (length > MaximumMessageBytes)
        {
            throw new NeraWireProtocolException(NeraControlCodes.MessageTooLarge,
                $"帧长度 {length} 超过 {MaximumMessageBytes} 字节。");
        }
        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> utf8Json,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (utf8Json.Length is <= 0 or > MaximumMessageBytes)
        {
            throw new NeraWireProtocolException(
                utf8Json.Length > MaximumMessageBytes ? NeraControlCodes.MessageTooLarge : NeraControlCodes.MalformedJson,
                "帧负载长度超出允许范围。");
        }
        var prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, utf8Json.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(utf8Json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeBounded<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, NeraJson.Options);
        if (bytes.Length is <= 0 or > MaximumMessageBytes)
        {
            throw new NeraWireProtocolException(NeraControlCodes.MessageTooLarge,
                "序列化消息超出允许范围。");
        }
        return bytes;
    }

    private static T DeserializeBounded<T>(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is <= 0 or > MaximumMessageBytes)
        {
            throw new NeraWireProtocolException(
                utf8Json.Length > MaximumMessageBytes ? NeraControlCodes.MessageTooLarge : NeraControlCodes.MalformedJson,
                "JSON 消息超出允许范围。");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, NeraJson.Options)
                ?? throw new NeraWireProtocolException(NeraControlCodes.MalformedJson, "JSON 反序列化结果为 null。");
        }
        catch (JsonException error)
        {
            throw new NeraWireProtocolException(NeraControlCodes.MalformedJson, error.Message);
        }
    }

    private sealed record ProtocolMismatchResponseV3Compatibility
    {
        public required int ProtocolVersion { get; init; }
        public required bool Ok { get; init; }
        public required string RequestId { get; init; }
        public required long PreviousRevision { get; init; }
        public required long CurrentRevision { get; init; }
        public required ProtocolMismatchStateV3Compatibility State { get; init; }
        public required string Code { get; init; }
        public required string UserMessage { get; init; }
    }

    private sealed record ProtocolMismatchStateV3Compatibility
    {
        public required long Revision { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required string AppVersion { get; init; }
        public required string RuntimeAdapterVersion { get; init; }
        public string WorkingColorSpace { get; init; } = NeraStateSnapshot.DefaultWorkingColorSpace;
        public string DldrActualState { get; init; } = "disabled";
        public string ProcessingMode { get; init; } = "natural";
        public int Strength { get; init; } = 50;
        public int NeuralScale { get; init; } = 100;
        public string PerformanceMode { get; init; } = "quality";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? UiProtection { get; init; } = "auto";
        public string RecoveryState { get; init; } = "none";
        public bool OffGateSatisfied { get; init; } = true;
    }
}

public enum NeraAgentConnectionDecision
{
    Deny,
    AllowOnce,
    AlwaysAllow
}

public sealed record NeraAgentConnectionRequest
{
    public required string AgentBridgeVersion { get; init; }
    public required NeraCommandSource Source { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public interface INeraAgentConnectionAuthorizer
{
    ValueTask<NeraAgentConnectionDecision> AuthorizeAsync(
        NeraAgentConnectionRequest request,
        CancellationToken cancellationToken);
}

public sealed class DenyAgentConnectionAuthorizer : INeraAgentConnectionAuthorizer
{
    public static DenyAgentConnectionAuthorizer Instance { get; } = new();
    private DenyAgentConnectionAuthorizer() { }

    public ValueTask<NeraAgentConnectionDecision> AuthorizeAsync(
        NeraAgentConnectionRequest request,
        CancellationToken cancellationToken) => ValueTask.FromResult(NeraAgentConnectionDecision.Deny);
}

public sealed class NeraWireSession
{
    public bool HandshakeComplete { get; internal set; }
    public string? AgentBridgeVersion { get; internal set; }
    public NeraCommandSource Source { get; internal set; }
}

public sealed class NeraControlWireHandler
{
    private readonly INeraControlClient control_;
    private readonly INeraAgentConnectionAuthorizer authorizer_;
    private readonly string? expectedAgentBridgeVersion_;
    private readonly bool allowTestSource_;

    public NeraControlWireHandler(
        INeraControlClient control,
        INeraAgentConnectionAuthorizer? authorizer = null,
        string? expectedAgentBridgeVersion = null,
        bool allowTestSource = false)
    {
        control_ = control ?? throw new ArgumentNullException(nameof(control));
        authorizer_ = authorizer ?? DenyAgentConnectionAuthorizer.Instance;
        expectedAgentBridgeVersion_ = expectedAgentBridgeVersion;
        allowTestSource_ = allowTestSource;
    }

    public async Task<NeraWireResponse> HandleAsync(
        NeraWireRequest request,
        NeraWireSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = await control_.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (request.Command == NeraWireCommand.SetUiProtection)
            return Failure(request.RequestId, snapshot, NeraControlCodes.InvalidArgument,
                "界面保护已从正式产品移除", "UNSUPPORTED_PRODUCT_PARAMETER: uiProtection");
        if (request.ProtocolVersion != NeraControlWireProtocol.CurrentProtocolVersion)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.ProtocolMismatch,
                "控制协议版本不匹配",
                $"client={request.ProtocolVersion}; server={NeraControlWireProtocol.CurrentProtocolVersion}.");
        }
        if (!ValidRequestIdentity(request))
        {
            return Failure(request.RequestId ?? string.Empty, snapshot, NeraControlCodes.InvalidArgument,
                "请求参数无效", "requestId 和 timestamp 均为必填项。");
        }
        if (request.Command == NeraWireCommand.Handshake)
        {
            return await HandshakeAsync(request, session, snapshot, cancellationToken).ConfigureAwait(false);
        }
        if (!session.HandshakeComplete)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.HandshakeRequired,
                "需要先完成本机连接确认", "每个管道连接的第一条请求必须是 handshake。" );
        }
        if (request.Source != session.Source)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                "连接来源不一致", "handshake 完成后 source 发生变化。");
        }

        if (request.Command == NeraWireCommand.GetStatus)
        {
            return snapshot.AiPermissions.ReadState
                ? Success(request.RequestId, snapshot, "状态已读取")
                : Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                    "未允许读取状态", "AI 权限 readState 已关闭。");
        }
        if (request.Command == NeraWireCommand.GetDisplays)
        {
            if (!snapshot.AiPermissions.ReadState || !snapshot.AiPermissions.ReadDisplays)
            {
                return Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                    "未允许读取显示器列表", "AI 权限 readState 或 readDisplays 已关闭。");
            }
            var displays = await control_.ListDisplaysAsync(cancellationToken).ConfigureAwait(false);
            return Success(request.RequestId, snapshot, "显示器列表已读取") with { Displays = displays };
        }

        var kind = ToCommandKind(request.Command);
        if (!IsAllowed(kind, snapshot.AiPermissions))
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                "此操作未获允许", $"AI 权限拒绝了命令 {kind}。");
        }
        if (request.ExpectedRevision is null)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.InvalidArgument,
                "缺少状态版本", "修改状态的命令必须提供 expectedRevision。");
        }
        var command = new NeraCommandEnvelope
        {
            RequestId = request.RequestId,
            Source = request.Source,
            ExpectedRevision = request.ExpectedRevision.Value,
            Timestamp = request.Timestamp,
            Kind = kind,
            Payload = request.Payload
        };
        var result = await control_.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        return NeraWireResponse.FromCommandResult(result) with
        {
            State = SanitizeState(result.State),
            TechnicalMessage = result.State.AiPermissions.RunDiagnostics
                ? result.TechnicalMessage
                : string.Empty
        };
    }

    private async Task<NeraWireResponse> HandshakeAsync(
        NeraWireRequest request,
        NeraWireSession session,
        NeraStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (session.HandshakeComplete)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.InvalidState,
                "连接已完成握手", "每个管道会话只能执行一次 handshake。");
        }
        if (request.Source is not (NeraCommandSource.Agent or NeraCommandSource.Cli or
                NeraCommandSource.Orchestrator or NeraCommandSource.Test) ||
            request.Source == NeraCommandSource.Test && !allowTestSource_)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                "不允许此连接来源", $"source={request.Source}.");
        }
        if (!snapshot.AiPermissions.Enabled)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                "本机智能体控制尚未开启", "AI 控制总权限已关闭。");
        }
        if (string.IsNullOrWhiteSpace(request.AgentBridgeVersion) || request.AgentBridgeVersion.Length > 128)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.InvalidArgument,
                "AgentBridge 版本无效", "agentBridgeVersion 为必填项，且最多允许 128 个字符。");
        }
        if (!string.IsNullOrWhiteSpace(request.ExpectedAppVersion) &&
            !string.Equals(request.ExpectedAppVersion, snapshot.AppVersion, StringComparison.Ordinal))
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.VersionMismatch,
                "Nera 版本不匹配", $"expected={request.ExpectedAppVersion}; actual={snapshot.AppVersion}.");
        }
        if (!string.IsNullOrWhiteSpace(expectedAgentBridgeVersion_) &&
            !string.Equals(expectedAgentBridgeVersion_, request.AgentBridgeVersion, StringComparison.Ordinal))
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.VersionMismatch,
                "AgentBridge 版本不匹配",
                $"expected={expectedAgentBridgeVersion_}; actual={request.AgentBridgeVersion}.");
        }

        var decision = await authorizer_.AuthorizeAsync(new NeraAgentConnectionRequest
        {
            AgentBridgeVersion = request.AgentBridgeVersion,
            Source = request.Source,
            Timestamp = request.Timestamp
        }, cancellationToken).ConfigureAwait(false);
        if (decision == NeraAgentConnectionDecision.Deny)
        {
            return Failure(request.RequestId, snapshot, NeraControlCodes.PermissionDenied,
                "已拒绝本机 AgentBridge", "本机连接授权器返回 Deny。" );
        }

        session.HandshakeComplete = true;
        session.AgentBridgeVersion = request.AgentBridgeVersion;
        session.Source = request.Source;
        var data = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = NeraControlWireProtocol.CurrentProtocolVersion,
            appVersion = snapshot.AppVersion,
            runtimeAdapterVersion = snapshot.RuntimeAdapterVersion,
            agentBridgeVersion = request.AgentBridgeVersion,
            authorization = decision.ToString()
        }, NeraJson.Options);
        return Success(request.RequestId, snapshot, "本机连接已确认") with { Data = data };
    }

    private static bool IsAllowed(NeraCommandKind kind, NeraAiControlPermissions permissions)
    {
        if (kind != NeraCommandKind.EmergencyStop && !permissions.ReadState)
        {
            return false;
        }
        return kind switch
        {
        NeraCommandKind.SetDisplay => permissions.ReadDisplays && permissions.SetDisplay,
        NeraCommandKind.EnableDldr or NeraCommandKind.DisableDldr => permissions.ToggleDldr,
        NeraCommandKind.SetMode or NeraCommandKind.SetStrength or NeraCommandKind.SetPerformanceMode or
            NeraCommandKind.SetFeature18Tuning or NeraCommandKind.SetDldrTuning =>
            permissions.ModifyDldrSettings,
        NeraCommandKind.ShowHud or NeraCommandKind.HideHud => permissions.ControlHud,
        NeraCommandKind.RunSelfTest => permissions.RunDiagnostics,
        NeraCommandKind.EmergencyStop => true,
        _ => false
        };
    }

    private static NeraCommandKind ToCommandKind(NeraWireCommand command) => command switch
    {
        NeraWireCommand.SetDisplay => NeraCommandKind.SetDisplay,
        NeraWireCommand.EnableDldr => NeraCommandKind.EnableDldr,
        NeraWireCommand.DisableDldr => NeraCommandKind.DisableDldr,
        NeraWireCommand.SetMode => NeraCommandKind.SetMode,
        NeraWireCommand.SetStrength => NeraCommandKind.SetStrength,
        NeraWireCommand.SetPerformanceMode => NeraCommandKind.SetPerformanceMode,
        NeraWireCommand.SetUiProtection => NeraCommandKind.SetUiProtection,
        NeraWireCommand.SetFeature18Tuning => NeraCommandKind.SetFeature18Tuning,
        NeraWireCommand.SetDldrTuning => NeraCommandKind.SetDldrTuning,
        NeraWireCommand.ShowHud => NeraCommandKind.ShowHud,
        NeraWireCommand.HideHud => NeraCommandKind.HideHud,
        NeraWireCommand.RunSelfTest => NeraCommandKind.RunSelfTest,
        NeraWireCommand.EmergencyStop => NeraCommandKind.EmergencyStop,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "此线协议命令不是修改操作。")
    };

    private static bool ValidRequestIdentity(NeraWireRequest request) =>
        !string.IsNullOrWhiteSpace(request.RequestId) && request.RequestId.Length <= 128 &&
        !request.RequestId.Any(char.IsControl) && request.Timestamp != default;

    private static NeraWireResponse Success(string requestId, NeraStateSnapshot state, string userMessage) => new()
    {
        Ok = true,
        RequestId = requestId,
        PreviousRevision = state.Revision,
        CurrentRevision = state.Revision,
        State = SanitizeState(state),
        Code = NeraControlCodes.Ok,
        UserMessage = userMessage
    };

    internal static NeraWireResponse Failure(
        string requestId,
        NeraStateSnapshot state,
        string code,
        string userMessage,
        string technicalMessage) => new()
    {
        Ok = false,
        RequestId = requestId,
        PreviousRevision = state.Revision,
        CurrentRevision = state.Revision,
        State = SanitizeState(state),
        Code = code,
        UserMessage = userMessage,
        TechnicalMessage = state.AiPermissions.RunDiagnostics ? technicalMessage : string.Empty
    };

    private static NeraStateSnapshot SanitizeState(NeraStateSnapshot state)
    {
        if (!state.AiPermissions.ReadState)
        {
            return NeraStateSnapshot.CreateInitial(state.AppVersion, state.RuntimeAdapterVersion) with
            {
                Revision = state.Revision,
                UpdatedAt = state.UpdatedAt
            };
        }
        return state with
        {
            RuntimePath = null,
            TargetDisplay = state.AiPermissions.ReadDisplays ? state.TargetDisplay : null,
            AvailableDisplays = state.AiPermissions.ReadDisplays ? state.AvailableDisplays : [],
            LastError = state.LastError is null
                ? null
                : state.LastError with { TechnicalMessage = string.Empty }
        };
    }
}
