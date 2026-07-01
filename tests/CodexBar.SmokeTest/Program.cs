using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using CodexBar.Core;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Providers.Codex;

var metadataOnly = args.Contains("--metadata", StringComparer.OrdinalIgnoreCase);
var resumeCheck = args.Contains("--resume-check", StringComparer.OrdinalIgnoreCase);
var reconnectCheck = args.Contains("--reconnect-check", StringComparer.OrdinalIgnoreCase);
var activeReconnectCheck = args.Contains("--active-reconnect-check", StringComparer.OrdinalIgnoreCase);
var crashHost = args.Contains("--crash-host", StringComparer.OrdinalIgnoreCase);
var lifecycleCheck = args.Contains("--lifecycle-check", StringComparer.OrdinalIgnoreCase);
if (crashHost)
{
    return await RunCrashHostAsync(args);
}
if (lifecycleCheck)
{
    return await RunLifecycleCheckAsync();
}
if (!metadataOnly && !resumeCheck && !reconnectCheck && !activeReconnectCheck &&
    !args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    var reliability = RunProviderReliabilityTests();
    if (reliability != 0)
    {
        return reliability;
    }

    return await RunDeterministicTestsAsync();
}

var workspace = args.SkipWhile(arg => !arg.Equals("--workspace", StringComparison.OrdinalIgnoreCase))
    .Skip(1)
    .FirstOrDefault() ?? Environment.CurrentDirectory;

var response = new StringBuilder();
var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var provider = new CodexProvider();

provider.EventReceived += (_, providerEvent) =>
{
    switch (providerEvent)
    {
        case ProviderTextDeltaEvent delta:
            response.Append(delta.Delta);
            break;
        case ProviderCompletedEvent done:
            completed.TrySetResult(done.Status);
            break;
        case ProviderErrorEvent error when !error.WillRetry:
            completed.TrySetException(new InvalidOperationException(error.Message));
            break;
        case ProviderApprovalEvent approval:
            _ = approval.RespondAsync(ProviderApprovalDecision.Decline);
            break;
        case ProviderUserInputEvent input:
            _ = input.RespondAsync(null);
            break;
    }
};

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
await provider.InitializeAsync(timeout.Token);

if (reconnectCheck || activeReconnectCheck)
{
    var controller = new ConversationController(provider);
    await controller.InitializeAsync(timeout.Token);
    var threadsBefore = await controller.ListThreadsAsync(10, timeout.Token);
    TaskCompletionSource<string>? recovered = null;
    if (activeReconnectCheck)
    {
        recovered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.EventReceived += (_, providerEvent) =>
        {
            if (providerEvent is ProviderCompletedEvent completedEvent)
            {
                recovered.TrySetResult(completedEvent.Status);
            }
            else if (providerEvent is ProviderErrorEvent { WillRetry: false } errorEvent)
            {
                recovered.TrySetException(new InvalidOperationException(errorEvent.Message));
            }
        };
        await controller.OpenWorkspaceAsync(workspace, null, "workspace-write", "on-request", timeout.Token);
        await controller.SendAsync(
            "Tuliskan daftar bernomor berisi 200 ide nama aplikasi. Jangan gunakan tools.",
            cancellationToken: timeout.Token);
        Assert(controller.IsBusy, "Controller harus busy sebelum app-server diputus.");
    }

    var child = (Process?)typeof(CodexProvider).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(provider) ?? throw new InvalidOperationException("Proses app-server tidak ditemukan.");
    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync(timeout.Token);
    var disconnectDeadline = DateTimeOffset.Now.AddSeconds(5);
    while (provider.IsInitialized && DateTimeOffset.Now < disconnectDeadline)
    {
        await Task.Delay(50, timeout.Token);
    }

    Assert(!provider.IsInitialized, "Provider harus mendeteksi app-server yang dihentikan.");
    if (activeReconnectCheck)
    {
        var interruptedStatus = await recovered!.Task.WaitAsync(timeout.Token);
        Assert(interruptedStatus == "interrupted" && provider.IsInitialized && !controller.IsBusy,
            "Turn aktif yang terputus harus reconnect dan berakhir sebagai interrupted.");
    }

    var threadsAfter = await controller.ListThreadsAsync(10, timeout.Token);
    Assert(provider.IsInitialized, "Controller harus menginisialisasi ulang provider setelah disconnect.");
    Console.WriteLine(activeReconnectCheck
        ? $"PASS active-reconnect status=interrupted threadsBefore={threadsBefore.Count} threadsAfter={threadsAfter.Count}"
        : $"PASS reconnect threadsBefore={threadsBefore.Count} threadsAfter={threadsAfter.Count}");
    return 0;
}

