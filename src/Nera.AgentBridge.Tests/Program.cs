using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChipsStudio.Nera.AgentBridge;
using ChipsStudio.Nera.Localization;
using Control = ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.AgentBridge.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Body)> Tests =
    [
        ("Catalog.ExactToolsAndStrictSchemas", CatalogExactToolsAndStrictSchemas),
        ("PipeName.StableSidHash", PipeNameStableSidHash),
        ("Framing.RoundTrip", FramingRoundTrip),
        ("Framing.RejectsOversizeAndTruncation", FramingRejectsOversizeAndTruncation),
        ("PipeResponse.ValidatesIdentityAndProtocol", PipeResponseValidatesIdentityAndProtocol),
        ("Wire.StateSnapshotPropertyParity", WireStateSnapshotPropertyParity),
        ("Wire.NativeLifecycleHealthAndV6Negotiation", WireNativeLifecycleHealthAndV6Negotiation),
        ("Wire.WarmOverlayAndParameterAckStateRoundTrip", WireWarmOverlayAndParameterAckStateRoundTrip),
        ("Pipe.RealControlHandshakeAndPersistentSession", PipeRealControlHandshakeAndPersistentSession),
        ("Pipe.MissingEndpointIsServiceUnavailable", PipeMissingEndpointIsServiceUnavailable),
        ("Dispatcher.AllToolsHaveBoundedWireMappings", DispatcherAllToolsHaveBoundedWireMappings),
        ("Dispatcher.DiagnosticsExposeCleanupOwnership", DispatcherDiagnosticsExposeCleanupOwnership),
        ("Dispatcher.RecommendationUsesMeasuredHeadroom", DispatcherRecommendationUsesMeasuredHeadroom),
        ("Program.FatalExceptionDetailsStayInTrace", ProgramFatalExceptionDetailsStayInTrace),
        ("Mcp.ModernDiscover", McpModernDiscover),
        ("Mcp.ModernToolsList", McpModernToolsList),
        ("Mcp.ToolsListTitlesAreExactSimplifiedChinese", McpToolsListTitlesAreExactSimplifiedChinese),
        ("Localization.ThreeLanguagesPreserveMachineContract", LocalizationThreeLanguagesPreserveMachineContract),
        ("Localization.LanguageOptionsCanonicalOrder", LocalizationLanguageOptionsCanonicalOrder),
        ("Mcp.ModernMetadataRequired", McpModernMetadataRequired),
        ("Mcp.UnsupportedVersion", McpUnsupportedVersion),
        ("Mcp.ReadToolDispatch", McpReadToolDispatch),
        ("Mcp.MutationRequiresRevision", McpMutationRequiresRevision),
        ("Mcp.MutationForwardsRevision", McpMutationForwardsRevision),
        ("Mcp.RemovedProductToolsAreUnknown", McpImagePathIsLocalAndTyped),
        ("Mcp.PermissionFailureIsToolError", McpPermissionFailureIsToolError),
        ("Mcp.ExceptionDetailsStayTechnical", McpExceptionDetailsStayTechnical),
        ("Mcp.LegacyInitializeCompatibility", McpLegacyInitializeCompatibility),
        ("Mcp.LegacyRejectsOtherVersions", McpLegacyRejectsOtherVersions),
        ("Mcp.MalformedAndBatchRejected", McpMalformedAndBatchRejected),
        ("Mcp.UnknownNotificationHasNoResponse", McpUnknownNotificationHasNoResponse),
        ("Mcp.MessageLimit", McpMessageLimit),
        ("Cli.StatusJson", CliStatusJson),
        ("Cli.ReadsDefaultToHumanAndJsonIsExplicit", CliReadsDefaultToHumanAndJsonIsExplicit),
        ("Cli.HumanStateTranslationsMatchWireCasing", CliHumanStateTranslationsMatchWireCasing),
        ("Cli.ExceptionDetailsStayTechnical", CliExceptionDetailsStayTechnical),
        ("Cli.MutationUsesLatestRevision", CliMutationUsesLatestRevision),
        ("Cli.PermissionExitCode", CliPermissionExitCode),
        ("Cli.RuntimeUnavailableExitCode", CliRuntimeUnavailableExitCode),
        ("Cli.UsageExitCode", CliUsageExitCode),
        ("Cli.RemovedVisualControlsAreRejected", CliRemovedVisualControlsAreRejected)
    ];

    private static async Task<int> Main()
    {
        var passed = 0;
        var failed = 0;
        foreach (var test in Tests)
        {
            try
            {
                NeraLocalizer.SetLanguage("zh-CN");
                await test.Body().ConfigureAwait(false);
                ++passed;
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                ++failed;
                Console.WriteLine($"FAIL {test.Name}: {error}");
            }
        }
        Console.WriteLine($"RESULT pass={passed} fail={failed} total={Tests.Count}");
        return failed == 0 ? 0 : 1;
    }

    private static Task CatalogExactToolsAndStrictSchemas()
    {
        string[] expected =
        [
            "nera_get_status", "nera_get_displays", "nera_set_display",
            "nera_enable_dldr", "nera_disable_dldr", "nera_set_mode",
            "nera_set_strength", "nera_set_performance_mode", "nera_set_nr_parameters", "nera_set_dldr_parameters",
            "nera_set_hud", "nera_get_performance", "nera_recommend_settings",
            "nera_run_self_test", "nera_get_diagnostics", "nera_emergency_stop"
        ];
        True(expected.SequenceEqual(ToolCatalog.All.Select(tool => tool.Name)),
            "The MCP catalog must preserve the approved deterministic tool list.");
        foreach (var tool in ToolCatalog.All)
        {
            var definition = tool.ToMcpDefinition();
            True(definition["inputSchema"]?["additionalProperties"]?.GetValue<bool>() == false,
                $"{tool.Name} input schema must reject unknown fields.");
            True(definition["outputSchema"]?["additionalProperties"]?.GetValue<bool>() == false,
                $"{tool.Name} output schema must be strict.");
            var required = definition["inputSchema"]?["required"] as JsonArray;
            True(!tool.Mutating || required?.Any(node =>
                    node?.GetValue<string>() == "expected_revision") == true,
                $"{tool.Name} mutation must require expected_revision.");
        }
        return Task.CompletedTask;
    }

    private static Task PipeNameStableSidHash()
    {
        const string sid = "S-1-5-21-100-200-300-1001";
        var first = NeraControlProtocol.PipeNameForSid(sid);
        var second = NeraControlProtocol.PipeNameForSid(sid);
        Equal(first, second, "SID hash must be stable.");
        True(first.StartsWith("Nera.Control.", StringComparison.Ordinal), "Pipe prefix mismatch.");
        Equal(32, first["Nera.Control.".Length..].Length, "SID hash truncation length.");
        True(first["Nera.Control.".Length..].All(character =>
                char.IsDigit(character) || character is >= 'A' and <= 'F'),
            "Pipe hash must use uppercase hexadecimal.");
        True(!first.Contains(sid, StringComparison.Ordinal), "Raw SID must not appear in pipe name.");
        True(first != NeraControlProtocol.PipeNameForSid(sid + "-2"),
            "Different users must receive different pipe names.");
        return Task.CompletedTask;
    }

    private static Task WireStateSnapshotPropertyParity()
    {
        string[] controlProperties = typeof(Control.NeraStateSnapshot)
            .GetProperties()
            .Where(property => property.GetCustomAttributes(
                typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] bridgeProperties = typeof(NeraStateSnapshot)
            .GetProperties()
            .Where(property => property.GetCustomAttributes(
                typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        True(controlProperties.SequenceEqual(bridgeProperties),
            "AgentBridge strict state mirror drifted from Nera.Control: " +
            $"control=[{string.Join(',', controlProperties)}] bridge=[{string.Join(',', bridgeProperties)}]");
        return Task.CompletedTask;
    }

    private static async Task WireNativeLifecycleHealthAndV6Negotiation()
    {
        static string[] PublicInstanceProperties(Type type) => type.GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name + ":" + property.PropertyType.FullName)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        True(PublicInstanceProperties(typeof(Control.NeraNativeLifecycleHealth))
                .SequenceEqual(PublicInstanceProperties(typeof(NeraNativeLifecycleHealth))),
            "Native lifecycle health mirror must have exact nullable numeric/string field types.");
        True(PublicInstanceProperties(typeof(Control.NeraErrorInfo))
                .SequenceEqual(PublicInstanceProperties(typeof(NeraErrorInfo))),
            "Native error reason must not break the strict error mirror.");
        var authoritative = Control.NeraStateSnapshot.CreateInitial() with
        {
            HostSessionGeneration = 19, ProtectedRecoveryPending = true,
            NativeLifecycleHealth = new()
            {
                ReasonCode = "BLACK_FRAME_GUARD", LifecycleStage = "FIRST_PRESENT_FRAME",
                HeartbeatAgeMs = 12, LastSuccessfulFrameAgeMs = null,
                NativeHResult = 0x887A0005, NativeError = 5, LastWin32Error = 87,
                ProcessExitCode = 0xC0000142, StartupRetryAttempts = 0,
                StartupRetryPending = false, StartupAutoRetryEnabled = false,
                StartupRetryReason = "BLACK_FRAME_GUARD"
            },
            LastError = new() { Code = "PIPELINE_FAILED", ReasonCode = "BLACK_FRAME_GUARD", UserMessage = "已恢复原画" }
        };
        var response = new Control.NeraWireResponse
        {
            Ok = true, RequestId = "native-health", PreviousRevision = 0, CurrentRevision = 0,
            State = authoritative, Code = "OK", UserMessage = "已读取状态"
        };
        byte[] fullResponse = Control.NeraControlWireProtocol.SerializeResponse(response);
        NeraCommandResult decoded = PipeResponseValidator.Decode(fullResponse, "native-health");
        True(decoded.State is { HostSessionGeneration: 19, ProtectedRecoveryPending: true,
            NativeLifecycleHealth: { ReasonCode: "BLACK_FRAME_GUARD", LifecycleStage: "FIRST_PRESENT_FRAME",
                HeartbeatAgeMs: 12, LastSuccessfulFrameAgeMs: null, NativeHResult: 0x887A0005,
                NativeError: 5, LastWin32Error: 87, ProcessExitCode: 0xC0000142,
                StartupRetryAttempts: 0, StartupRetryPending: false, StartupAutoRetryEnabled: false },
            LastError.ReasonCode: "BLACK_FRAME_GUARD" },
            "Strict Bridge status lost current native health or new-session/recovery evidence.");
        JsonObject injected = JsonNode.Parse(fullResponse)!.AsObject();
        injected["state"]!["nativeLifecycleHealth"]!["windowTitle"] = "private-title";
        Equal(NeraErrorCodes.MalformedResponse, Throws<NeraTransportException>(() =>
            PipeResponseValidator.Decode(Encoding.UTF8.GetBytes(injected.ToJsonString()), "native-health")).StableCode,
            "Strict health DTO must reject extra fields; do not add extension-data passthrough.");

        // Freeze the v6 strict schema by removing exactly the added v7 members from
        // the existing DTO metadata. Required v6 members and unmapped rejection remain intact.
        var strictV6 = JsonSupport.CreateOptions();
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            string[] removed = info.Type == typeof(NeraStateSnapshot)
                ? ["hostSessionGeneration", "protectedRecoveryPending", "nativeLifecycleHealth"]
                : info.Type == typeof(NeraErrorInfo) ? ["reasonCode"] : [];
            foreach (var property in info.Properties.Where(property => removed.Contains(property.Name)).ToArray())
                info.Properties.Remove(property);
        });
        strictV6.TypeInfoResolver = resolver;
        Throws<JsonException>(() => JsonSerializer.Deserialize<NeraCommandResult>(fullResponse, strictV6));
        var authorizer = new TestAuthorizer();
        var handler = new Control.NeraControlWireHandler(new TestControlClient(), authorizer, "0.4.1");
        Control.NeraWireResponse mismatch = await handler.HandleAsync(new()
        {
            ProtocolVersion = 6, RequestId = "legacy-v6", Source = Control.NeraCommandSource.Agent,
            Command = Control.NeraWireCommand.Handshake, Timestamp = DateTimeOffset.UtcNow,
            AgentBridgeVersion = "0.4.1"
        }, new());
        byte[] negotiation = Control.NeraControlWireProtocol.SerializeResponseForPeer(mismatch, 6);
        NeraCommandResult? oldClientReply = JsonSerializer.Deserialize<NeraCommandResult>(negotiation, strictV6);
        True(oldClientReply is { Ok: false, ProtocolVersion: 7, Code: "PROTOCOL_MISMATCH" },
            "A strict v6 client must decode the explicit mismatch, not fail on an unknown v7 state field.");
        Equal(0, authorizer.CallCount, "A stale client must fail before prompting for permission.");
        True(!Encoding.UTF8.GetString(negotiation).Contains("nativeLifecycleHealth", StringComparison.Ordinal),
            "Mismatch negotiation must not return a partial success snapshot.");
    }

    private static Task WireWarmOverlayAndParameterAckStateRoundTrip()
    {
        var authoritative = Control.NeraStateSnapshot.CreateInitial() with
        {
            Revision = 31,
            DldrActualState = Control.NeraDldrActualState.Bypass,
            WarmStandby = true,
            WarmOffGateSatisfied = true,
            HostConnected = true,
            FeatureCreated = true,
            OffGateSatisfied = false,
            AppliedParameterRevision = 27,
            AppliedParameterFrameId = 1042
        };
        Control.NeraWireResponse reply = new()
        {
            Ok = true,
            RequestId = "warm-state",
            PreviousRevision = 30,
            CurrentRevision = 31,
            State = authoritative,
            Code = "OK",
            UserMessage = "已关闭"
        };
        byte[] payload = Control.NeraControlWireProtocol.SerializeResponse(reply);
        NeraCommandResult decoded = PipeResponseValidator.Decode(payload, "warm-state");
        True(decoded.State is { WarmStandby: true, WarmOffGateSatisfied: true,
                OverlayBypass: false, AppliedParameterRevision: 27, AppliedParameterFrameId: 1042,
                OffGateSatisfied: false, HostConnected: true, FeatureCreated: true },
            "Strict bridge mirror must preserve warm output gate without claiming full release.");
        payload = Control.NeraControlWireProtocol.SerializeResponse(reply with
        {
            State = authoritative with
            {
                WarmStandby = false,
                WarmOffGateSatisfied = false,
                OverlayBypass = true,
                DldrRequested = true
            }
        });
        decoded = PipeResponseValidator.Decode(payload, "warm-state");
        True(decoded.State is { OverlayBypass: true, DldrRequested: true, DldrOn: false },
            "Temporary overlay bypass is requested, not actual ON.");
        True(!Encoding.UTF8.GetString(payload).Contains("uiProtection", StringComparison.Ordinal),
            "Removed UI protection must not reappear in stable status JSON.");
        Equal(Control.NeraControlWireProtocol.CurrentProtocolVersion, NeraControlProtocol.Version,
            "App and AgentBridge protocol versions must advance together for a strict snapshot shape.");
        return Task.CompletedTask;
    }

    private static async Task FramingRoundTrip()
    {
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
        await using var stream = new MemoryStream();
        await PipeFraming.WriteFrameAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;
        var decoded = await PipeFraming.ReadFrameAsync(stream, CancellationToken.None);
        True(payload.SequenceEqual(decoded), "Framed payload mismatch.");
    }

    private static async Task FramingRejectsOversizeAndTruncation()
    {
        await ThrowsAsync<NeraTransportException>(() => PipeFraming.WriteFrameAsync(
            new MemoryStream(), new byte[NeraControlProtocol.MaximumMessageBytes + 1],
            CancellationToken.None));
        var truncated = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 20);
        await truncated.WriteAsync(header);
        await truncated.WriteAsync(new byte[2]);
        truncated.Position = 0;
        var error = await ThrowsAsync<NeraTransportException>(() =>
            PipeFraming.ReadFrameAsync(truncated, CancellationToken.None));
        Equal(NeraErrorCodes.MalformedResponse, error.StableCode,
            "Truncated frame error code mismatch.");
    }

    private static Task PipeResponseValidatesIdentityAndProtocol()
    {
        var valid = Success("req-1", 3) with
        {
            State = State(3) with
            {
                DldrActualState = "failed",
                RecoveryState = "failed",
                BrokerProcessOwned = true,
                BrokerConnected = false,
                OffGateSatisfied = false
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(valid, JsonSupport.Compact);
        NeraCommandResult decoded = PipeResponseValidator.Decode(bytes, "req-1");
        Equal("req-1", decoded.RequestId,
            "Valid response rejected.");
        True(decoded.State is { BrokerProcessOwned: true, BrokerConnected: false,
                OffGateSatisfied: false },
            "Wire decode lost independent Broker ownership/OFF-gate evidence.");
        var mismatch = Throws<NeraTransportException>(() =>
            PipeResponseValidator.Decode(bytes, "other"));
        Equal(NeraErrorCodes.MalformedResponse, mismatch.StableCode, "requestId mismatch code.");
        var wrongVersion = valid with { ProtocolVersion = 99 };
        var protocol = Throws<NeraTransportException>(() => PipeResponseValidator.Decode(
            JsonSerializer.SerializeToUtf8Bytes(wrongVersion, JsonSupport.Compact), "req-1"));
        Equal(NeraErrorCodes.ProtocolMismatch, protocol.StableCode, "protocol mismatch code.");
        return Task.CompletedTask;
    }

    private static async Task PipeRealControlHandshakeAndPersistentSession()
    {
        var pipeName = "Nera.Control.AgentBridgeTest." + Guid.NewGuid().ToString("N");
        var control = new TestControlClient();
        var authorizer = new TestAuthorizer();
        await using var server = new Control.NeraControlPipeServer(control, authorizer,
            new Control.NeraControlPipeServerOptions
            {
                PipeName = pipeName,
                ExpectedAgentBridgeVersion = NeraControlProtocol.AgentBridgeVersion
            });
        await server.StartAsync();
        await using var transport = new NamedPipeNeraControlTransport(pipeName);
        var dispatcher = new NeraCommandDispatcher(transport);
        using var empty = JsonDocument.Parse("{}");
        var first = await dispatcher.InvokeToolAsync("nera_get_status", empty.RootElement,
            NeraControlProtocol.AgentSource, default);
        True(first.Ok, "Real Control getStatus failed after handshake.");
        Equal(7L, first.CurrentRevision, "Real Control revision mismatch.");

        using var mutation = JsonDocument.Parse("{\"strength\":61,\"expected_revision\":7}");
        var changed = await dispatcher.InvokeToolAsync("nera_set_strength", mutation.RootElement,
            NeraControlProtocol.AgentSource, default);
        True(changed.Ok, "Real Control mutation failed.");
        Equal("SetStrength", control.LastKind?.ToString(), "Control command mapping mismatch.");
        Equal(1, authorizer.CallCount, "Persistent connection repeated authorization.");
    }

    private static async Task DispatcherAllToolsHaveBoundedWireMappings()
    {
        var cases = new (string Tool, JsonObject Args, string Wire)[]
        {
            ("nera_get_status", new(), "getStatus"),
            ("nera_get_displays", new(), "getDisplays"),
            ("nera_set_display", Revision(("display_id", "display-1")), "setDisplay"),
            ("nera_enable_dldr", Revision(), "enableDldr"),
            ("nera_disable_dldr", Revision(), "disableDldr"),
            ("nera_set_mode", Revision(("mode", "clear")), "setMode"),
            ("nera_set_strength", Revision(("strength", 42)), "setStrength"),
            ("nera_set_performance_mode", Revision(("performance_mode", "smooth")), "setPerformanceMode"),
            ("nera_set_nr_parameters", Revision(("style", 1), ("intensity", .5),
                ("local_tone", 1), ("local_structure", 1.5), ("skin_structure", -1),
                ("automatic_skin_mask", false)), "setFeature18Tuning"),
            ("nera_set_dldr_parameters", Revision(("highlight_protection", .9),
                ("shadow_protection", .85), ("chroma_strength", .5), ("temporal_response", .35)), "setDldrTuning"),
            ("nera_set_hud", Revision(("enabled", false)), "hideHud"),
            ("nera_get_performance", new(), "getStatus"),
            ("nera_recommend_settings", new(), "getStatus"),
            ("nera_run_self_test", Revision(("scope", "quick")), "runSelfTest"),
            ("nera_get_diagnostics", new(), "getStatus"),
            ("nera_emergency_stop", Revision(), "emergencyStop")
        };
        foreach (var item in cases)
        {
            var fake = new FakeTransport();
            var dispatcher = new NeraCommandDispatcher(fake);
            var element = JsonSerializer.SerializeToElement(item.Args, JsonSupport.Compact);
            var result = await dispatcher.InvokeToolAsync(item.Tool, element,
                NeraControlProtocol.AgentSource, default);
            True(result.Ok, $"{item.Tool} failed through FakeTransport.");
            Equal(1, fake.Requests.Count, $"{item.Tool} request count.");
            Equal(item.Wire, fake.Requests[0].Command, $"{item.Tool} wire mapping.");
            if (item.Tool == "nera_set_mode")
            {
                Equal("clear", fake.Requests[0].Payload["mode"]?.GetValue<string>(),
                    "AgentBridge must preserve the public clear token on the Control wire.");
            }
        }

        static JsonObject Revision(params (string Name, object Value)[] values)
        {
            var result = new JsonObject { ["expected_revision"] = 1 };
            foreach (var value in values)
                result[value.Name] = JsonSerializer.SerializeToNode(value.Value);
            return result;
        }
    }

    private static async Task DispatcherRecommendationUsesMeasuredHeadroom()
    {
        var measured = State(17) with
        {
            InputWidth = 3840,
            InputHeight = 2160,
            OutputWidth = 3840,
            OutputHeight = 2160,
            InputFps = 60,
            OutputFps = 54,
            NeuralScale = 100,
            Performance = new NeraPerformanceSnapshot
            {
                InputFramesPerSecond = 60,
                ProcessingFramesPerSecond = 54,
                Feature18Milliseconds = 16.2,
                GpuPercent = 87
            }
        };
        var fake = new FakeTransport(envelope => Success(envelope.RequestId, 17) with
        {
            State = measured
        });
        var dispatcher = new NeraCommandDispatcher(fake);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NeraCommandResult result = await dispatcher.InvokeToolAsync(
            "nera_recommend_settings", empty, NeraControlProtocol.AgentSource, default);
        True(result.Ok && result.Data.HasValue, "Recommendation did not return structured data.");
        JsonElement data = result.Data!.Value;
        JsonElement recommendation = data.GetProperty("recommendation");
        Equal("smooth", recommendation.GetProperty("performanceMode").GetString(),
            "4K60 constrained recommendation.");
        Equal(80, recommendation.GetProperty("neuralScale").GetInt32(),
            "4K60 constrained neural scale.");
        Equal("high", data.GetProperty("confidence").GetString(),
            "Measured recommendation confidence.");
        True(data.GetProperty("reason").GetString()!.Contains("16.20 ms", StringComparison.Ordinal),
            "Recommendation reason omitted measured Feature 18 timing.");
        True(data.GetProperty("expectedEffect").GetString()!.Contains(
                "最终输出分辨率", StringComparison.Ordinal),
            "Recommendation omitted expected effect.");
        True(data.GetProperty("advisoryOnly").GetBoolean(),
            "Recommendation must never apply itself.");

        var unmeasured = State(18) with { NeuralScale = 80 };
        fake = new FakeTransport(envelope => Success(envelope.RequestId, 18) with
        {
            State = unmeasured
        });
        dispatcher = new NeraCommandDispatcher(fake);
        result = await dispatcher.InvokeToolAsync(
            "nera_recommend_settings", empty, NeraControlProtocol.AgentSource, default);
        data = result.Data!.Value;
        recommendation = data.GetProperty("recommendation");
        Equal("smooth", recommendation.GetProperty("performanceMode").GetString(),
            "Unmeasured recommendation must preserve the current explicit scale.");
        Equal("low", data.GetProperty("confidence").GetString(),
            "Unmeasured recommendation confidence.");
    }

    private static async Task PipeMissingEndpointIsServiceUnavailable()
    {
        await using var transport = new NamedPipeNeraControlTransport(
            "Nera.Control.MissingTest." + Guid.NewGuid().ToString("N"));
        var error = await ThrowsAsync<NeraTransportException>(() => transport.SendAsync(
            new NeraCommandEnvelope
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Source = NeraControlProtocol.CliSource,
                Timestamp = DateTimeOffset.UtcNow,
                Command = "getStatus"
            }, TimeSpan.FromMilliseconds(100), default));
        Equal(NeraErrorCodes.ServiceUnavailable, error.StableCode,
            "A missing Control endpoint must use CLI exit-code 3 semantics.");
    }

    private static async Task McpModernDiscover()
    {
        var server = Server(new FakeTransport());
        using var response = Parse(await server.HandleLineAsync(
            Request("server/discover", new JsonObject { ["_meta"] = ModernMeta() }), default));
        var result = response.RootElement.GetProperty("result");
        Equal("complete", result.GetProperty("resultType").GetString(), "Modern resultType.");
        Equal(McpStdioServer.ModernProtocolVersion,
            result.GetProperty("supportedVersions")[0].GetString(), "Discover version.");
        Equal(McpStdioServer.LegacyProtocolVersion,
            result.GetProperty("supportedVersions")[1].GetString(), "Legacy compatibility version.");
        Equal("nera-agent-bridge", result.GetProperty("_meta")
            .GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString(),
            "Server identity metadata.");
    }

    private static async Task McpModernToolsList()
    {
        var server = Server(new FakeTransport());
        using var response = Parse(await server.HandleLineAsync(
            Request("tools/list", new JsonObject { ["_meta"] = ModernMeta() }), default));
        var result = response.RootElement.GetProperty("result");
        Equal("complete", result.GetProperty("resultType").GetString(), "Modern list resultType.");
        Equal(ToolCatalog.All.Count, result.GetProperty("tools").GetArrayLength(), "Tool count.");
        Equal("nera_get_status", result.GetProperty("tools")[0]
            .GetProperty("name").GetString(), "Deterministic first tool.");
    }

    private static async Task McpToolsListTitlesAreExactSimplifiedChinese()
    {
        IReadOnlyDictionary<string, string> expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nera_get_status"] = "获取状态",
            ["nera_get_displays"] = "获取显示器",
            ["nera_set_display"] = "选择显示器",
            ["nera_enable_dldr"] = "开启 DLDR",
            ["nera_disable_dldr"] = "关闭 DLDR",
            ["nera_set_mode"] = "设置画面方案",
            ["nera_set_strength"] = "设置 DLDR 效果强度",
            ["nera_set_performance_mode"] = "设置性能档位",
            ["nera_set_nr_parameters"] = "设置神经渲染参数",
            ["nera_set_dldr_parameters"] = "设置 HDR 回迁参数",
            ["nera_set_hud"] = "设置性能监视器",
            ["nera_get_performance"] = "获取性能数据",
            ["nera_recommend_settings"] = "获取设置建议",
            ["nera_run_self_test"] = "运行自检",
            ["nera_get_diagnostics"] = "获取诊断",
            ["nera_emergency_stop"] = "紧急停止"
        };
        var server = Server(new FakeTransport());
        using var response = Parse(await server.HandleLineAsync(
            Request("tools/list", new JsonObject { ["_meta"] = ModernMeta() }), default));
        JsonElement tools = response.RootElement.GetProperty("result").GetProperty("tools");
        Equal(expected.Count, tools.GetArrayLength(), "Every approved tool needs one exact title.");
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            True(expected.TryGetValue(name, out string? title), $"Unexpected MCP tool: {name}");
            Equal(title, tool.GetProperty("title").GetString(),
                $"MCP tool title is not the reviewed Simplified Chinese mapping: {name}");
        }
    }

    private static Task ProgramFatalExceptionDetailsStayInTrace()
    {
        const string secret = "English fatal exception detail must stay technical";
        var writer = new StringWriter();
        int exit = ChipsStudio.Nera.AgentBridge.Program.ReportFatalError(
            new InvalidOperationException(secret), writer);
        Equal(ExitCodes.OperationFailed, exit, "Fatal exception exit code.");
        string userText = writer.ToString();
        True(!userText.Contains(secret, StringComparison.Ordinal) &&
             !userText.Contains(nameof(InvalidOperationException), StringComparison.Ordinal),
            "Fatal exception detail leaked into ordinary stderr text.");
        True(userText.Contains(NeraLocalizer.Get("Error.OPERATION_FAILED"), StringComparison.Ordinal),
            "Fatal exception omitted the shared localized stable user message.");
        return Task.CompletedTask;
    }

    private static Task LocalizationLanguageOptionsCanonicalOrder()
    {
        string[] preferences = ["system", "en-US", "zh-CN", "zh-TW"];
        True(NeraLocalizer.SupportedPreferences.SequenceEqual(preferences), "System must be pinned first.");
        True(NeraLocalizer.SupportedLanguages.SequenceEqual(preferences.Skip(1)), "Canonical English ordering.");
        True(NeraLocalizer.LanguageOptions.Count(option => option.IsSystem) == 1 &&
            NeraLocalizer.LanguageOptions[0].IsSystem, "Exactly one pinned system option.");
        True(NeraLocalizer.LanguageOptions.Skip(1).Select(option => option.CanonicalEnglishName)
            .SequenceEqual(["English", "Simplified Chinese", "Traditional Chinese"]), "Separate canonical sort keys.");
        foreach (var (language, systemLabel) in new[]
        {
            ("en-US", "Follow system"), ("zh-CN", "跟随系统"), ("zh-TW", "跟隨系統")
        })
        {
            NeraLocalizer.SetLanguage(language);
            True(NeraLocalizer.LanguageOptions.Select(option => option.Preference).SequenceEqual(preferences),
                "Logical order must not depend on UI language.");
            True(NeraLocalizer.LanguageOptions.Select(option => option.Display)
                .SequenceEqual([systemLabel, "English", "简体中文", "繁體中文"]),
                "Native display labels only; no regions or canonical sort keys.");
        }
        NeraLocalizer.SetLanguage("system", "zh-CN");
        Equal("system", NeraLocalizer.Preference, "System remains a selectable preference.");
        Equal("跟随系统", NeraLocalizer.LanguageOptions[0].Display, "System label follows resolved UI language.");
        return Task.CompletedTask;
    }

    private static async Task LocalizationThreeLanguagesPreserveMachineContract()
    {
        string[] languages = ["en-US", "zh-TW", "zh-CN"];
        foreach (string language in languages)
        {
            NeraLocalizer.SetLanguage(language);
            var fake = new FakeTransport(envelope => Success(envelope.RequestId, 87) with
            {
                State = State(87) with
                {
                    Language = language, LanguagePreference = language,
                    Strength = 71, NeuralScale = 90, Feature18Intensity = 0.8,
                    Performance = new NeraPerformanceSnapshot
                    {
                        PresenterTimingFlags = 3, PresenterTimingSampleCount = 120,
                        FrameTimeP99Milliseconds = 17.2, GpuUsageAvailable = true,
                        CpuUsageAvailable = false
                    }
                }
            });
            NeraCommandResult status = await new NeraCommandDispatcher(fake).InvokeToolAsync(
                "nera_get_status", JsonSerializer.SerializeToElement(new { }),
                NeraControlProtocol.CliSource, default);
            Equal(language, NeraLocalizer.Language, "Response language is authoritative.");
            Equal("OK", status.Code, "Localization cannot translate machine codes.");
            Equal(71, status.State!.Strength, "Localization changed effect strength.");
            Equal(90, status.State.NeuralScale, "Localization changed scale.");
            Equal(3U, status.State.Performance.PresenterTimingFlags, "Timing availability mirror.");
            Equal(NeraLocalizer.Get("Common.Applied"), status.LocalizedMessage,
                "Human message did not use the shared language resource.");
            var server = Server(fake);
            using var response = Parse(await server.HandleLineAsync(
                Request("tools/list", new JsonObject { ["_meta"] = ModernMeta() }), default));
            foreach (var tool in response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
            {
                string name = tool.GetProperty("name").GetString()!;
                Equal(NeraLocalizer.Get("Mcp.Title." + name), tool.GetProperty("title").GetString(),
                    "Tool title is not localized from the shared resource.");
                True(!tool.GetProperty("description").GetString()!.StartsWith("Mcp.", StringComparison.Ordinal),
                    "Tool description leaked a resource key.");
                True(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("expected_revision", out _) ||
                    !ToolCatalog.All.Single(t => t.Name == name).Mutating,
                    "Localized tool lost its machine revision field.");
            }
            var output = new StringWriter();
            Equal(ExitCodes.Success, await new CliApplication(new NeraCommandDispatcher(fake), output,
                new StringWriter()).RunAsync(["status"], default), "Localized CLI status.");
            True(output.ToString().Contains(NeraLocalizer.Get("Common.Disabled"), StringComparison.Ordinal),
                "CLI state ignored the authoritative language.");
        }
    }

    private static async Task McpModernMetadataRequired()
    {
        var server = Server(new FakeTransport());
        using var response = Parse(await server.HandleLineAsync(
            Request("tools/list", new JsonObject()), default));
        Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Missing metadata error code.");
    }

    private static async Task McpUnsupportedVersion()
    {
        var meta = ModernMeta();
        meta["io.modelcontextprotocol/protocolVersion"] = "2099-01-01";
        var server = Server(new FakeTransport());
        using var response = Parse(await server.HandleLineAsync(
            Request("tools/list", new JsonObject { ["_meta"] = meta }), default));
        Equal(-32022, response.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Unsupported protocol code.");
    }

    private static async Task McpReadToolDispatch()
    {
        var fake = new FakeTransport(envelope => Success(envelope.RequestId, 8));
        var server = Server(fake);
        using var response = Parse(await server.HandleLineAsync(ToolCall(
            "nera_get_status", new JsonObject()), default));
        True(!response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(),
            "Successful tool call marked error.");
        Equal("getStatus", fake.Requests.Single().Command, "Command mapping.");
        Equal(NeraControlProtocol.AgentSource, fake.Requests.Single().Source, "Agent source.");
        True(fake.Requests.Single().ExpectedRevision is null, "Read call gained a revision.");
    }

    private static async Task McpMutationRequiresRevision()
    {
        var fake = new FakeTransport();
        var server = Server(fake);
        using var response = Parse(await server.HandleLineAsync(ToolCall(
            "nera_set_strength", new JsonObject { ["strength"] = 50 }), default));
        Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Missing revision must be invalid params.");
        Equal(0, fake.Requests.Count, "Invalid mutation reached transport.");
    }

    private static async Task McpMutationForwardsRevision()
    {
        var fake = new FakeTransport(envelope => Success(envelope.RequestId, 10));
        var server = Server(fake);
        using var response = Parse(await server.HandleLineAsync(ToolCall(
            "nera_set_strength", new JsonObject
            {
                ["strength"] = 55,
                ["expected_revision"] = 9
            }), default));
        True(!response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean(),
            "Valid mutation failed.");
        var request = fake.Requests.Single();
        Equal(9L, request.ExpectedRevision, "Expected revision not forwarded.");
        True(!request.Payload.ContainsKey("expected_revision"),
            "Revision must be envelope metadata, not duplicated payload.");
        Equal(55, request.Payload["strength"]!.GetValue<int>(), "Strength payload.");
    }

    private static async Task McpImagePathIsLocalAndTyped()
    {
        var fake = new FakeTransport();
        var server = Server(fake);
        foreach (var removedTool in new[]
        {
            "nera_open_image", "nera_select_target", "nera_select_window",
            "nera_enable_dlss5", "nera_enter_fullscreen", "nera_list_profiles",
            "nera_set_ui_protection", "nera_set_preset", "nera_set_ui_correction"
        })
        {
            using var response = Parse(await server.HandleLineAsync(ToolCall(
                removedTool, new JsonObject()), default));
            Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
                $"Removed product tool was accepted: {removedTool}");
        }
        Equal(0, fake.Requests.Count, "Removed product tool reached transport.");
    }

    private static async Task McpPermissionFailureIsToolError()
    {
        var fake = new FakeTransport(envelope => Failure(envelope.RequestId,
            NeraErrorCodes.PermissionDenied));
        var server = Server(fake);
        using var response = Parse(await server.HandleLineAsync(ToolCall(
            "nera_get_diagnostics", new JsonObject()), default));
        var result = response.RootElement.GetProperty("result");
        True(result.GetProperty("isError").GetBoolean(), "Permission failure not marked isError.");
        Equal(NeraErrorCodes.PermissionDenied,
            result.GetProperty("structuredContent").GetProperty("code").GetString(),
            "Stable permission code missing.");
    }

    private static async Task McpExceptionDetailsStayTechnical()
    {
        const string unexpectedDetail = "English unexpected exception must stay technical";
        const string transportDetail = "English transport exception must stay technical";
        foreach ((Exception Error, string Detail, string ExpectedCode) scenario in new[]
        {
            ((Exception)new InvalidOperationException(unexpectedDetail), unexpectedDetail,
                NeraErrorCodes.OperationFailed),
            ((Exception)new NeraTransportException(NeraErrorCodes.ServiceUnavailable,
                transportDetail), transportDetail, NeraErrorCodes.ServiceUnavailable)
        })
        {
            var server = Server(new FakeTransport(_ => throw scenario.Error));
            using var response = Parse(await server.HandleLineAsync(ToolCall(
                "nera_get_status", new JsonObject()), default));
            JsonElement result = response.RootElement.GetProperty("result");
            JsonElement structured = result.GetProperty("structuredContent");
            string userText = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            True(result.GetProperty("isError").GetBoolean(), "Injected exception was not an MCP tool error.");
            Equal(scenario.ExpectedCode, structured.GetProperty("code").GetString(),
                "Injected exception lost its stable error code.");
            True(!userText.Contains(scenario.Detail, StringComparison.Ordinal) &&
                 !structured.GetProperty("userMessage").GetString()!.Contains(
                     scenario.Detail, StringComparison.Ordinal),
                "Raw exception detail leaked into MCP user text.");
            True(structured.GetProperty("technicalMessage").GetString()!.Contains(
                    scenario.Detail, StringComparison.Ordinal),
                "Raw exception detail was not preserved in TechnicalMessage.");
        }

        const string upstreamTechnical = "English upstream technical detail must not become content";
        var upstreamServer = Server(new FakeTransport(envelope =>
            Failure(envelope.RequestId, NeraErrorCodes.OperationFailed) with
            {
                UserMessage = string.Empty,
                TechnicalMessage = upstreamTechnical
            }));
        using var upstreamResponse = Parse(await upstreamServer.HandleLineAsync(ToolCall(
            "nera_get_status", new JsonObject()), default));
        JsonElement upstreamResult = upstreamResponse.RootElement.GetProperty("result");
        string upstreamUserText = upstreamResult.GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        True(!upstreamUserText.Contains(upstreamTechnical, StringComparison.Ordinal),
            "TechnicalMessage was used as the MCP human-readable fallback.");
        True(upstreamResult.GetProperty("structuredContent").GetProperty("technicalMessage")
                .GetString()!.Contains(upstreamTechnical, StringComparison.Ordinal),
            "Structured MCP result lost the upstream TechnicalMessage.");
    }

    private static async Task McpLegacyInitializeCompatibility()
    {
        var server = Server(new FakeTransport());
        var initialize = new JsonObject
        {
            ["protocolVersion"] = McpStdioServer.LegacyProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "legacy-test", ["version"] = "1" }
        };
        using var initialized = Parse(await server.HandleLineAsync(
            Request("initialize", initialize), default));
        var initResult = initialized.RootElement.GetProperty("result");
        Equal(McpStdioServer.LegacyProtocolVersion,
            initResult.GetProperty("protocolVersion").GetString(), "Legacy negotiation.");
        True(!initResult.TryGetProperty("resultType", out _), "Legacy response gained modern field.");

        using var beforeReady = Parse(await server.HandleLineAsync(
            Request("tools/list", new JsonObject()), default));
        True(beforeReady.RootElement.TryGetProperty("error", out _),
            "Legacy tools/list accepted before initialized notification.");
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
            ["params"] = new JsonObject()
        }.ToJsonString();
        True(await server.HandleLineAsync(notification, default) is null,
            "Initialized notification emitted a response.");
        using var listed = Parse(await server.HandleLineAsync(
            Request("tools/list", new JsonObject()), default));
        True(!listed.RootElement.GetProperty("result").TryGetProperty("resultType", out _),
            "Legacy tools/list gained modern resultType.");
    }

    private static async Task McpLegacyRejectsOtherVersions()
    {
        var server = Server(new FakeTransport());
        var initialize = new JsonObject
        {
            ["protocolVersion"] = "2026-07-28",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "wrong-version", ["version"] = "1" }
        };
        using var response = Parse(await server.HandleLineAsync(
            Request("initialize", initialize), default));
        Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Legacy initialize accepted an unsupported version.");
    }

    private static async Task McpOperationStatusAndCancel()
    {
        var fake = new FakeTransport();
        var server = Server(fake);
        using var status = Parse(await server.HandleLineAsync(ToolCall(
            "nera_get_operation", new JsonObject { ["operation_id"] = "op-opaque" }), default));
        Equal(-32602, status.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Removed operation polling tool was accepted.");
    }

    private static async Task McpMalformedAndBatchRejected()
    {
        var server = Server(new FakeTransport());
        using var malformed = Parse(await server.HandleLineAsync("{", default));
        Equal(-32700, malformed.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Parse error code.");
        using var batch = Parse(await server.HandleLineAsync("[]", default));
        Equal(-32600, batch.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Batch rejection code.");
    }

    private static async Task McpUnknownNotificationHasNoResponse()
    {
        var server = Server(new FakeTransport());
        var notification = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unknown\"}";
        True(await server.HandleLineAsync(notification, default) is null,
            "Unknown notification must not receive a JSON-RPC response.");
    }

    private static async Task McpMessageLimit()
    {
        var server = Server(new FakeTransport());
        var oversized = new string('x', NeraControlProtocol.MaximumMessageBytes + 1);
        using var response = Parse(await server.HandleLineAsync(oversized, default));
        Equal(-32600, response.RootElement.GetProperty("error").GetProperty("code").GetInt32(),
            "Oversize MCP request code.");
        Equal(NeraErrorCodes.ResponseTooLarge,
            response.RootElement.GetProperty("error").GetProperty("data")
                .GetProperty("code").GetString(), "Oversize stable code.");
    }

    private static async Task CliStatusJson()
    {
        var fake = new FakeTransport(envelope => Success(envelope.RequestId, 4) with
        {
            State = State(4) with
            {
                DldrActualState = "failed",
                RecoveryState = "failed",
                BrokerProcessOwned = true,
                BrokerConnected = false,
                OffGateSatisfied = false
            }
        });
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new CliApplication(new NeraCommandDispatcher(fake), output, error)
            .RunAsync(["status", "--json"], default);
        Equal(ExitCodes.Success, exit, "status exit code.");
        using var json = JsonDocument.Parse(output.ToString());
        True(json.RootElement.GetProperty("ok").GetBoolean(), "status JSON result.");
        JsonElement state = json.RootElement.GetProperty("state");
        True(state.GetProperty("brokerProcessOwned").GetBoolean() &&
             !state.GetProperty("brokerConnected").GetBoolean() &&
             !state.GetProperty("offGateSatisfied").GetBoolean(),
            "Status JSON must expose cleanup ownership and complete OFF truth.");
        Equal("getStatus", fake.Requests.Single().Command, "status command mapping.");
    }

    private static async Task DispatcherDiagnosticsExposeCleanupOwnership()
    {
        var fake = new FakeTransport(envelope => Success(envelope.RequestId, 9) with
        {
            State = State(9) with
            {
                DldrActualState = "failed",
                RecoveryState = "failed",
                BrokerProcessOwned = true,
                BrokerConnected = false,
                OffGateSatisfied = false
            }
        });
        var dispatcher = new NeraCommandDispatcher(fake);
        using JsonDocument empty = JsonDocument.Parse("{}");
        NeraCommandResult result = await dispatcher.InvokeToolAsync(
            "nera_get_diagnostics", empty.RootElement,
            NeraControlProtocol.AgentSource, default);
        True(result.Data is JsonElement, "Diagnostics data was omitted.");
        JsonElement diagnostics = result.Data!.Value.GetProperty("diagnostics");
        True(diagnostics.GetProperty("brokerProcessOwned").GetBoolean() &&
             !diagnostics.GetProperty("brokerConnected").GetBoolean() &&
             !diagnostics.GetProperty("offGateSatisfied").GetBoolean(),
            "Diagnostics must distinguish cleanup ownership, readiness, and complete OFF.");
    }

    private static async Task CliReadsDefaultToHumanAndJsonIsExplicit()
    {
        string[][] commands =
        [
            ["status"], ["displays"], ["performance"], ["recommend"], ["diagnostics"]
        ];
        foreach (string[] command in commands)
        {
            var humanOutput = new StringWriter();
            int humanExit = await new CliApplication(
                new NeraCommandDispatcher(new FakeTransport()), humanOutput, new StringWriter())
                .RunAsync(command, default);
            Equal(ExitCodes.Success, humanExit, $"{command[0]} human exit.");
            string human = humanOutput.ToString().Trim();
            True(human.Length > 0 && !human.StartsWith('{'),
                $"{command[0]} must default to concise human-readable output.");

            var jsonOutput = new StringWriter();
            int jsonExit = await new CliApplication(
                new NeraCommandDispatcher(new FakeTransport()), jsonOutput, new StringWriter())
                .RunAsync([command[0], "--json"], default);
            Equal(ExitCodes.Success, jsonExit, $"{command[0]} JSON exit.");
            using JsonDocument parsed = JsonDocument.Parse(jsonOutput.ToString());
            True(parsed.RootElement.GetProperty("ok").GetBoolean(),
                $"{command[0]} --json must preserve the stable result envelope.");
        }
    }

    private static async Task CliHumanStateTranslationsMatchWireCasing()
    {
        NeraStateSnapshot translatedState = State(12) with
        {
            DldrActualState = "disabled",
            ProcessingMode = "clear",
            PerformanceMode = "balanced",
            UiProtection = "auto",
            RecoveryState = "bypassRestored"
        };
        var fake = new FakeTransport(envelope => Success(envelope.RequestId, 12) with
        {
            State = translatedState
        });

        var statusOutput = new StringWriter();
        int statusExit = await new CliApplication(new NeraCommandDispatcher(fake),
            statusOutput, new StringWriter()).RunAsync(["status"], default);
        Equal(ExitCodes.Success, statusExit, "translated status exit.");
        string status = statusOutput.ToString();
        True(status.Contains("DLDR OFF · 已关闭", StringComparison.Ordinal),
            "camelCase disabled state must be rendered as 已关闭.");
        True(status.Contains("画面方案：清晰", StringComparison.Ordinal),
            "public clear mode must be rendered as 清晰.");
        True(status.Contains("平衡（神经输入", StringComparison.Ordinal),
            "camelCase balanced mode must be rendered as 平衡.");
        True(!status.Contains("界面保护", StringComparison.Ordinal) &&
             status.Contains("神经增强强度", StringComparison.Ordinal),
            "Removed UI protection must not appear; NR intensity must be distinguished.");

        var diagnosticsOutput = new StringWriter();
        int diagnosticsExit = await new CliApplication(new NeraCommandDispatcher(fake),
            diagnosticsOutput, new StringWriter()).RunAsync(["diagnostics"], default);
        Equal(ExitCodes.Success, diagnosticsExit, "translated diagnostics exit.");
        string diagnostics = diagnosticsOutput.ToString();
        True(diagnostics.Contains("DLDR 已关闭", StringComparison.Ordinal),
            "diagnostics must render camelCase disabled state correctly.");
        True(diagnostics.Contains("恢复状态 已恢复原画", StringComparison.Ordinal),
            "diagnostics must render camelCase bypassRestored correctly.");
    }

    private static async Task CliMutationUsesLatestRevision()
    {
        var fake = new FakeTransport(envelope => Success(envelope.RequestId,
            envelope.Command == "getStatus" ? 40 : 41));
        var exit = await new CliApplication(new NeraCommandDispatcher(fake),
            new StringWriter(), new StringWriter()).RunAsync(["strength", "60"], default);
        Equal(ExitCodes.Success, exit, "mutation exit code.");
        Equal(2, fake.Requests.Count, "CLI must read revision then mutate.");
        Equal("getStatus", fake.Requests[0].Command, "First CLI request.");
        Equal(40L, fake.Requests[1].ExpectedRevision, "CLI optimistic revision.");
        Equal(NeraControlProtocol.CliSource, fake.Requests[1].Source, "CLI source.");
    }

    private static async Task CliExceptionDetailsStayTechnical()
    {
        const string humanDetail = "English CLI exception must stay technical";
        var humanOutput = new StringWriter();
        var humanError = new StringWriter();
        int humanExit = await new CliApplication(
            new NeraCommandDispatcher(new FakeTransport(_ =>
                throw new NeraTransportException(NeraErrorCodes.ServiceUnavailable, humanDetail))),
            humanOutput, humanError).RunAsync(["status"], default);
        Equal(ExitCodes.ServiceUnavailable, humanExit, "Human CLI exception exit code.");
        True(!humanError.ToString().Contains(humanDetail, StringComparison.Ordinal),
            "Raw transport exception leaked into human CLI stderr.");
        True(humanError.ToString().Contains("Nera 控制层未运行或暂时不可用", StringComparison.Ordinal),
            "Human CLI omitted the localized stable failure message.");

        const string jsonDetail = "English JSON exception must stay technical";
        var jsonOutput = new StringWriter();
        var jsonError = new StringWriter();
        int jsonExit = await new CliApplication(
            new NeraCommandDispatcher(new FakeTransport(_ =>
                throw new InvalidOperationException(jsonDetail))),
            jsonOutput, jsonError).RunAsync(["status", "--json"], default);
        Equal(ExitCodes.OperationFailed, jsonExit, "JSON CLI exception exit code.");
        True(string.IsNullOrWhiteSpace(jsonError.ToString()),
            "JSON CLI failure also wrote an ordinary stderr message.");
        using JsonDocument json = JsonDocument.Parse(jsonOutput.ToString());
        string userMessage = json.RootElement.GetProperty("userMessage").GetString()!;
        string technicalMessage = json.RootElement.GetProperty("technicalMessage").GetString()!;
        True(!userMessage.Contains(jsonDetail, StringComparison.Ordinal),
            "Raw exception leaked into JSON UserMessage.");
        True(technicalMessage.Contains(jsonDetail, StringComparison.Ordinal),
            "JSON TechnicalMessage did not preserve the raw exception detail.");

        const string upstreamTechnical = "English upstream CLI technical fallback must stay hidden";
        var upstreamError = new StringWriter();
        int upstreamExit = await new CliApplication(
            new NeraCommandDispatcher(new FakeTransport(envelope =>
                Failure(envelope.RequestId, NeraErrorCodes.OperationFailed) with
                {
                    UserMessage = string.Empty,
                    TechnicalMessage = upstreamTechnical
                })),
            new StringWriter(), upstreamError).RunAsync(["status"], default);
        Equal(ExitCodes.OperationFailed, upstreamExit, "Upstream CLI failure exit code.");
        True(!upstreamError.ToString().Contains(upstreamTechnical, StringComparison.Ordinal),
            "TechnicalMessage was used as the human CLI fallback.");
        True(upstreamError.ToString().Contains(NeraLocalizer.Get("Error.OPERATION_FAILED"), StringComparison.Ordinal),
            "Human CLI fallback omitted the stable localized message.");
    }

    private static async Task CliPermissionExitCode()
    {
        var fake = new FakeTransport(envelope => Failure(envelope.RequestId,
            NeraErrorCodes.PermissionDenied));
        var exit = await new CliApplication(new NeraCommandDispatcher(fake),
            new StringWriter(), new StringWriter()).RunAsync(["status", "--json"], default);
        Equal(ExitCodes.PermissionDenied, exit, "Permission exit code.");
    }

    private static async Task CliDoctorUnavailableExitCode()
    {
        var fake = new FakeTransport(_ => throw new NeraTransportException(
            NeraErrorCodes.ServiceUnavailable, "offline"));
        var output = new StringWriter();
        var exit = await new CliApplication(new NeraCommandDispatcher(fake), output,
            new StringWriter()).RunAsync(["doctor", "--json"], default);
        Equal(ExitCodes.ServiceUnavailable, exit, "Doctor unavailable exit.");
        using var json = JsonDocument.Parse(output.ToString());
        Equal(NeraErrorCodes.ServiceUnavailable, json.RootElement.GetProperty("code").GetString(),
            "Doctor stable code.");
        True(!json.RootElement.GetProperty("networkListener").GetBoolean(),
            "Doctor must report no network listener.");
    }

    private static async Task CliRuntimeUnavailableExitCode()
    {
        var fake = new FakeTransport(envelope => Failure(envelope.RequestId,
            NeraErrorCodes.RuntimeUnavailable));
        var exit = await new CliApplication(new NeraCommandDispatcher(fake),
            new StringWriter(), new StringWriter()).RunAsync(["status", "--json"], default);
        Equal(ExitCodes.RuntimeUnavailable, exit, "Runtime unavailable exit code.");
    }

    private static async Task CliUsageExitCode()
    {
        var exit = await new CliApplication(new NeraCommandDispatcher(new FakeTransport()),
            new StringWriter(), new StringWriter()).RunAsync(["strength", "101"], default);
        Equal(ExitCodes.Usage, exit, "Usage exit code.");
    }

    private static async Task CliRemovedVisualControlsAreRejected()
    {
        foreach (string removed in new[] { "ui-protection", "ui_protection", "preset", "ui-correction" })
        {
            var fake = new FakeTransport();
            int exit = await new CliApplication(new NeraCommandDispatcher(fake),
                new StringWriter(), new StringWriter()).RunAsync([removed, "on"], default);
            Equal(ExitCodes.Usage, exit, $"Removed CLI control was accepted: {removed}");
            Equal(0, fake.Requests.Count, $"Removed CLI control reached transport: {removed}");
        }
    }

    private static McpStdioServer Server(FakeTransport transport) =>
        new(new NeraCommandDispatcher(transport));

    private static JsonObject ModernMeta() => new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = McpStdioServer.ModernProtocolVersion,
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        ["io.modelcontextprotocol/clientInfo"] = new JsonObject
        {
            ["name"] = "bridge-tests", ["version"] = "1.0"
        }
    };

    private static string Request(string method, JsonObject parameters, object? id = null) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? JsonValue.Create(1) : JsonSerializer.SerializeToNode(id),
            ["method"] = method,
            ["params"] = parameters
        }.ToJsonString();

    private static string ToolCall(string name, JsonObject arguments) =>
        Request("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments,
            ["_meta"] = ModernMeta()
        });

    private static JsonDocument Parse(string? response)
    {
        True(response is not null, "Expected a JSON-RPC response.");
        return JsonDocument.Parse(response!);
    }

    private static NeraCommandResult Success(string requestId, long revision) => new()
    {
        ProtocolVersion = NeraControlProtocol.Version,
        RequestId = requestId,
        Ok = true,
        PreviousRevision = revision,
        CurrentRevision = revision,
        State = State(revision),
        Code = "OK",
        UserMessage = "ok",
        RollbackPerformed = false
    };

    private static NeraCommandResult Failure(string requestId, string code) => new()
    {
        ProtocolVersion = NeraControlProtocol.Version,
        RequestId = requestId,
        Ok = false,
        PreviousRevision = 0,
        CurrentRevision = 0,
        State = State(0),
        Code = code,
        UserMessage = "denied",
        RollbackPerformed = false
    };

    private static NeraStateSnapshot State(long revision) => new()
    {
        Language = NeraLocalizer.Language,
        LanguagePreference = NeraLocalizer.Preference,
        Revision = revision,
        UpdatedAt = DateTimeOffset.UtcNow,
        AppVersion = "0.4.1-global-alpha.1",
        RuntimeAdapterVersion = "0.4.1",
        WorkingColorSpace = "FP16 linear scRGB",
        DldrActualState = "disabled",
        ProcessingMode = "natural",
        Strength = 50,
        NeuralScale = 100,
        PerformanceMode = "quality",
        UiProtection = "auto",
        RecoveryState = "none",
        OffGateSatisfied = true
    };

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected={expected} Actual={actual}");
    }

    private static void Equal<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"{message} Expected=[{string.Join(',', expected)}] " +
                $"Actual=[{string.Join(',', actual)}]");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeTransport : INeraControlTransport
    {
        private readonly Func<NeraCommandEnvelope, NeraCommandResult> handler_;

        public FakeTransport(Func<NeraCommandEnvelope, NeraCommandResult>? handler = null) =>
            handler_ = handler ?? (envelope => Success(envelope.RequestId, 1));

        public List<NeraCommandEnvelope> Requests { get; } = [];

        public Task<NeraCommandResult> SendAsync(NeraCommandEnvelope envelope,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(envelope);
            return Task.FromResult(handler_(envelope));
        }
    }

    private sealed class TestAuthorizer : Control.INeraAgentConnectionAuthorizer
    {
        public int CallCount { get; private set; }

        public ValueTask<Control.NeraAgentConnectionDecision> AuthorizeAsync(
            Control.NeraAgentConnectionRequest request, CancellationToken cancellationToken)
        {
            ++CallCount;
            return ValueTask.FromResult(Control.NeraAgentConnectionDecision.AllowOnce);
        }
    }

    private sealed class TestControlClient : Control.INeraControlClient
    {
        private Control.NeraStateSnapshot state_ = Control.NeraStateSnapshot.CreateInitial() with
        {
            Revision = 7,
            AiPermissions = new Control.NeraAiControlPermissions
            {
                Enabled = true,
                ReadState = true,
                ModifyDldrSettings = true
            }
        };

#pragma warning disable CS0067
        public event EventHandler<Control.NeraStateSnapshot>? StateChanged;
#pragma warning restore CS0067

        public Control.NeraCommandKind? LastKind { get; private set; }

        public ValueTask<Control.NeraStateSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(state_);

        public Task<IReadOnlyList<Control.NeraDisplaySummary>> ListDisplaysAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(state_.AvailableDisplays);

        public Task<Control.NeraCommandResult> ExecuteAsync(
            Control.NeraCommandEnvelope command, CancellationToken cancellationToken = default)
        {
            LastKind = command.Kind;
            var previous = state_.Revision;
            state_ = state_ with
            {
                Revision = previous + 1,
                Strength = command.Payload.Strength ?? state_.Strength,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return Task.FromResult(new Control.NeraCommandResult
            {
                Ok = true,
                RequestId = command.RequestId,
                PreviousRevision = previous,
                CurrentRevision = state_.Revision,
                State = state_,
                Code = Control.NeraControlCodes.Ok,
                UserMessage = "ok"
            });
        }
    }
}
