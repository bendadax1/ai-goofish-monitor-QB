using System.Net;
using System.Text;
using System.Text.Json;
using AiGoofish.Launcher.Platform.Windows;

internal static class PortableLauncherUserClientCases
{
    private static readonly Guid InstanceId = Guid.Parse("94289b32-67b8-484c-a5be-9b33a3ed17ef");
    private const string ControlToken = "control-test-token-do-not-log";
    private const string AccessToken = "l1.short-lived-user-session-token";

    public static async Task TestPairingAndUserRequestsAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            request =>
            {
                AssertInternalRequest(request, "/internal/launcher/pairing");
                var body = ReadBody(request);
                Assert(body.GetProperty("instance_id").GetString() == InstanceId.ToString("D"), "配对请求必须绑定当前实例");
                var verifier = body.GetProperty("verifier").GetString()!;
                Assert(verifier.Length == 43 && verifier.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
                    "verifier 必须为 32-byte 随机值的 Base64Url 编码");
                return Json(HttpStatusCode.OK, new
                {
                    pairing_id = "pairing-test-id",
                    pairing_code = "ABCD1234EFGH",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                    expires_in_seconds = 300,
                });
            },
            request =>
            {
                AssertInternalRequest(request, "/internal/launcher/pairing/exchange");
                var body = ReadBody(request);
                Assert(body.GetProperty("pairing_id").GetString() == "pairing-test-id", "兑换必须复用配对 id");
                Assert(body.GetProperty("verifier").GetString()!.Length == 43, "兑换必须证明同一 verifier");
                return Json(HttpStatusCode.Accepted, new { status = "pending" });
            },
            request =>
            {
                AssertInternalRequest(request, "/internal/launcher/pairing/exchange");
                return Json(HttpStatusCode.OK, new
                {
                    status = "exchanged",
                    access_token = AccessToken,
                    token_type = "Bearer",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1),
                    instance_id = InstanceId.ToString("D"),
                });
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/me");
                return Json(HttpStatusCode.OK, new
                {
                    user_id = "user-1",
                    username = "alice",
                    is_active = true,
                    permissions = new { ai_read = true, ai_write = true },
                    instance_id = InstanceId.ToString("D"),
                });
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                return Json(HttpStatusCode.OK, AiResponse(baseUrl: "https://api.example.test/v1", apiKeySet: true));
            });
        using var context = CreateContext();
        using var client = new PortableLauncherUserClient(context, handler);

        var prompt = await client.BeginPairingAsync();
        Assert(prompt.PairingCode == "ABCD1234EFGH" && prompt.ExpiresInSeconds == 300, "Launcher 应显示可人工输入的配对码及期限");
        Assert((await client.ExchangePairingOnceAsync()).IsPending, "未批准时兑换必须返回 pending");
        Assert(!(await client.ExchangePairingOnceAsync()).IsPending, "明确批准后兑换应建立短期会话");
        var identity = await client.GetCurrentUserAsync();
        var ai = await client.GetAiConfigurationAsync();
        Assert(identity.UserId == "user-1" && identity.CanWriteAi, "应读取当前用户及其权限");
        Assert(ai.ApiKeySet && ai.BaseUrl == "https://api.example.test/v1", "AI 摘要应包含脱敏配置");
        Assert(!client.ToString().Contains(AccessToken, StringComparison.Ordinal), "客户端诊断文本不得包含 bearer");
        Assert(!ai.ToString().Contains(AccessToken, StringComparison.Ordinal), "配置诊断文本不得包含 bearer");
        Assert(handler.RequestCount == 5, "只应发出配对、轮询和只读请求，不应触发 AI 请求");
    }

    public static async Task TestSessionExpiryAndContextInvalidationAsync(ProcessTestWorkspace _)
    {
        var generation = 1L;
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id",
                pairing_code = "ABCD1234EFGH",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                instance_id = InstanceId.ToString("D"),
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                user_id = "user-1",
                username = "alice",
                is_active = true,
                permissions = new { ai_read = true, ai_write = true },
                instance_id = InstanceId.ToString("D"),
            }),
            _ => Json(HttpStatusCode.OK, AiResponse("https://api.example.test/v1", apiKeySet: false)),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/me");
                return Json(HttpStatusCode.Unauthorized, new { detail = "do not surface server body" });
            },
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id-2",
                pairing_code = "IJKL5678MNOP",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                instance_id = InstanceId.ToString("D"),
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                user_id = "user-1",
                username = "alice",
                is_active = true,
                permissions = new { ai_read = true, ai_write = true },
                instance_id = InstanceId.ToString("D"),
            }));
        var context = CreateContext(() => generation == 1);
        using var client = new PortableLauncherUserClient(context, handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        await client.GetAiConfigurationAsync();
        Assert(client.HasCachedUserData, "成功读取用户/AI 后应存在本次私有态");
        try
        {
            await client.GetCurrentUserAsync();
            throw new InvalidOperationException("401 必须拒绝读取");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.Unauthorized, "401 应映射为会话失效");
            Assert(!exception.ToString().Contains("do not surface", StringComparison.Ordinal), "错误不能透出后端响应正文");
        }

        Assert(!client.HasCachedUserData, "会话 401 必须即时清空用户摘要和 AI 缓存");
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        Assert(client.HasCachedUserData, "重新授权后允许重新读取");
        generation++;
        try
        {
            await client.GetCurrentUserAsync();
            throw new InvalidOperationException("无效 Host generation 必须拒绝后续请求");
        }
        catch (InvalidOperationException exception)
        {
            Assert(!exception.ToString().Contains(ControlToken, StringComparison.Ordinal), "Host 失效异常不能暴露控制凭据");
        }

        Assert(!client.HasCachedUserData, "Host generation 变化必须即时清空用户态缓存");
    }

    public static async Task TestInstanceMismatchAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id",
                pairing_code = "ABCD1234EFGH",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                instance_id = Guid.NewGuid().ToString("D"),
            }));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        try
        {
            await client.ExchangePairingOnceAsync();
            throw new InvalidOperationException("跨实例授权必须拒绝");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(!exception.ToString().Contains(AccessToken, StringComparison.Ordinal), "跨实例拒绝不能把响应 token 放进异常");
        }

        Assert(!client.HasCachedUserData, "跨实例响应不得缓存为会话");
    }

    public static async Task TestExpiredPairingTokenAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id",
                pairing_code = "ABCD1234EFGH",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(-1),
                instance_id = InstanceId.ToString("D"),
            }));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        try
        {
            await client.ExchangePairingOnceAsync();
            throw new InvalidOperationException("过期的会话 token 必须拒绝");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(!exception.ToString().Contains(AccessToken, StringComparison.Ordinal), "过期响应异常不得暴露短期 token");
        }

        Assert(!client.HasCachedUserData, "过期响应不能生成本地会话缓存");
    }

    public static Task TestHostContextEligibilityGuardsAsync(ProcessTestWorkspace workspace)
    {
        var identity = new OwnedProcessIdentity(
            1234,
            DateTimeOffset.UtcNow,
            Path.Combine(workspace.Root, "python.exe"),
            Path.Combine(workspace.Root, "instance"),
            InstanceId,
            Guid.NewGuid());
        var runtime = new PythonRuntimeRecord(
            1, InstanceId, "normal", "test", 58000,
            workspace.Root, identity.InstanceRoot, "Running", identity,
            Guid.NewGuid(), true, DateTimeOffset.UtcNow);
        var running = new OwnedProcessOperationResult(
            true, false, null,
            new OwnedProcessSnapshot(OwnedProcessState.Running, identity, null, DateTimeOffset.UtcNow, "Running"));
        var otherRun = identity with { RunId = Guid.NewGuid() };

        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureReady(
                new PythonReadinessResult(PythonReadinessState.NotReady, "not ready", 1), identity, identity, ControlToken),
            "未就绪服务必须拒绝建立用户上下文");
        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureReady(
                new PythonReadinessResult(PythonReadinessState.Ready, null, 1, SetupRequired: true), identity, identity, ControlToken),
            "首次设置期间必须拒绝建立用户上下文");
        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureReady(
                new PythonReadinessResult(PythonReadinessState.Ready, null, 1), identity, null, ControlToken),
            "未绑定受管进程身份必须拒绝建立用户上下文");

        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureReady(
                new PythonReadinessResult(PythonReadinessState.Ready, null, 1), otherRun, identity, ControlToken),
            "前一代进程的 Ready 结果不得授权新 run identity");

        var mismatchedRefresh = running with
        {
            Snapshot = running.Snapshot with { Identity = otherRun },
        };
        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureOwned(InstanceId, identity, mismatchedRefresh, identity, runtime),
            "刷新发现不同 run identity 时必须拒绝建立用户上下文");
        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureOwned(Guid.NewGuid(), identity, running, identity, runtime),
            "不同实例 id 必须拒绝建立用户上下文");
        AssertGuardRejects(
            () => LauncherUserContextGuard.EnsureControlCredentialMatches("wrong-sensitive-control", ControlToken),
            "DPAPI 控制凭据不匹配时必须拒绝建立上下文");
        return Task.CompletedTask;
    }

    public static async Task TestLogoutRevokesSessionAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id",
                pairing_code = "ABCD1234EFGH",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                instance_id = InstanceId.ToString("D"),
            }),
            request =>
            {
                AssertInternalRequest(request, "/internal/launcher/session/revoke");
                Assert(request.Method == HttpMethod.Post, "退出必须使用受控 revoke POST");
                Assert(!request.RequestUri!.AbsoluteUri.Contains(AccessToken, StringComparison.Ordinal), "短期 bearer 不得进入 URL");
                Assert(ReadBody(request).GetProperty("access_token").GetString() == AccessToken, "主动退出必须请求后端撤销会话");
                return Json(HttpStatusCode.OK, new { status = "revoked" });
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.LogoutAsync();
        Assert(!client.HasCachedUserData, "退出后不得保留用户缓存");
    }

    public static async Task TestOversizedPrivateResponseClearsCacheAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id",
                pairing_code = "ABCD1234EFGH",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                instance_id = InstanceId.ToString("D"),
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                user_id = "user-1",
                username = "alice",
                is_active = true,
                permissions = new { ai_read = true, ai_write = true },
                instance_id = InstanceId.ToString("D"),
            }),
            _ => Json(HttpStatusCode.OK, new { payload = new string('x', 70 * 1024) }));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        Assert(client.HasCachedUserData, "测试需先建有私有缓存");
        try
        {
            await client.GetAiConfigurationAsync();
            throw new InvalidOperationException("超限的私有响应必须被拒绝");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(!exception.ToString().Contains(new string('x', 32), StringComparison.Ordinal), "错误不得包含超限响应内容");
        }

        Assert(!client.HasCachedUserData, "格式异常或超限响应必须清除旧私有缓存");
    }

    public static async Task TestObsoleteExpiryCallbackCannotClearNewSessionAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("pair-1", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.OK, UserResponse()),
            _ => Json(HttpStatusCode.OK, PairingResponse("pair-2", "IJKL5678MNOP")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.OK, UserResponse()));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        var oldSessionGeneration = client.SessionGenerationForTests;
        var oldPairingGeneration = client.PairingGenerationForTests;

        await client.BeginPairingAsync();
        var replacementPairingGeneration = client.PairingGenerationForTests;
        Assert(replacementPairingGeneration != oldPairingGeneration, "新配对必须推进 pairing generation");
        Assert(!client.ExpirePairingIfCurrentForTests(oldPairingGeneration), "旧配对计时器回调不得清除新 pairing verifier");
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        var replacementSessionGeneration = client.SessionGenerationForTests;
        Assert(replacementSessionGeneration != oldSessionGeneration, "新授权必须推进 session generation");
        Assert(client.HasCachedUserData, "替换授权后需有新用户摘要缓存");
        Assert(!client.ExpireSessionIfCurrentForTests(oldSessionGeneration), "旧会话计时器回调不得清除新 session 与缓存");
        Assert(client.HasCachedUserData, "旧会话回调后必须保留新账号当前缓存");
    }

    public static Task TestLateUserSuccessAfterLogoutCannotPopulateCacheAsync(ProcessTestWorkspace _) =>
        TestLateUserResponseAfterSessionReplacementAsync(HttpStatusCode.OK);

    public static Task TestLateUserUnauthorizedAfterNewLoginCannotClearCacheAsync(ProcessTestWorkspace _) =>
        TestLateUserResponseAfterSessionReplacementAsync(HttpStatusCode.Unauthorized);

    private static async Task TestLateUserResponseAfterSessionReplacementAsync(HttpStatusCode oldResponseStatus)
    {
        var handler = new SessionRaceHandler(oldResponseStatus);
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var staleRequest = client.GetCurrentUserAsync();
        await handler.WaitForOldUserRequestAsync();

        await client.LogoutAsync();
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var current = await client.GetCurrentUserAsync();
        Assert(current.UserId == "user-2" && client.HasCachedUserData, "新会话应先建立自己的私有缓存");

        handler.CompleteOldUserResponse();
        try
        {
            await staleRequest;
            throw new InvalidOperationException("旧 session 的迟到用户响应必须丢弃");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.Unauthorized, "旧 session 结果应标注会话切换/过期");
        }

        Assert(client.HasCachedUserData, "旧 session 的迟到 200/401 不得写入或清除新 session 缓存");
    }

    public static async Task TestMaskedKeyAndCasConflictAsync(ProcessTestWorkspace workspace)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, new
            {
                pairing_id = "pairing-test-id",
                pairing_code = "ABCD1234EFGH",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                expires_in_seconds = 300,
            }),
            _ => Json(HttpStatusCode.OK, new
            {
                status = "exchanged",
                access_token = AccessToken,
                token_type = "Bearer",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
                instance_id = InstanceId.ToString("D"),
            }),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                return Json(HttpStatusCode.OK, AiResponse("https://user:secret@api.example.test/v1?token=secret#fragment", apiKeySet: true));
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Put, "CAS 更新必须是 PUT");
                var body = ReadBody(request);
                Assert(body.GetProperty("config_revision").GetInt32() == 9, "写入必须提交观察到的 revision");
                Assert(body.GetProperty("config_id").GetString() == "cfg-1", "写入必须绑定配置 id");
                Assert(body.GetProperty("OPENAI_MODEL_NAME").GetString() == "model-b", "只提交允许的字段");
                Assert(!body.TryGetProperty("OPENAI_API_KEY", out _), "空白 API key 不得覆盖保存的凭据");
                return Json(HttpStatusCode.Conflict, new { detail = "secret backend payload" });
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var config = await client.GetAiConfigurationAsync();
        Assert(config.BaseUrl is null && config.BaseUrlRedacted, "含 userinfo/query/fragment 的 Base URL 必须在客户端再次脱敏");
        try
        {
            await client.SaveAiConfigurationAsync(9, "cfg-1", baseUrl: null, modelName: "model-b", replacementApiKey: null);
            throw new InvalidOperationException("CAS 冲突必须拒绝静默覆盖");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.Conflict, "CAS 冲突应映射为 409");
            Assert(!exception.ToString().Contains("secret", StringComparison.Ordinal), "CAS 异常不得回显 key 或响应正文");
            Assert(handler.RequestCount == 4, "服务器 CAS 冲突后不得自动重试或读取回退");
        }
    }

    public static async Task TestAiConfigurationFirstSaveCreatesAndRefreshesAuthoritativeConfigAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("first-save-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Get, "首次保存前应读取权威空配置");
                return Json(HttpStatusCode.OK, new { config_id = "", config_revision = 0 });
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Put, "无变更保存应提交 PUT");
                var body = ReadBody(request);
                Assert(body.GetProperty("config_id").GetString() == "" && body.GetProperty("config_revision").GetInt32() == 0,
                    "无配置保存必须使用空配置 ID 和 revision 0");
                Assert(!body.TryGetProperty("OPENAI_BASE_URL", out var ignoredBaseUrl) &&
                       !body.TryGetProperty("OPENAI_MODEL_NAME", out var ignoredModelName),
                    "无变更保存不能伪造字段更新");
                return Json(HttpStatusCode.OK, new { config_id = "", config_revision = 0 });
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Get, "无变更保存后仍应刷新权威配置");
                return Json(HttpStatusCode.OK, new { config_id = "", config_revision = 0 });
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Put, "首次创建应提交 PUT");
                var body = ReadBody(request);
                Assert(body.GetProperty("config_id").GetString() == "" && body.GetProperty("config_revision").GetInt32() == 0,
                    "首次保存必须使用空配置 ID 和 revision 0");
                return Json(HttpStatusCode.OK, new { config_id = "cfg-created", config_revision = 1 });
            },
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Get, "创建后应重新读取权威配置");
                return Json(HttpStatusCode.OK,
                    AiResponse("https://api.example.test/v1", apiKeySet: false, configId: "cfg-created", configRevision: 1));
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var empty = await client.GetAiConfigurationAsync();
        Assert(empty.ConfigId == "" && empty.ConfigRevision == 0 && !empty.ApiKeySet && empty.BaseUrl is null && empty.ModelName is null,
            "应识别未创建的配置 sentinel，并将缺省摘要字段视为未设置");

        var noOp = await client.SaveAiConfigurationAsync(0, "", baseUrl: null, modelName: null, replacementApiKey: null);
        Assert(noOp.ConfigId == "" && noOp.ConfigRevision == 0,
            "后端 no-op 响应的空 ID/revision 0 必须接受并返回权威配置");

        var saved = await client.SaveAiConfigurationAsync(0, "", "https://api.example.test/v1", "model-b", null);
        Assert(saved.ConfigId == "cfg-created" && saved.ConfigRevision == 1 && saved.ModelName == "model-a",
            "首次创建应接受服务器生成的新身份/版本，并返回重新读取的权威值");
        Assert(handler.RequestCount == 7, "no-op 与首次创建都应各自刷新权威配置且不重试");
    }

    public static async Task TestAiConfigurationRejectsEmptyIdWithPositiveRevisionAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("invalid-save-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.SaveAiConfigurationAsync(1, "", baseUrl: null, modelName: "model-b", replacementApiKey: null);
            throw new InvalidOperationException("空配置 ID 搭配正 revision 必须在请求前拒绝");
        }
        catch (ArgumentException)
        {
            Assert(handler.RequestCount == 2, "非法空 ID/revision 组合不得发送保存请求");
        }
    }

    public static async Task TestAiConfigurationRejectsExistingIdentityMismatchAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("identity-mismatch-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai");
                Assert(request.Method == HttpMethod.Put, "已有配置更新应提交 PUT");
                var body = ReadBody(request);
                Assert(body.GetProperty("config_id").GetString() == "cfg-existing" &&
                       body.GetProperty("config_revision").GetInt32() == 7,
                    "已有配置请求必须带原身份及 revision");
                return Json(HttpStatusCode.OK, new { config_id = "cfg-other", config_revision = 8 });
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.SaveAiConfigurationAsync(7, "cfg-existing", baseUrl: null, modelName: "model-b", replacementApiKey: null);
            throw new InvalidOperationException("已有配置不能接受不同配置 ID 的保存响应");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode is null, "配置身份不一致应作为协议错误拒绝");
            Assert(handler.RequestCount == 3, "身份不一致响应后不得进行权威刷新或重试");
        }
    }

    public static async Task TestManualAiTestConfirmationAllowlistAndSafeResultAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("manual-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai/test");
                Assert(request.Method == HttpMethod.Post, "AI 手动测试必须使用 POST");
                var body = ReadBody(request);
                Assert(body.GetProperty("confirmed").GetBoolean(), "请求必须固定 confirmed=true");
                Assert(body.GetProperty("request_id").GetString() == "manual-test-123", "必须发送稳定 request_id");
                Assert(body.GetProperty("expected_config_id").GetString() == "cfg-1" &&
                       body.GetProperty("expected_config_revision").GetInt32() == 9,
                    "测试必须绑定用户确认时看到的配置身份和版本");
                Assert(body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "confirmed", "request_id", "expected_config_id", "expected_config_revision", "settings" }.Order(StringComparer.Ordinal)),
                    "测试请求顶层字段必须严格限制");
                var settings = body.GetProperty("settings");
                Assert(settings.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_MODEL_NAME" }.Order(StringComparer.Ordinal)),
                    "草稿设置只能包含 URL、模型和 key");
                Assert(settings.GetProperty("OPENAI_API_KEY").GetString() == "manual-secret-key", "草稿 key 仅可通过受保护请求体发送");
                Assert(!request.RequestUri!.AbsoluteUri.Contains("manual-secret-key", StringComparison.Ordinal), "API key 不得进入 URL");
                return Json(HttpStatusCode.OK, new
                {
                    success = false,
                    message = "manual-secret-key " + AccessToken + " https://provider.example/?token=private",
                    test = new
                    {
                        status = "unknown",
                        message = "manual-secret-key " + AccessToken,
                        latency_ms = 25,
                        checked_at = DateTimeOffset.UtcNow,
                        provider_detail = "ignored",
                    },
                    unexpected = "ignored",
                });
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.RunManualAiTestAsync(false, "manual-test-123", "", 0, null, null, null);
            throw new InvalidOperationException("没有用户明确确认就必须拒绝测试");
        }
        catch (InvalidOperationException exception)
        {
            Assert(exception.Message == "发送 AI 测试请求前必须取得用户明确确认。", "确认缺失应返回安全拒绝");
        }
        Assert(handler.RequestCount == 2, "未确认请求不能到达本机服务");

        var result = await client.RunManualAiTestAsync(
            true,
            "manual-test-123",
            "cfg-1",
            9,
            "https://provider.example/v1",
            "model-a",
            "manual-secret-key");
        Assert(!result.Success && result.Status == "unknown", "unknown 结果不能伪报成功");
        Assert(result.Message.Contains("结果未知", StringComparison.Ordinal), "unknown 必须提示不要盲目重试");
        Assert(!result.ToString().Contains("manual-secret-key", StringComparison.Ordinal) &&
               !result.ToString().Contains(AccessToken, StringComparison.Ordinal) &&
               !result.ToString().Contains("provider.example", StringComparison.Ordinal),
            "结果诊断文本不得包含密钥、会话或 URL");
        Assert(handler.RequestCount == 3, "一次手动测试只发送一个请求");
    }

    public static async Task TestManualAiTestCancellationIsUnknownAndNotRetriedAsync(ProcessTestWorkspace _)
    {
        var handler = new ManualTestCancellationHandler();
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        var cacheInvalidated = false;
        client.UserDataInvalidated += () => cacheInvalidated = true;
        using var cancellation = new CancellationTokenSource();
        var testTask = client.RunManualAiTestAsync(true, "manual-test-cancel", "", 0, null, null, null, cancellation.Token);
        await handler.WaitForTestRequestAsync();
        cancellation.Cancel();
        var result = await testTask;
        Assert(!result.Success && result.Status == "unknown", "取消只可报告结果未知");
        Assert(client.HasCachedUserData && !cacheInvalidated, "取消手动测试不得登出用户或清理其缓存");
        Assert(handler.RequestCount == 4, "取消后不得自动发送第二次请求");
    }

    public static async Task TestLateManualAiTestResultAfterUserSwitchIsDiscardedAsync(ProcessTestWorkspace workspace)
    {
        var handler = new LateManualTestHandler();
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var oldTest = client.RunManualAiTestAsync(true, "manual-test-late-result", "cfg-1", 9, null, null, null);
        await handler.WaitForOldTestRequestAsync();
        await client.LogoutAsync();
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        Assert(client.HasCachedUserData, "新会话应建立自己的用户缓存");
        handler.CompleteOldTestResponse();
        try
        {
            _ = await oldTest;
            throw new InvalidOperationException("用户切换后的迟到 AI 测试结果必须丢弃");
        }
        catch (LauncherUserClientException)
        {
        }
        Assert(client.HasCachedUserData, "旧 AI 测试不能污染或清除新用户缓存");
    }

    public static async Task TestAiHealthFreshnessAndCategoryStaySeparateAsync(ProcessTestWorkspace _)
    {
        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-12);
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("health-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai/health");
                Assert(request.Method == HttpMethod.Get, "健康快照只能读取");
                var body = AiHealthResponse("current", "timeout", observedAt, 300);
                body["provider_key"] = "must not expose";
                body["detail"] = "private URL must not expose";
                return Json(HttpStatusCode.OK, body);
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var health = await client.GetAiHealthAsync();
        Assert(health.Freshness == "current", "freshness 必须保留 current");
        Assert(health.Category == "timeout", "current 不能被误解成测试健康成功");
        Assert(health.ObservedAtUtc is not null && health.ObservedAtUtc.Value.Offset == TimeSpan.Zero, "观测时间必须解析为 UTC");
        Assert(health.ExpiresAtUtc is not null && health.ExpiresAtUtc.Value.Offset == TimeSpan.Zero, "过期时间必须解析为 UTC");
        Assert(health.RemainingTtlSeconds is >= 1 and <= 300, "TTL 必须来自有限的后端过期时间");
        Assert(health.LatencyMilliseconds == 20 && health.ConfigId == "cfg-health" && health.ConfigRevision == 12,
            "响应应保留 latency 与配置身份");
        Assert(health.Source == "postgres_user_config", "响应应保留健康快照来源");
        Assert(!health.ToString().Contains("must not expose", StringComparison.Ordinal), "健康 DTO 不应序列化未授权响应字段");
        Assert(handler.RequestCount == 3, "读取健康缓存只应产生一次本地请求");
    }

    public static async Task TestAiHealthUnknownAndUnconfiguredAreNotZeroAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("health-state-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.OK, NeverCheckedHealthResponse()),
            _ => Json(HttpStatusCode.OK, UnconfiguredHealthResponse()),
            _ => Json(HttpStatusCode.OK, AiHealthResponse("config_changed", "unknown", null, null)),
            _ => Json(HttpStatusCode.OK, AiHealthResponse("stale", "unavailable", DateTimeOffset.UtcNow.AddMinutes(-6), 300)));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();

        var neverChecked = await client.GetAiHealthAsync();
        Assert(neverChecked.Freshness == "never_checked" && neverChecked.Category == "unknown", "未检测不得冒充 0 或成功");
        Assert(neverChecked.LatencyMilliseconds is null && neverChecked.RemainingTtlSeconds is null, "未知时间/耗时必须保留 null");

        var unconfigured = await client.GetAiHealthAsync();
        Assert(unconfigured.Freshness == "unconfigured" && unconfigured.Category == "unconfigured", "未配置须独立表达");
        Assert(unconfigured.LatencyMilliseconds is null && unconfigured.ConfigId == "", "无数据字段不能转换为零耗时");

        var changed = await client.GetAiHealthAsync();
        Assert(changed.Freshness == "config_changed" && changed.Category == "unknown", "配置 revision/id 变化状态需保留");
        Assert(changed.RemainingTtlSeconds is null, "旧配置的缓存 TTL 不能提示可用时长");

        var stale = await client.GetAiHealthAsync();
        Assert(stale.Freshness == "stale" && stale.Category == "unavailable", "stale freshness 与失败 category 需独立保留");
        Assert(stale.RemainingTtlSeconds == 0, "过期缓存 TTL 应为零，不代表 latency 为零");
    }

    public static async Task TestAiHealthUnavailableDoesNotExpireSessionAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("health-503-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.ServiceUnavailable, new { detail = "private backend error" }),
            _ => Json(HttpStatusCode.OK, UserResponse()));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.GetAiHealthAsync();
            throw new InvalidOperationException("503 健康缓存不可用必须作为读取错误返回");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.ServiceUnavailable, "503 应保留安全状态码");
            Assert(!exception.ToString().Contains("private backend error", StringComparison.Ordinal), "异常不应回显后端内容");
        }

        var currentUser = await client.GetCurrentUserAsync();
        Assert(currentUser.UserId == "user-1", "健康缓存 503 不代表用户授权失效");
        Assert(handler.RequestCount == 4, "503 后只进行显式的用户读取，没有自动重试 health");
    }

    public static async Task TestManualAiTestServerErrorDoesNotExpireSessionAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("manual-500-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.InternalServerError, new { detail = "provider orchestrator error with private data" }),
            _ => Json(HttpStatusCode.OK, UserResponse()));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.RunManualAiTestAsync(true, "manual-test-http-500", "cfg-1", 9, null, null, null);
            throw new InvalidOperationException("HTTP 500 must be returned as a sanitized test failure");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.InternalServerError, "500 应作为手动测试结果错误保留");
            Assert(!exception.ToString().Contains("private data", StringComparison.Ordinal), "异常不能回显服务正文");
        }

        Assert((await client.GetCurrentUserAsync()).UserId == "user-1", "手动测试后端 500 不代表用户 session 失效");
        Assert(handler.RequestCount == 4, "500 后调用方的用户读取是唯一后续请求，未自动重试测试");
    }

    public static async Task TestManualAiTestConfigConflictDoesNotClearSessionOrRetryAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("manual-cas-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai/test");
                var body = ReadBody(request);
                Assert(body.GetProperty("expected_config_id").GetString() == "cfg-at-confirmation" &&
                       body.GetProperty("expected_config_revision").GetInt32() == 17,
                    "手测必须提交确认时捕获的 expected config CAS identity");
                Assert(body.GetProperty("settings").GetProperty("OPENAI_API_KEY").GetString() == "draft-key-never-repeat",
                    "草稿 key 只在这个单次请求中发送");
                return Json(HttpStatusCode.Conflict, new
                {
                    detail = "draft-key-never-repeat " + AccessToken + " https://private.example/?secret=1",
                });
            },
            _ => Json(HttpStatusCode.OK, UserResponse()));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.RunManualAiTestAsync(
                true, "manual-test-cas-conflict", "cfg-at-confirmation", 17,
                "https://provider.example/v1", "model-confirmed", "draft-key-never-repeat");
            throw new InvalidOperationException("后台配置在确认后变化时必须拒绝手动测试");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.Conflict, "CAS 冲突必须返回 HTTP 409 状态");
            Assert(exception.Message.Contains("配置已在确认后更改", StringComparison.Ordinal), "409 应给出固定安全提示");
            Assert(!exception.ToString().Contains("draft-key-never-repeat", StringComparison.Ordinal) &&
                   !exception.ToString().Contains(AccessToken, StringComparison.Ordinal) &&
                   !exception.ToString().Contains("private.example", StringComparison.Ordinal),
                "409 正文中的 key/token/URL 必须完全丢弃");
        }

        Assert((await client.GetCurrentUserAsync()).UserId == "user-1", "配置冲突不得清理仍有效的用户会话");
        Assert(handler.RequestCount == 4, "409 后显式用户读取之外不得自动重发 AI 测试");
    }

    public static async Task TestManualAiTestSupportsNoSavedConfigIdentityAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("manual-empty-config-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            request =>
            {
                AssertUserRequest(request, "/api/launcher/ai/test");
                var body = ReadBody(request);
                Assert(body.GetProperty("expected_config_id").GetString() == "", "无持久配置时 expected_config_id 必须为空字符串");
                Assert(body.GetProperty("expected_config_revision").GetInt32() == 0, "无持久配置时 expected revision 必须为 0");
                Assert(!body.GetProperty("settings").EnumerateObject().Any(), "无未保存草稿时 settings 保持空 allowlist 对象");
                return Json(HttpStatusCode.OK, ManualTestResponse("rejected", "AI 配置不完整。"));
            });
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var result = await client.RunManualAiTestAsync(true, "manual-test-no-saved-config", "", 0, null, null, null);
        Assert(result.Status == "rejected" && result.Message.Contains("配置不完整", StringComparison.Ordinal),
            "无配置测试应安全返回配置不完整");
        Assert(handler.RequestCount == 3, "无配置请求也只调用一次");
    }

    public static async Task TestManualAiTestRejectsEmptyConfigIdWithPositiveRevisionAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("manual-invalid-config-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.OK, UserResponse()));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        try
        {
            await client.RunManualAiTestAsync(true, "manual-test-invalid-config", "", 1, null, null, null);
            throw new InvalidOperationException("空配置 ID 携带正 revision 必须在本地拒绝");
        }
        catch (ArgumentException exception)
        {
            Assert(exception.Message == "AI 测试请求字段无效。", "无效配置 identity 应返回固定本地错误");
        }

        Assert(handler.RequestCount == 2, "无效配置 identity 必须在 HTTP 前拒绝");
        Assert((await client.GetCurrentUserAsync()).UserId == "user-1", "本地参数拒绝不应清理有效会话");
        Assert(handler.RequestCount == 3, "拒绝后只产生显式用户读取，不发送测试请求");
    }

    public static async Task TestManualAiTestFixedMessagesAreSafelyClassifiedAsync(ProcessTestWorkspace _)
    {
        var handler = new QueueHandler(
            _ => Json(HttpStatusCode.OK, PairingResponse("manual-message-pair", "ABCD1234EFGH")),
            _ => Json(HttpStatusCode.OK, ExchangedResponse()),
            _ => Json(HttpStatusCode.OK, ManualTestResponse("unknown", "请求超时，服务端是否处理或计费未知。")),
            _ => Json(HttpStatusCode.OK, ManualTestResponse("unknown", "网络请求结果未知。")),
            _ => Json(HttpStatusCode.OK, ManualTestResponse("rejected", "当前配置启用了 AI 代理；手动测试暂不支持代理，未发送请求。")),
            _ => Json(HttpStatusCode.OK, ManualTestResponse("rejected", "目标地址已更改，请为新目标重新输入 API Key。")),
            _ => Json(HttpStatusCode.OK, ManualTestResponse("unknown", "manual-secret-key https://private.example/?token=x " + AccessToken)));
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var timeout = await client.RunManualAiTestAsync(true, "manual-test-timeout", "cfg-1", 9, null, null, null);
        Assert(timeout.Status == "unknown" && timeout.Message.Contains("AI 请求超时", StringComparison.Ordinal),
            "精确后端超时文案应映射为脱敏 timeout 分类");

        var network = await client.RunManualAiTestAsync(true, "manual-test-network", "cfg-1", 9, null, null, null);
        Assert(network.Status == "unknown" && network.Message.Contains("网络请求结果未知", StringComparison.Ordinal),
            "精确后端网络文案应与 timeout 分开呈现");

        var rejected = await client.RunManualAiTestAsync(true, "manual-test-proxy", "cfg-1", 9, null, null, null);
        Assert(rejected.Status == "rejected" && rejected.Message.Contains("不支持手动测试", StringComparison.Ordinal),
            "精确后端 rejected 文案应映射安全的代理提示");

        var changedTarget = await client.RunManualAiTestAsync(true, "manual-test-new-target", "cfg-1", 9, null, null, null);
        Assert(changedTarget.Status == "rejected" && changedTarget.Message.Contains("目标已变化", StringComparison.Ordinal) &&
               changedTarget.Message.Contains("重新输入 API Key", StringComparison.Ordinal),
            "目标地址变更应提示重新绑定 API Key，同时不回显原目标");

        var malicious = await client.RunManualAiTestAsync(true, "manual-test-malicious", "cfg-1", 9, null, null, null);
        Assert(malicious.Status == "unknown" && malicious.Message.Contains("结果未知", StringComparison.Ordinal),
            "非已知固定文案应泛化为 unknown");
        Assert(!malicious.Message.Contains("manual-secret-key", StringComparison.Ordinal) &&
               !malicious.Message.Contains("private.example", StringComparison.Ordinal) &&
               !malicious.Message.Contains(AccessToken, StringComparison.Ordinal),
            "任何任意后端 message 都不得泄露 key、URL 或 bearer");
        Assert(handler.RequestCount == 7, "每次明确调用一发，无自动重试");
    }

    public static async Task TestLateAiHealthResponseAfterUserSwitchIsDiscardedAsync(ProcessTestWorkspace _)
    {
        var handler = new LateAiHealthHandler();
        using var client = new PortableLauncherUserClient(CreateContext(), handler);
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        var oldRead = client.GetAiHealthAsync();
        await handler.WaitForOldHealthRequestAsync();
        await client.LogoutAsync();
        await client.BeginPairingAsync();
        await client.ExchangePairingOnceAsync();
        await client.GetCurrentUserAsync();
        Assert(client.HasCachedUserData, "新会话应保留自己的当前用户状态");
        handler.CompleteOldHealthResponse();
        try
        {
            await oldRead;
            throw new InvalidOperationException("旧用户的迟到 health 响应必须丢弃");
        }
        catch (LauncherUserClientException exception)
        {
            Assert(exception.StatusCode == HttpStatusCode.Unauthorized, "迟到旧会话响应应报告会话切换");
        }

        Assert(client.HasCachedUserData, "迟到健康快照不能污染或清除新用户缓存");
    }

    private static PortableLauncherUserClientContext CreateContext(Func<bool>? valid = null) =>
        new(InstanceId, 58000, 1, ControlToken, _ => valid?.Invoke() ?? true);

    private static object AiResponse(
        string baseUrl,
        bool apiKeySet,
        string configId = "cfg-1",
        int configRevision = 9) => new
    {
        OPENAI_API_KEY_SET = apiKeySet,
        OPENAI_BASE_URL = baseUrl,
        OPENAI_MODEL_NAME = "model-a",
        config_revision = configRevision,
        config_id = configId,
        config_source = "user",
        effective_state = "active",
        IS_MULTI_USER_MODE = true,
        NEEDS_SETUP = false,
    };

    private static object PairingResponse(string pairingId, string pairingCode) => new
    {
        pairing_id = pairingId,
        pairing_code = pairingCode,
        expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
        expires_in_seconds = 300,
    };

    private static object ExchangedResponse() => new
    {
        status = "exchanged",
        access_token = AccessToken,
        token_type = "Bearer",
        expires_at = DateTimeOffset.UtcNow.AddHours(1),
        instance_id = InstanceId.ToString("D"),
    };

    private static object UserResponse(string userId = "user-1") => new
    {
        user_id = userId,
        username = userId,
        is_active = true,
        permissions = new { ai_read = true, ai_write = true },
        instance_id = InstanceId.ToString("D"),
    };

    private static Dictionary<string, object?> AiHealthResponse(
        string freshness, string category, DateTimeOffset? observedAt, int? ttlSeconds) => new()
    {
        ["status"] = freshness,
        ["observed_at"] = observedAt,
        ["expires_at"] = observedAt is null || ttlSeconds is null ? null : observedAt.Value.AddSeconds(ttlSeconds.Value),
        ["config_id"] = "cfg-health",
        ["config_revision"] = 12,
        ["source"] = "postgres_user_config",
        ["category"] = category,
        ["latency_ms"] = 20,
    };

    private static Dictionary<string, object?> NeverCheckedHealthResponse()
    {
        var body = AiHealthResponse("never_checked", "unknown", null, null);
        body["latency_ms"] = null;
        return body;
    }

    private static Dictionary<string, object?> UnconfiguredHealthResponse()
    {
        var body = AiHealthResponse("unconfigured", "unconfigured", null, null);
        body["config_id"] = "";
        body["config_revision"] = 0;
        body["latency_ms"] = null;
        return body;
    }

    private static object ManualTestResponse(string status, string message) => new
    {
        success = status == "success",
        message,
        test = new { status, message, latency_ms = 4, checked_at = DateTimeOffset.UtcNow },
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object body) => new(statusCode)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static JsonElement ReadBody(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.Clone();
    }

    private static void AssertInternalRequest(HttpRequestMessage request, string path)
    {
        Assert(request.RequestUri?.AbsolutePath == path, "内部接口路径必须匹配固定 allowlist");
        Assert(request.RequestUri?.Host == "127.0.0.1" && request.RequestUri.Port == 58000, "请求只能指向本机回环端口");
        Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == ControlToken,
            "内部请求必须通过私有上下文附带本实例 control bearer");
        Assert(request.Headers.GetValues("X-Goofish-Instance-Id").Single() == InstanceId.ToString("D"), "必须绑定实例 id");
        Assert(!request.Headers.Contains("Origin"), "控制请求禁止带 Origin");
    }

    private static void AssertUserRequest(HttpRequestMessage request, string path)
    {
        Assert(request.RequestUri?.AbsolutePath == path, "业务接口路径必须匹配固定 allowlist");
        Assert(request.RequestUri?.Host == "127.0.0.1" && request.RequestUri.Port == 58000, "业务请求只能指向本机回环端口");
        Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == AccessToken,
            "业务请求必须使用兑换后的短期 bearer");
        Assert(request.Headers.GetValues("X-Goofish-Instance-Id").Single() == InstanceId.ToString("D"), "业务请求必须绑定实例 id");
        Assert(request.Headers.GetValues("Origin").Single() == "http://127.0.0.1:58000", "业务请求必须使用精确 loopback Origin");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertGuardRejects(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException(message);
        }
        catch (PythonLifecycleException exception)
        {
            Assert(!exception.ToString().Contains(ControlToken, StringComparison.Ordinal), "上下文拒绝异常不能暴露控制 token");
            Assert(!exception.ToString().Contains("wrong-sensitive-control", StringComparison.Ordinal), "上下文拒绝异常不能暴露凭据内容");
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("fake handler 没有配置对应响应。");
            }

            RequestCount++;
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class SessionRaceHandler(HttpStatusCode oldUserResponseStatus) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _oldUserRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _oldUserResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestNumber;

        public Task WaitForOldUserRequestAsync() => _oldUserRequestStarted.Task;

        public void CompleteOldUserResponse()
        {
            object user;
            if (oldUserResponseStatus == HttpStatusCode.OK)
            {
                user = UserResponse(userId: "user-1");
            }
            else
            {
                user = new { detail = "stale response body is never shown" };
            }

            _oldUserResponse.TrySetResult(Json(oldUserResponseStatus, user));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _requestNumber) switch
            {
                1 => Json(HttpStatusCode.OK, PairingResponse("pair-1", "ABCD1234EFGH")),
                2 => Json(HttpStatusCode.OK, ExchangedResponse()),
                3 => await DelayOldUserRequestAsync(request, cancellationToken).ConfigureAwait(false),
                4 => Revoke(request),
                5 => Json(HttpStatusCode.OK, PairingResponse("pair-2", "IJKL5678MNOP")),
                6 => Json(HttpStatusCode.OK, ExchangedResponse()),
                7 => Json(HttpStatusCode.OK, UserResponse(userId: "user-2")),
                _ => throw new InvalidOperationException("session race fake handler 收到未预期请求。"),
            };
        }

        private async Task<HttpResponseMessage> DelayOldUserRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AssertUserRequest(request, "/api/launcher/me");
            _oldUserRequestStarted.TrySetResult();
            return await _oldUserResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpResponseMessage Revoke(HttpRequestMessage request)
        {
            AssertInternalRequest(request, "/internal/launcher/session/revoke");
            return Json(HttpStatusCode.OK, new { status = "revoked" });
        }
    }

    private sealed class ManualTestCancellationHandler : HttpMessageHandler
    {
        private int _requestNumber;
        private readonly TaskCompletionSource _testRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref _requestNumber);

        public Task WaitForTestRequestAsync() => _testRequestStarted.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _requestNumber) switch
            {
                1 => Json(HttpStatusCode.OK, PairingResponse("cancel-pair", "ABCD1234EFGH")),
                2 => Json(HttpStatusCode.OK, ExchangedResponse()),
                3 => Json(HttpStatusCode.OK, UserResponse()),
                4 => await WaitForCancellationAsync(request, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("manual cancellation fake received an unexpected request."),
            };
        }

        private async Task<HttpResponseMessage> WaitForCancellationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AssertUserRequest(request, "/api/launcher/ai/test");
            _testRequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Json(HttpStatusCode.OK, new { });
        }
    }

    private sealed class LateManualTestHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _oldTestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _oldTestResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestNumber;

        public Task WaitForOldTestRequestAsync() => _oldTestStarted.Task;

        public void CompleteOldTestResponse() => _oldTestResponse.TrySetResult(Json(HttpStatusCode.OK, new
        {
            success = true,
            message = "连接成功",
            test = new { status = "success", message = "连接成功", latency_ms = 3, checked_at = DateTimeOffset.UtcNow },
        }));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _requestNumber) switch
            {
                1 => Json(HttpStatusCode.OK, PairingResponse("late-pair-1", "ABCD1234EFGH")),
                2 => Json(HttpStatusCode.OK, ExchangedResponse()),
                3 => await DelayOldTestAsync(request, cancellationToken).ConfigureAwait(false),
                4 => Revoke(request),
                5 => Json(HttpStatusCode.OK, PairingResponse("late-pair-2", "IJKL5678MNOP")),
                6 => Json(HttpStatusCode.OK, ExchangedResponse()),
                7 => Json(HttpStatusCode.OK, UserResponse("user-2")),
                _ => throw new InvalidOperationException("manual late-result fake received an unexpected request."),
            };
        }

        private async Task<HttpResponseMessage> DelayOldTestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AssertUserRequest(request, "/api/launcher/ai/test");
            _oldTestStarted.TrySetResult();
            return await _oldTestResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpResponseMessage Revoke(HttpRequestMessage request)
        {
            AssertInternalRequest(request, "/internal/launcher/session/revoke");
            return Json(HttpStatusCode.OK, new { status = "revoked" });
        }
    }

    private sealed class LateAiHealthHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _oldHealthStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _oldHealthResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestNumber;

        public Task WaitForOldHealthRequestAsync() => _oldHealthStarted.Task;

        public void CompleteOldHealthResponse() => _oldHealthResponse.TrySetResult(Json(HttpStatusCode.OK,
            AiHealthResponse("current", "healthy", DateTimeOffset.UtcNow.AddSeconds(-2), 300)));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _requestNumber) switch
            {
                1 => Json(HttpStatusCode.OK, PairingResponse("health-late-1", "ABCD1234EFGH")),
                2 => Json(HttpStatusCode.OK, ExchangedResponse()),
                3 => await DelayOldHealthAsync(request, cancellationToken).ConfigureAwait(false),
                4 => Revoke(request),
                5 => Json(HttpStatusCode.OK, PairingResponse("health-late-2", "IJKL5678MNOP")),
                6 => Json(HttpStatusCode.OK, ExchangedResponse()),
                7 => Json(HttpStatusCode.OK, UserResponse("user-2")),
                _ => throw new InvalidOperationException("late health fake received an unexpected request."),
            };
        }

        private async Task<HttpResponseMessage> DelayOldHealthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AssertUserRequest(request, "/api/launcher/ai/health");
            _oldHealthStarted.TrySetResult();
            return await _oldHealthResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpResponseMessage Revoke(HttpRequestMessage request)
        {
            AssertInternalRequest(request, "/internal/launcher/session/revoke");
            return Json(HttpStatusCode.OK, new { status = "revoked" });
        }
    }
}