if (metadataOnly || resumeCheck)
{
    var account = await provider.GetAccountAsync(timeout.Token);
    var threads = await provider.ListThreadsAsync(10, timeout.Token);
    var models = await provider.ListModelsAsync(timeout.Token);
    var messageCount = threads.Count > 0
        ? (await provider.ReadThreadAsync(threads[0].Id, timeout.Token)).Count
        : 0;
    var defaultModel = models.FirstOrDefault(model => model.IsDefault)?.Model ?? "none";
    if (resumeCheck)
    {
        var resumable = threads.FirstOrDefault(thread => Directory.Exists(thread.WorkspacePath))
            ?? throw new InvalidOperationException("Tidak ada thread dengan workspace lokal yang dapat diuji.");
        var resumedId = await provider.StartOrResumeThreadAsync(
            resumable.WorkspacePath,
            resumable.Id,
            "workspace-write",
            "on-request",
            timeout.Token);
        Console.WriteLine($"PASS resumedThread={resumedId} workspace={resumable.WorkspacePath}");
        return 0;
    }

    Console.WriteLine($"PASS authenticated={account.IsAuthenticated} method={account.AuthenticationMethod} models={models.Count} defaultModel={defaultModel} threads={threads.Count} firstThreadMessages={messageCount}");
    return 0;
}

var threadId = await provider.StartOrResumeThreadAsync(workspace, null, "workspace-write", "on-request", timeout.Token);
await provider.SendPromptAsync(
    threadId,
    "Reply with exactly CODEXBAR_OK and do not use tools.",
    cancellationToken: timeout.Token);

var status = await completed.Task.WaitAsync(timeout.Token);
var text = response.ToString();

if (status != "completed" || !text.Contains("CODEXBAR_OK", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Unexpected status/response. Status={status}; Response={text}");
    return 1;
}

Console.WriteLine($"PASS thread={threadId} status={status} response={text.Trim()}");
return 0;

static async Task<int> RunDeterministicTestsAsync()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"CodexBar-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRoot);

    try
    {
        var provider = new FakeProvider();
        var controller = new ConversationController(provider);
        var forwardedEvents = 0;
        controller.EventReceived += (_, _) => forwardedEvents++;

        await controller.InitializeAsync();
        await controller.InitializeAsync();
        provider.Emit(new ProviderStatusEvent("test"));
        Assert(forwardedEvents == 1, "Initialize berulang tidak boleh menggandakan event subscription.");

        await controller.OpenWorkspaceAsync(temporaryRoot, null, "workspace-write", "on-request");
        var failedWorkspace = Path.Combine(temporaryRoot, "failed-workspace");
        Directory.CreateDirectory(failedWorkspace);
        provider.FailStart = true;
        await AssertThrowsAsync<InvalidOperationException>(
            () => controller.OpenWorkspaceAsync(failedWorkspace, null, "workspace-write", "on-request"));
        Assert(controller.WorkspacePath == Path.GetFullPath(temporaryRoot) && controller.ThreadId == "thread-test",
            "Gagal membuka workspace tidak boleh merusak state aktif.");
        provider.FailStart = false;

        provider.FailSend = true;
        await AssertThrowsAsync<InvalidOperationException>(() => controller.SendAsync("gagal"));
        Assert(!controller.IsBusy, "Send yang gagal harus mereset status busy.");

        provider.FailSend = false;
        await controller.SendAsync("jalan");
        Assert(controller.IsBusy, "Turn aktif harus menandai controller busy.");
        await AssertThrowsAsync<InvalidOperationException>(
            () => controller.OpenWorkspaceAsync(temporaryRoot, null, "workspace-write", "on-request"));
        provider.Emit(new ProviderCompletedEvent("completed"));
        Assert(!controller.IsBusy, "Event selesai harus mereset status busy.");

        var reconnectCompleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.EventReceived += (_, providerEvent) =>
        {
            if (providerEvent is ProviderCompletedEvent result)
            {
                reconnectCompleted.TrySetResult(result.Status);
            }
        };
        await controller.SendAsync("turn yang akan terputus");
        var activeReconnectInitializeCount = provider.InitializeCount;
        provider.DisconnectWithError();
        var reconnectStatus = await reconnectCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(reconnectStatus == "interrupted" && !controller.IsBusy && provider.IsInitialized &&
               provider.InitializeCount == activeReconnectInitializeCount + 1,
            "Disconnect saat turn aktif harus reconnect otomatis dan mengakhiri busy state secara jelas.");

        var initializeCountBeforeReconnect = provider.InitializeCount;
        var startCountBeforeReconnect = provider.StartCount;
        provider.Disconnect();
        await controller.SendAsync("lanjut setelah disconnect");
        Assert(provider.InitializeCount == initializeCountBeforeReconnect + 1 &&
               provider.StartCount == startCountBeforeReconnect + 1,
            "Send setelah disconnect harus menginisialisasi ulang provider dan melanjutkan thread.");
        provider.Emit(new ProviderCompletedEvent("completed"));

        var settingsRoot = Path.Combine(temporaryRoot, "settings");
        Directory.CreateDirectory(settingsRoot);
        var store = new JsonSettingsStore(settingsRoot);
        await File.WriteAllTextAsync(store.SettingsPath, "{ bukan json");
        var recovered = await store.LoadAsync();
        Assert(recovered.SchemaVersion == 5, "Settings rusak harus dipulihkan ke schema terbaru.");
        Assert(Directory.GetFiles(settingsRoot, "settings.json.invalid-*").Length == 1,
            "Settings rusak harus dibackup satu kali.");

        await File.WriteAllTextAsync(store.SettingsPath,
            """
            {
              "schemaVersion": 1,
              "provider": "lain",
              "workspacePath": null,
              "appearance": { "theme": "asing", "inputFontSize": 999 },
              "window": { "width": 9999, "maxExpandedHeight": -1, "position": "asing" },
              "threadIds": null
            }
            """);
        var normalized = await store.LoadAsync();
        Assert(normalized.Provider == "codex" && normalized.WorkspacePath == string.Empty,
            "Provider dan workspace harus dinormalisasi.");
        Assert(normalized.Appearance.Theme == "system" && normalized.Appearance.InputFontSize == 24,
            "Appearance di luar batas harus dinormalisasi.");
        Assert(normalized.Window.Width == 1200 && normalized.Window.MaxExpandedHeight == 180,
            "Ukuran window harus dibatasi.");
        await store.SaveAsync(normalized);
        Assert(Directory.GetFiles(settingsRoot, "*.tmp").Length == 0,
            "Save settings tidak boleh meninggalkan file temporary.");

        await using var protocolProvider = new CodexProvider();
        ProviderEvent? protocolEvent = null;
        protocolProvider.EventReceived += (_, value) => protocolEvent = value;

        InvokeServerRequest(protocolProvider, "item/tool/requestUserInput",
            """
            {
              "threadId": "thread-test",
              "turnId": "turn-test",
              "itemId": "item-test",
              "autoResolutionMs": 60000,
              "questions": [{
                "id": "strategy",
                "header": "Strategi",
                "question": "Pilih strategi",
                "isOther": true,
                "options": [
                  { "label": "Aman", "description": "Perubahan kecil" },
                  { "label": "Cepat", "description": "Perubahan luas" }
                ]
              }]
            }
            """);
        var userInput = protocolEvent as ProviderUserInputEvent;
        Assert(userInput is not null && userInput.Questions.Count == 1,
            "request_user_input harus dipetakan menjadi event input provider.");
        Assert(userInput!.AutoResolutionMs == 60000 && userInput.Questions[0].AllowsOther &&
               userInput.Questions[0].Options.Count == 2,
            "Metadata pilihan dan auto-resolution request_user_input harus dipertahankan.");
        var userInputResponse = await CaptureServerResponseAsync(protocolProvider, () => userInput.RespondAsync(
            new Dictionary<string, IReadOnlyList<string>> { ["strategy"] = ["Aman"] }));
        using (var responseDocument = JsonDocument.Parse(userInputResponse))
        {
            var answer = responseDocument.RootElement.GetProperty("result").GetProperty("answers")
                .GetProperty("strategy").GetProperty("answers")[0].GetString();
            Assert(answer == "Aman", "Jawaban request_user_input harus dikirim dalam bentuk protocol yang benar.");
        }

        protocolEvent = null;
        InvokeServerRequest(protocolProvider, "mcpServer/elicitation/request",
            """
            {
              "serverName": "test-server",
              "threadId": "thread-test",
              "turnId": "turn-test",
              "mode": "form",
              "message": "Isi konfigurasi",
              "requestedSchema": {
                "type": "object",
                "required": ["count"],
                "properties": {
                  "count": { "type": "integer", "title": "Jumlah" },
                  "enabled": { "type": "boolean", "title": "Aktif" },
                  "tags": {
                    "type": "array",
                    "title": "Tag",
                    "items": { "type": "string", "enum": ["one", "two"] }
                  }
                }
              }
            }
            """);
        var mcpInput = protocolEvent as ProviderUserInputEvent;
        Assert(mcpInput is not null && mcpInput.Questions.Count == 3,
            "MCP form harus dipetakan menjadi pertanyaan terstruktur.");
        Assert(mcpInput!.Questions.Single(question => question.Id == "count").ValueType == "integer" &&
               mcpInput.Questions.Single(question => question.Id == "count").IsRequired,
            "Tipe dan required MCP field harus dipertahankan.");
        Assert(mcpInput.Questions.Single(question => question.Id == "tags").AllowsMultiple,
            "MCP array enum harus menjadi pilihan jamak.");
        var mcpResponse = await CaptureServerResponseAsync(protocolProvider, () => mcpInput.RespondAsync(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["count"] = ["3"],
                ["enabled"] = ["true"],
                ["tags"] = ["one", "two"]
            }));
        using (var responseDocument = JsonDocument.Parse(mcpResponse))
        {
            var result = responseDocument.RootElement.GetProperty("result");
            var content = result.GetProperty("content");
            Assert(result.GetProperty("action").GetString() == "accept" &&
                   content.GetProperty("count").GetInt64() == 3 &&
                   content.GetProperty("enabled").GetBoolean() &&
                   content.GetProperty("tags").GetArrayLength() == 2,
                "Jawaban MCP harus mempertahankan tipe number, boolean, dan array.");
        }

        protocolEvent = null;
        InvokeServerRequest(protocolProvider, "item/commandExecution/requestApproval",
            """
            {
              "threadId": "thread-test",
              "turnId": "turn-test",
              "command": "dotnet test",
              "cwd": "C:\\workspace",
              "reason": "Verifikasi perubahan"
            }
            """);
        var commandApproval = protocolEvent as ProviderApprovalEvent;
        Assert(commandApproval is not null && commandApproval.Summary.Contains("dotnet test"),
            "Approval perintah harus memuat command yang akan dijalankan.");
        var commandResponse = await CaptureServerResponseAsync(protocolProvider, () =>
            commandApproval!.RespondAsync(ProviderApprovalDecision.AcceptForSession));
        using (var responseDocument = JsonDocument.Parse(commandResponse))
        {
            Assert(responseDocument.RootElement.GetProperty("result").GetProperty("decision").GetString() ==
                   "acceptForSession",
                "Approval sesi harus dipertahankan pada response Codex.");
        }

        protocolEvent = null;
        InvokeServerRequest(protocolProvider, "item/permissions/requestApproval",
            """
            {
              "threadId": "thread-test",
              "turnId": "turn-test",
              "reason": "Akses jaringan untuk dependency",
              "permissions": { "network": { "enabled": true } }
            }
            """);
        var permissionApproval = protocolEvent as ProviderApprovalEvent;
        Assert(permissionApproval is not null, "Permission request harus menjadi approval event.");
        var permissionResponse = await CaptureServerResponseAsync(protocolProvider, () =>
            permissionApproval!.RespondAsync(ProviderApprovalDecision.AcceptForSession));
        using (var responseDocument = JsonDocument.Parse(permissionResponse))
        {
            var result = responseDocument.RootElement.GetProperty("result");
            Assert(result.GetProperty("scope").GetString() == "session" &&
                   result.GetProperty("permissions").GetProperty("network").GetProperty("enabled").GetBoolean(),
                "Permission approval sesi harus membawa scope dan permission yang diminta.");
        }

        protocolEvent = null;
        InvokeNotification(protocolProvider, "thread/status/changed",
            """
            {
              "threadId": "thread-background",
              "status": {
                "type": "active",
                "activeFlags": ["waitingOnApproval"]
              }
            }
            """);
        var threadStatus = protocolEvent as ProviderThreadStatusEvent;
        Assert(threadStatus is not null && threadStatus.ThreadId == "thread-background" &&
               threadStatus.Status == "active" && threadStatus.ActiveFlags.Contains("waitingOnApproval"),
            "Status thread dan attention flag harus diteruskan ke UI.");

        Console.WriteLine("PASS deterministic controller/settings/protocol tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

static void InvokeServerRequest(CodexProvider provider, string method, string parametersJson)
{
    var handler = typeof(CodexProvider).GetMethod(
        "HandleServerRequest",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Handler server request tidak ditemukan.");
    using var idDocument = JsonDocument.Parse("101");
    using var paramsDocument = JsonDocument.Parse(parametersJson);
    handler.Invoke(provider, [idDocument.RootElement.Clone(), method, paramsDocument.RootElement.Clone()]);
}

static void InvokeNotification(CodexProvider provider, string method, string parametersJson)
{
    var handler = typeof(CodexProvider).GetMethod(
        "HandleNotification",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Handler notification tidak ditemukan.");
    using var paramsDocument = JsonDocument.Parse(parametersJson);
    handler.Invoke(provider, [method, paramsDocument.RootElement.Clone()]);
}

static async Task<string> CaptureServerResponseAsync(CodexProvider provider, Func<Task> respond)
{
    using var stream = new MemoryStream();
    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
    {
        AutoFlush = true
    };
    SetPrivateField(provider, "_process", Process.GetCurrentProcess());
    SetPrivateField(provider, "_writer", writer);
    try
    {
        await respond();
    }
    finally
    {
        SetPrivateField(provider, "_writer", null);
        SetPrivateField(provider, "_process", null);
    }

    stream.Position = 0;
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return await reader.ReadToEndAsync();
}

static void SetPrivateField(object target, string name, object? value)
{
    var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Field {name} tidak ditemukan.");
    field.SetValue(target, value);
}

static int RunProviderReliabilityTests()
{
    try
    {
        // Sandbox/approval normalization must always fall back to a safe value.
        Assert(CodexProvider.NormalizeSandboxMode("read-only") == "read-only",
            "Sandbox read-only harus dipertahankan.");
        Assert(CodexProvider.NormalizeSandboxMode("DANGER-FULL-ACCESS") == "danger-full-access",
            "Sandbox harus case-insensitive.");
        Assert(CodexProvider.NormalizeSandboxMode("ngawur") == "workspace-write",
            "Sandbox tak dikenal harus jatuh ke workspace-write.");
        Assert(CodexProvider.NormalizeSandboxMode("") == "workspace-write",
            "Sandbox kosong harus jatuh ke workspace-write.");

        Assert(CodexProvider.NormalizeApprovalPolicy("never") == "never",
            "Approval never harus dipertahankan.");
        Assert(CodexProvider.NormalizeApprovalPolicy("On-Failure") == "on-failure",
            "Approval harus case-insensitive.");
        Assert(CodexProvider.NormalizeApprovalPolicy("untrusted") == "untrusted",
            "Approval untrusted harus dipertahankan.");
        Assert(CodexProvider.NormalizeApprovalPolicy("ngawur") == "on-request",
            "Approval tak dikenal harus jatuh ke on-request.");

        // Missing-thread vs not-materialized classification drives resume recovery.
        Assert(CodexProvider.IsMissingThread("Thread not found"),
            "'not found' harus terdeteksi sebagai thread hilang.");
        Assert(CodexProvider.IsMissingThread("no rollout found for thread id abc"),
            "'rollout' harus terdeteksi sebagai thread hilang.");
        Assert(CodexProvider.IsMissingThread("unknown thread xyz"),
            "'unknown thread' harus terdeteksi sebagai thread hilang.");
        Assert(!CodexProvider.IsMissingThread("Koneksi terputus"),
            "Pesan lain tidak boleh dianggap thread hilang.");

        Assert(CodexProvider.IsThreadNotMaterialized("thread abc is not materialized yet"),
            "'not materialized' harus terdeteksi.");
        Assert(CodexProvider.IsThreadNotMaterialized("includeTurns is unavailable before first user message"),
            "'includeTurns is unavailable' harus terdeteksi.");
        Assert(!CodexProvider.IsThreadNotMaterialized("thread not found"),
            "Thread hilang bukan not-materialized.");

        // Preview/title formatting: first non-empty line, trimmed, elided beyond 72 chars.
        Assert(CodexProvider.FirstDisplayLine("\n\n  Halo dunia  \nbaris kedua") == "Halo dunia",
            "FirstDisplayLine harus ambil baris non-kosong pertama yang di-trim.");
        var truncated = CodexProvider.FirstDisplayLine(new string('a', 100));
        Assert(truncated.Length == 72 && truncated.EndsWith("...", StringComparison.Ordinal),
            "Baris panjang harus dipotong ke 72 karakter dengan elipsis.");
        var exact = new string('b', 72);
        Assert(CodexProvider.FirstDisplayLine(exact) == exact,
            "Baris tepat 72 karakter tidak boleh dipotong.");

        // Thread status parsing (nested object, flat, and missing).
        var nested = JsonDocument.Parse("""{"status":{"type":"running","activeFlags":["tools"]}}""").RootElement;
        var (nestedType, nestedFlags) = CodexProvider.ParseThreadStatus(nested);
        Assert(nestedType == "running" && nestedFlags.Count == 1 && nestedFlags[0] == "tools",
            "ParseThreadStatus harus membaca objek status + activeFlags.");
        var flat = JsonDocument.Parse("""{"type":"idle"}""").RootElement;
        var (flatType, flatFlags) = CodexProvider.ParseThreadStatus(flat);
        Assert(flatType == "idle" && flatFlags.Count == 0,
            "ParseThreadStatus harus menangani status flat tanpa flags.");
        Assert(CodexProvider.ParseThreadStatus(JsonDocument.Parse("{}").RootElement).Status == "unknown",
            "ParseThreadStatus tanpa type harus 'unknown'.");

        // Thread summary parsing: skip entries without id/cwd, derive title from preview.
        var summaries = CodexProvider.ParseThreadSummaries(JsonDocument.Parse("""
            {
              "data": [
                { "id": "t1", "cwd": "C:/ws", "preview": "Judul dari preview\nbaris lain", "updatedAt": 1700000000 },
                { "id": "", "cwd": "C:/ws" },
                { "cwd": "C:/ws", "preview": "tanpa id" }
              ]
            }
            """).RootElement);
        Assert(summaries.Count == 1, "Entri tanpa id/cwd harus dilewati.");
        Assert(summaries[0].Id == "t1" && summaries[0].Title == "Judul dari preview",
            "Judul harus diambil dari baris pertama preview.");
        Assert(summaries[0].WorkspacePath == "C:/ws", "Workspace path harus dipertahankan.");
        Assert(CodexProvider.ParseThreadSummaries(JsonDocument.Parse("""{"other":true}""").RootElement).Count == 0,
            "Result tanpa array 'data' harus menghasilkan list kosong.");

        Console.WriteLine("PASS provider reliability helper tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Exception {typeof(TException).Name} tidak terjadi.");
}

static async Task<int> RunCrashHostAsync(string[] arguments)
{
    var pidFile = arguments.SkipWhile(arg => !arg.Equals("--pid-file", StringComparison.OrdinalIgnoreCase))
        .Skip(1).FirstOrDefault() ?? throw new ArgumentException("--pid-file wajib diisi.");
    await using var provider = new CodexProvider();
    await provider.InitializeAsync();
    var child = (Process?)typeof(CodexProvider).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(provider) ?? throw new InvalidOperationException("Child Codex tidak ditemukan.");
    await File.WriteAllTextAsync(pidFile, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Environment.FailFast("CodexBar lifecycle crash smoke test");
    return 99;
}

static async Task<int> RunLifecycleCheckAsync()
{
    var pidFile = Path.Combine(Path.GetTempPath(), $"codexbar-child-{Guid.NewGuid():N}.pid");
    try
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Path smoke-test executable tidak ditemukan.");
        using var host = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"--crash-host --pid-file \"{pidFile}\""
        }) ?? throw new InvalidOperationException("Crash host gagal dimulai.");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!File.Exists(pidFile) && !host.HasExited && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert(File.Exists(pidFile), "Crash host tidak menulis PID child Codex.");
        var childPid = int.Parse(await File.ReadAllTextAsync(pidFile),
            System.Globalization.CultureInfo.InvariantCulture);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var childGoneDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (IsProcessRunning(childPid) && DateTimeOffset.UtcNow < childGoneDeadline)
        {
            await Task.Delay(100);
        }
        Assert(!IsProcessRunning(childPid), $"Child codex.exe {childPid} menjadi orphan setelah crash host.");
        Console.WriteLine($"PASS lifecycle crashHost={host.Id} child={childPid} orphan=false");
        return 0;
    }
    finally
    {
        if (File.Exists(pidFile))
        {
            File.Delete(pidFile);
        }
    }
}

static bool IsProcessRunning(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

sealed class FakeProvider : IAgentProvider
{
    public string Id => "fake";
    public string DisplayName => "Fake";
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, true);
    public bool IsInitialized { get; private set; }
    public bool FailStart { get; set; }
    public bool FailSend { get; set; }
    public int InitializeCount { get; private set; }
    public int StartCount { get; private set; }

    public event EventHandler<ProviderEvent>? EventReceived;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsInitialized = true;
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task<string> StartOrResumeThreadAsync(
        string workspacePath,
        string? threadId,
        string sandboxMode,
        string approvalPolicy,
        CancellationToken cancellationToken = default) =>
        CountStart(FailStart
            ? Task.FromException<string>(new InvalidOperationException("start gagal"))
            : Task.FromResult(threadId ?? "thread-test"));

    public Task<ProviderAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderAccountInfo(true, "test", null, null, false));

    public Task<IReadOnlyList<ProviderThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadSummary>>([]);

    public Task<IReadOnlyList<ProviderThreadSummary>> SearchThreadsAsync(
        string searchTerm,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadSummary>>([]);

    public Task CompactAsync(string threadId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ProviderThreadMessage>> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderThreadMessage>>([]);

    public Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderModelInfo>>([]);

    public Task SendPromptAsync(
        string threadId,
        string prompt,
        IReadOnlyList<ProviderInputAttachment>? attachments = null,
        string? model = null,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default) =>
        FailSend
            ? Task.FromException(new InvalidOperationException("send gagal"))
            : Task.CompletedTask;

    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Emit(ProviderEvent providerEvent) => EventReceived?.Invoke(this, providerEvent);

    public void Disconnect() => IsInitialized = false;

    public void DisconnectWithError()
    {
        IsInitialized = false;
        Emit(new ProviderErrorEvent("koneksi test terputus"));
    }

    private Task<string> CountStart(Task<string> result)
    {
        StartCount++;
        return result;
    }
}
