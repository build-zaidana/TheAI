using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Channels;
using CodexBar.Core;
using CodexBar.Persistence;
using CodexBar.Providers;
using CodexBar.Providers.Anthropic;
using CodexBar.Providers.ClaudeCode;
using CodexBar.Providers.Codex;
using CodexBar.Providers.OpenAI;
using CodexBar.Providers.OpenAiCompatible;
using CodexBar.Providers.OpenRouter;

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

    var anthropic = await RunAnthropicProviderTestsAsync();
    if (anthropic != 0)
    {
        return anthropic;
    }

    var claudeCode = RunClaudeCodeProviderTests();
    if (claudeCode != 0)
    {
        return claudeCode;
    }

    var fileChanges = RunFileChangeTests();
    if (fileChanges != 0)
    {
        return fileChanges;
    }

    var openAiCompatible = await RunOpenAiCompatibleProviderTestsAsync();
    if (openAiCompatible != 0)
    {
        return openAiCompatible;
    }

    var credentials = RunCredentialStoreTests();
    if (credentials != 0)
    {
        return credentials;
    }

    var bugReport = RunBugReportTests();
    if (bugReport != 0)
    {
        return bugReport;
    }

    var rateLimit = RunRateLimitTests();
    if (rateLimit != 0)
    {
        return rateLimit;
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

    var child = GetChildProcess(provider);
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

        var failedReplacement = new FakeProvider { ProviderId = "failed", FailInitialize = true };
        await AssertThrowsAsync<InvalidOperationException>(
            () => controller.SwitchProviderAsync(failedReplacement));
        Assert(controller.ProviderId == "fake" && !provider.Disposed && failedReplacement.Disposed,
            "Live provider switch yang gagal harus mempertahankan provider lama dan membersihkan kandidat.");

        var replacement = new FakeProvider { ProviderId = "replacement" };
        await controller.SwitchProviderAsync(replacement);
        Assert(controller.ProviderId == "replacement" && replacement.IsInitialized && provider.Disposed &&
               controller.ThreadId is null && controller.WorkspacePath is null,
            "Live provider switch harus mengaktifkan provider baru dan mereset konteks provider lama.");
        var forwardedAfterSwitch = forwardedEvents;
        provider.Emit(new ProviderStatusEvent("stale"));
        replacement.Emit(new ProviderStatusEvent("active"));
        Assert(forwardedEvents == forwardedAfterSwitch + 1,
            "Setelah live switch hanya event provider aktif yang boleh diteruskan.");

        var settingsRoot = Path.Combine(temporaryRoot, "settings");
        Directory.CreateDirectory(settingsRoot);
        var store = new JsonSettingsStore(settingsRoot);
        await File.WriteAllTextAsync(store.SettingsPath, "{ bukan json");
        var recovered = await store.LoadAsync();
        Assert(recovered.SchemaVersion == 12, "Settings rusak harus dipulihkan ke schema terbaru.");
        Assert(recovered.QuickCaptureReveal == "done" && recovered.QuickCaptureHotkey.Length > 0,
            "Default quick-capture harus reveal=done + hotkey terisi.");

        await File.WriteAllTextAsync(store.SettingsPath,
            """{ "quickCaptureReveal": "CLIPBOARD", "quickCaptureHotkey": "" }""");
        var qc = await store.LoadAsync();
        Assert(qc.QuickCaptureReveal == "clipboard" && qc.QuickCaptureHotkey == "Win+Shift+X",
            "Reveal harus dinormalisasi (case-insensitive) & hotkey kosong jatuh ke default.");

        var scheduledNow = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var scheduled = ScheduledPrompt.Normalize(new[]
        {
            new ScheduledPrompt { Prompt = "nanti", RunAt = scheduledNow.AddMinutes(30) },
            new ScheduledPrompt { Prompt = "  ", RunAt = scheduledNow },
            new ScheduledPrompt { Prompt = "sekarang", RunAt = scheduledNow.AddMinutes(-5) }
        });
        Assert(scheduled.Count == 2 && scheduled[0].Prompt == "sekarang",
            "ScheduledPrompt.Normalize buang prompt kosong & urut berdasarkan waktu.");
        var due = ScheduledPrompt.Due(scheduled, scheduledNow);
        Assert(due.Count == 1 && due[0].Prompt == "sekarang",
            "ScheduledPrompt.Due hanya mengembalikan yang waktunya sudah tiba.");
        Assert(!recovered.EnableDiagnosticsLogging, "Diagnostics logging harus mati secara default (opt-in).");
        Assert(Directory.GetFiles(settingsRoot, "settings.json.invalid-*").Length == 1,
            "Settings rusak harus dibackup satu kali.");

        await File.WriteAllTextAsync(store.SettingsPath,
            """
            {
              "schemaVersion": 1,
              "provider": "lain",
              "language": "EN",
              "workspacePath": null,
              "appearance": { "theme": "asing", "inputFontSize": 999 },
              "window": { "width": 9999, "maxExpandedHeight": -1, "position": "asing" },
              "threadIds": null,
              "promptTemplates": [
                { "name": "  Valid  ", "content": "isi" },
                { "name": "", "content": "tanpa nama" },
                { "name": "Kosong", "content": "   " },
                { "name": "valid", "content": "duplikat nama" }
              ]
            }
            """);
        var normalized = await store.LoadAsync();
        Assert(normalized.Provider == "codex" && normalized.WorkspacePath == string.Empty,
            "Provider dan workspace harus dinormalisasi.");
        Assert(normalized.Appearance.Theme == "system" && normalized.Appearance.InputFontSize == 24,
            "Appearance di luar batas harus dinormalisasi.");
        Assert(normalized.Window.Width == 1200 && normalized.Window.MaxExpandedHeight == 180,
            "Ukuran window harus dibatasi.");
        Assert(normalized.PromptTemplates.Count == 1 &&
               normalized.PromptTemplates[0].Name == "Valid" &&
               normalized.PromptTemplates[0].Content == "isi",
            "Prompt template harus di-trim, buang nama/isi kosong, dan dedup nama.");
        Assert(PromptTemplate.CreateDefaults().Count >= 1 &&
               PromptTemplate.Normalize(PromptTemplate.CreateDefaults()).Count == PromptTemplate.CreateDefaults().Count,
            "Template default harus valid dan stabil terhadap normalisasi.");
        Assert(PromptTemplate.Expand("Review {selection}\nContext: {CLIPBOARD}", "kode terpilih", "clipboard") ==
               "Review kode terpilih\nContext: clipboard",
            "Variabel template selection/clipboard harus di-expand case-insensitive.");
        Assert(normalized.Language == "en", "Kode bahasa harus dinormalisasi (case-insensitive).");

        await File.WriteAllTextAsync(store.SettingsPath, """{ "provider": "Anthropic" }""");
        Assert((await store.LoadAsync()).Provider == "anthropic",
            "Provider anthropic harus dipertahankan (case-insensitive), yang tak dikenal jatuh ke codex.");

        await File.WriteAllTextAsync(store.SettingsPath, """{ "provider": "Claude-Code" }""");
        Assert((await store.LoadAsync()).Provider == "claude-code",
            "Provider claude-code harus dipertahankan (case-insensitive).");

        await File.WriteAllTextAsync(store.SettingsPath, """{ "provider": "OpenAI" }""");
        Assert((await store.LoadAsync()).Provider == "openai",
            "Provider openai harus dipertahankan (case-insensitive).");

        await File.WriteAllTextAsync(store.SettingsPath, """{ "provider": "OpenRouter" }""");
        Assert((await store.LoadAsync()).Provider == "openrouter",
            "Provider openrouter harus dipertahankan (case-insensitive).");

        await File.WriteAllTextAsync(store.SettingsPath,
            """{ "pendingResume": { "prompt": "   ", "retryAt": "2030-01-01T00:00:00+00:00" } }""");
        Assert((await store.LoadAsync()).PendingResume is null,
            "PendingResume dengan prompt kosong harus dinormalisasi ke null.");
        await File.WriteAllTextAsync(store.SettingsPath,
            """{ "pendingResume": { "prompt": "lanjut", "retryAt": "2030-01-01T00:00:00+00:00", "isContinuation": true } }""");
        Assert((await store.LoadAsync()).PendingResume is { Prompt: "lanjut", IsContinuation: true },
            "PendingResume valid harus dipertahankan lintas load (persist across restart).");

        var history = new PromptHistory(capacity: 2);
        history.Add("  pertama  ");
        history.Add("kedua");
        history.Add("kedua");
        history.Add("ketiga");
        Assert(history.Items.SequenceEqual(["kedua", "ketiga"]),
            "Prompt history harus trim, cegah duplikat berurutan, dan patuh kapasitas.");
        Assert(history.Previous("draft") == "ketiga" &&
               history.Previous("draft") == "kedua" &&
               history.Previous("draft") == "kedua" &&
               history.Next() == "ketiga" &&
               history.Next() == "draft",
            "Navigasi prompt history Up/Down harus mempertahankan draft input.");

        // Localization: parsing, lookup with fallback, and live-switch notification.
        Assert(Localization.Parse("en") == AppLanguage.En && Localization.Parse("ID") == AppLanguage.Id &&
               Localization.Parse("xx") == AppLanguage.Id && Localization.ToCode(AppLanguage.En) == "en",
            "Parsing/serialisasi kode bahasa harus benar dengan fallback ke Indonesia.");
        Assert(Strings.Get(AppLanguage.En, "settings.close") == "Close" &&
               Strings.Get(AppLanguage.Id, "settings.close") == "Tutup" &&
               Strings.Get(AppLanguage.En, "kunci.tidak.ada") == "kunci.tidak.ada",
            "Strings.Get harus per-bahasa dengan fallback ke key untuk yang belum diterjemahkan.");
        var localizationChanged = false;
        void OnLocalizationChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __) => localizationChanged = true;
        Localization.Instance.PropertyChanged += OnLocalizationChanged;
        Localization.Instance.SetLanguage(AppLanguage.En);
        Assert(localizationChanged && Localization.Instance["settings.close"] == "Close",
            "Ganti bahasa harus memicu notifikasi dan indexer mengikuti bahasa baru.");
        Localization.Instance.SetLanguage(AppLanguage.Id);
        Localization.Instance.PropertyChanged -= OnLocalizationChanged;
        await store.SaveAsync(normalized);
        Assert(Directory.GetFiles(settingsRoot, "*.tmp").Length == 0,
            "Save settings tidak boleh meninggalkan file temporary.");
        var exportPath = Path.Combine(temporaryRoot, "settings-export.json");
        var exportSettings = new AppSettings
        {
            Language = "EN",
            Hotkey = " Ctrl+Alt+C ",
            WorkspacePath = temporaryRoot,
            SandboxMode = "read-only",
            ApprovalPolicy = "never",
            PromptTemplates =
            [
                new PromptTemplate { Name = "  Ekspor  ", Content = "Isi {selection}" },
                new PromptTemplate { Name = "ekspor", Content = "duplikat" }
            ],
            Appearance = new AppearanceSettings { Theme = "dark", InputFontSize = 18 },
            ThreadIds = new Dictionary<string, string> { ["C:\\Repo"] = "thread-1" }
        };
        await store.ExportAsync(exportSettings, exportPath);
        var importedSettings = await store.ImportAsync(exportPath);
        Assert(importedSettings.Language == "en" &&
               importedSettings.Hotkey == "Ctrl+Alt+C" &&
               importedSettings.SandboxMode == "read-only" &&
               importedSettings.ApprovalPolicy == "never" &&
               importedSettings.PromptTemplates.Count == 1 &&
               importedSettings.PromptTemplates[0].Name == "Ekspor" &&
               importedSettings.PromptTemplates[0].Content == "Isi {selection}" &&
               importedSettings.Appearance.Theme == "dark" &&
               importedSettings.Appearance.InputFontSize == 18,
            "Export/import settings harus roundtrip dan tetap melewati normalisasi.");
        Assert(importedSettings.ThreadIds.ContainsKey("c:\\repo"),
            "ThreadIds hasil import harus tetap case-insensitive.");
        var applied = new AppSettings();
        applied.ApplyFrom(importedSettings);
        importedSettings.PromptTemplates[0].Name = "Mutasi";
        importedSettings.Appearance.Theme = "light";
        importedSettings.ThreadIds["D:\\Repo"] = "thread-2";
        Assert(applied.PromptTemplates[0].Name == "Ekspor" &&
               applied.Appearance.Theme == "dark" &&
               !applied.ThreadIds.ContainsKey("D:\\Repo"),
            "ApplyFrom harus melakukan copy mandiri untuk template, appearance, dan thread map.");

        // Diagnostics logging: opt-in, best-effort, writes a per-day file when enabled.
        var diagRoot = Path.Combine(temporaryRoot, "diag");
        DiagnosticsLog.Configure(enabled: true, diagRoot);
        Assert(DiagnosticsLog.IsEnabled && DiagnosticsLog.LogDirectory is not null,
            "DiagnosticsLog harus aktif setelah Configure(enabled:true).");
        DiagnosticsLog.Error("Test", "pesan diagnostik", new InvalidOperationException("boom"));
        var logFiles = Directory.GetFiles(DiagnosticsLog.LogDirectory!, "codexbar-*.log");
        Assert(logFiles.Length == 1, "DiagnosticsLog harus menulis satu file log per hari.");
        var logText = await File.ReadAllTextAsync(logFiles[0]);
        Assert(logText.Contains("pesan diagnostik", StringComparison.Ordinal) &&
               logText.Contains("boom", StringComparison.Ordinal),
            "Log harus memuat pesan dan detail exception.");

        var lengthBeforeDisable = new FileInfo(logFiles[0]).Length;
        DiagnosticsLog.Configure(enabled: false, diagRoot);
        Assert(!DiagnosticsLog.IsEnabled && DiagnosticsLog.LogDirectory is null,
            "DiagnosticsLog harus mati setelah Configure(enabled:false).");
        DiagnosticsLog.Info("Test", "tidak boleh ditulis");
        Assert(new FileInfo(logFiles[0]).Length == lengthBeforeDisable,
            "Saat mati, DiagnosticsLog tidak boleh menambah entri baru.");

        await RunTransportSeamTestsAsync();

        var protocolTransport = new TestCodexTransport
        {
            Responder = request =>
            {
                if (!request.TryGetProperty("id", out var id) ||
                    !request.TryGetProperty("method", out var method) ||
                    method.GetString() != "initialize")
                {
                    return null;
                }

                return $"{{\"id\":{id.GetRawText()},\"result\":{{}}}}";
            }
        };
        await using var protocolProvider = new CodexProvider(
            () => protocolTransport,
            TimeSpan.FromSeconds(1),
            key => Strings.Get(AppLanguage.En, key));
        await protocolProvider.InitializeAsync();
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
        Assert(userInput!.Title == "Codex needs an answer" &&
               userInput.Message == "Answer so the agent can continue working.",
            "Teks fallback request_user_input harus mengikuti bahasa aplikasi.");
        Assert(userInput.AutoResolutionMs == 60000 && userInput.Questions[0].AllowsOther &&
               userInput.Questions[0].Options.Count == 2,
            "Metadata pilihan dan auto-resolution request_user_input harus dipertahankan.");
        var userInputResponse = await CaptureServerResponseAsync(protocolTransport, () => userInput.RespondAsync(
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
        Assert(mcpInput!.Title == "Input for test-server",
            "Judul fallback MCP form harus mengikuti bahasa aplikasi.");
        Assert(mcpInput.Questions.Single(question => question.Id == "count").ValueType == "integer" &&
               mcpInput.Questions.Single(question => question.Id == "count").IsRequired,
            "Tipe dan required MCP field harus dipertahankan.");
        Assert(mcpInput.Questions.Single(question => question.Id == "tags").AllowsMultiple,
            "MCP array enum harus menjadi pilihan jamak.");
        var mcpResponse = await CaptureServerResponseAsync(protocolTransport, () => mcpInput.RespondAsync(
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
        InvokeServerRequest(protocolProvider, "mcpServer/elicitation/request",
            """
            {
              "serverName": "test-server",
              "threadId": "thread-test",
              "turnId": "turn-test",
              "mode": "url",
              "url": "https://example.com/authorize"
            }
            """);
        var mcpUrl = protocolEvent as ProviderUserInputEvent;
        Assert(mcpUrl is not null && mcpUrl.Title == "Connect test-server" &&
               mcpUrl.Message == "Complete the steps in your browser, then return to CodexBar." &&
               mcpUrl.Questions.Single().Header == "Confirmation" &&
               mcpUrl.Questions.Single().Options.Single().Label == "I'm finished",
            "Teks fallback MCP URL elicitation harus mengikuti bahasa aplikasi.");
        _ = await CaptureServerResponseAsync(protocolTransport, () => mcpUrl!.RespondAsync(null));

        protocolEvent = null;
        InvokeNotification(protocolProvider, "turn/started", """{"turn":{"id":"turn-test"}}""");
        Assert(protocolEvent is ProviderStatusEvent { Message: "Codex is working…" },
            "Status dinamis provider harus mengikuti bahasa aplikasi.");

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
        Assert(commandApproval!.Summary.StartsWith("Allow Codex to run the following command?", StringComparison.Ordinal) &&
               commandApproval.Summary.Contains("Folder: C:\\workspace", StringComparison.Ordinal) &&
               commandApproval.Summary.Contains("Reason: Verifikasi perubahan", StringComparison.Ordinal),
            "Ringkasan approval harus mengikuti bahasa aplikasi.");
        var commandResponse = await CaptureServerResponseAsync(protocolTransport, () =>
            commandApproval.RespondAsync(ProviderApprovalDecision.AcceptForSession));
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
        var permissionResponse = await CaptureServerResponseAsync(protocolTransport, () =>
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

static async Task<string> CaptureServerResponseAsync(TestCodexTransport transport, Func<Task> respond)
{
    var before = transport.WrittenLines.Count;
    await respond();
    return transport.WrittenLines.Skip(before).LastOrDefault()
        ?? throw new InvalidOperationException("Provider tidak menulis response transport.");
}

static async Task RunTransportSeamTestsAsync()
{
    static string? InitializeOnly(JsonElement request)
    {
        if (!request.TryGetProperty("id", out var id) ||
            !request.TryGetProperty("method", out var method) ||
            method.GetString() != "initialize")
        {
            return null;
        }

        return $"{{\"id\":{id.GetRawText()},\"result\":{{}}}}";
    }

    var timeoutTransport = new TestCodexTransport { Responder = InitializeOnly };
    await using (var provider = new CodexProvider(
        () => timeoutTransport,
        TimeSpan.FromMilliseconds(60)))
    {
        await provider.InitializeAsync();
        await AssertThrowsAsync<TimeoutException>(() => provider.GetAccountAsync());
        Assert(provider.IsInitialized,
            "Timeout satu RPC tidak boleh menandai seluruh transport terputus.");
    }

    var rpcTransport = new TestCodexTransport
    {
        Responder = request =>
        {
            if (!request.TryGetProperty("id", out var id))
            {
                return null;
            }

            return request.TryGetProperty("method", out var method) && method.GetString() == "initialize"
                ? $"{{\"id\":{id.GetRawText()},\"result\":{{}}}}"
                : request.TryGetProperty("method", out _)
                    ? $"{{\"id\":{id.GetRawText()},\"error\":{{\"code\":-32000,\"message\":\"rpc-boom\"}}}}"
                    : null;
        }
    };
    await using (var provider = new CodexProvider(
        () => rpcTransport,
        TimeSpan.FromSeconds(1)))
    {
        await provider.InitializeAsync();
        await AssertThrowsContainingAsync(() => provider.GetAccountAsync(), "rpc-boom");
    }

    var disconnectTransport = new TestCodexTransport { Responder = InitializeOnly };
    await using (var provider = new CodexProvider(
        () => disconnectTransport,
        TimeSpan.FromSeconds(1)))
    {
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.EventReceived += (_, providerEvent) =>
        {
            if (providerEvent is ProviderErrorEvent error)
            {
                disconnected.TrySetResult(error.Message);
            }
        };
        await provider.InitializeAsync();
        disconnectTransport.Disconnect();
        var message = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(!provider.IsInitialized && message.Contains("terputus", StringComparison.OrdinalIgnoreCase),
            "EOF transport harus menandai provider disconnected dan menerbitkan error.");
    }
}

static async Task AssertThrowsContainingAsync(Func<Task> action, string expectedText)
{
    try
    {
        await action();
    }
    catch (Exception exception) when (exception.Message.Contains(expectedText, StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException($"Exception yang memuat '{expectedText}' tidak terjadi.");
}

static async Task<int> RunAnthropicProviderTestsAsync()
{
    try
    {
        var models = AnthropicProvider.KnownModels();
        Assert(models.Count >= 1 && models.Count(m => m.IsDefault) == 1,
            "Harus ada tepat satu model Claude default.");
        Assert(models.Single(m => m.IsDefault).Model == AnthropicProvider.DefaultModel,
            "Model default harus konsisten dengan DefaultModel.");

        var requestJson = AnthropicProvider.BuildRequestJson(
            "claude-sonnet-5",
            [new AnthropicProvider.ChatTurn("user", "hai")],
            4096);
        using (var doc = JsonDocument.Parse(requestJson))
        {
            var root = doc.RootElement;
            Assert(root.GetProperty("model").GetString() == "claude-sonnet-5", "Model harus masuk request.");
            Assert(root.GetProperty("max_tokens").GetInt32() == 4096, "max_tokens harus di-set.");
            Assert(root.GetProperty("stream").GetBoolean(), "stream harus true.");
            var first = root.GetProperty("messages")[0];
            Assert(first.GetProperty("role").GetString() == "user" && first.GetProperty("content").GetString() == "hai",
                "Pesan user harus masuk request dengan benar.");
        }

        Assert(AnthropicProvider.ParseSseData("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"Halo"}}""").TextDelta == "Halo",
            "text_delta harus terbaca.");
        Assert(AnthropicProvider.ParseSseData("""{"type":"message_start","message":{"usage":{"input_tokens":12}}}""").InputTokens == 12,
            "input_tokens harus terbaca dari message_start.");
        Assert(AnthropicProvider.ParseSseData("""{"type":"message_delta","usage":{"output_tokens":7}}""").OutputTokens == 7,
            "output_tokens harus terbaca dari message_delta.");
        Assert(AnthropicProvider.ParseSseData("""{"type":"message_stop"}""").Stop, "message_stop harus set Stop.");
        Assert(AnthropicProvider.ParseSseData("""{"type":"error","error":{"message":"boom"}}""").Error == "boom",
            "Event error harus terbaca.");
        Assert(AnthropicProvider.ParseSseData("bukan json").TextDelta is null, "JSON rusak harus aman (default).");

        const string sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\" world\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        await using var provider = new AnthropicProvider(new HttpClient(new CannedSseHandler(sse)), () => "test-key");
        var text = new StringBuilder();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        long totalTokens = 0;
        provider.EventReceived += (_, ev) =>
        {
            switch (ev)
            {
                case ProviderTextDeltaEvent d: text.Append(d.Delta); break;
                case ProviderTokenUsageEvent u: totalTokens = u.TotalTokens; break;
                case ProviderCompletedEvent c: done.TrySetResult(c.Status); break;
                case ProviderErrorEvent e: done.TrySetException(new InvalidOperationException(e.Message)); break;
            }
        };

        await provider.InitializeAsync();
        var account = await provider.GetAccountAsync();
        Assert(account.IsAuthenticated && account.AuthenticationMethod == "api-key",
            "Akun API-key harus terautentikasi saat key tersedia.");
        var threadId = await provider.StartOrResumeThreadAsync("", null, "read-only", "never");
        await provider.SendPromptAsync(threadId, "hi");
        var status = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(status == "completed" && text.ToString() == "Hello world",
            "Streaming Claude harus merakit teks penuh lalu selesai.");
        Assert(totalTokens == 15, "Token usage total harus input+output.");

        await using var noKey = new AnthropicProvider(new HttpClient(new CannedSseHandler(sse)), () => null);
        var errored = false;
        noKey.EventReceived += (_, ev) => { if (ev is ProviderErrorEvent) { errored = true; } };
        await noKey.SendPromptAsync("t", "hi");
        Assert(errored, "Tanpa API key harus memunculkan error, bukan crash.");

        await AssertLocalConversationPersistsAsync(
            "anthropic",
            () => new AnthropicProvider(new HttpClient(new CannedSseHandler(sse)), () => "test-key"));

        Console.WriteLine("PASS anthropic provider tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
}

static int RunFileChangeTests()
{
    try
    {
        var diff = LineDiff.Build("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nC\nd\ne\nf\ng\nh\n", context: 1);
        Assert(LineDiff.Count(diff) == (2, 1), $"LineDiff harus menghitung +2 -1, dapat {LineDiff.Count(diff)}: {diff}");
        Assert(diff.Contains("-c\n") && diff.Contains("+C\n") && diff.Contains("+h\n") && !diff.Contains(" a\n"),
            "LineDiff harus memotong konteks jauh dan menandai baris yang berubah.");
        Assert(LineDiff.Build("same\n", "same\n").Length == 0, "Teks yang sama tidak boleh menghasilkan diff.");
        Assert(LineDiff.Count("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n+new\n") == (1, 1),
            "Header file unified diff tidak boleh ikut dihitung.");

        using var codexItem = JsonDocument.Parse("""
            {"type":"fileChange","id":"i1","status":"completed","changes":[
              {"path":"C:/w/a.cs","kind":{"type":"update","move_path":"C:/w/b.cs"},"diff":"@@\n-x\n+y\n"},
              {"path":"C:/w/new.cs","kind":{"type":"add"},"diff":"hello\n"}]}
            """);
        var codexChanges = CodexProvider.ParseFileChangeItem(codexItem.RootElement);
        Assert(codexChanges.Count == 2 && codexChanges[0].MovePath == "C:/w/b.cs" && codexChanges[1].Kind == "add",
            "fileChange Codex harus diparse beserta kind dan move_path.");
        using var declined = JsonDocument.Parse("""{"type":"fileChange","id":"i2","status":"declined","changes":[{"path":"x","kind":{"type":"add"},"diff":""}]}""");
        Assert(CodexProvider.ParseFileChangeItem(declined.RootElement).Count == 0, "Patch yang ditolak tidak boleh dilaporkan.");

        var edits = ClaudeCodeProvider.ParseFileEdits("""
            {"type":"assistant","message":{"content":[
              {"type":"text","text":"ok"},
              {"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"C:/w/a.ts","old_string":"x","new_string":"y"}},
              {"type":"tool_use","id":"t2","name":"Write","input":{"file_path":"C:/w/b.ts","content":"z"}},
              {"type":"tool_use","id":"t3","name":"Bash","input":{"command":"ls"}}]}}
            """.ReplaceLineEndings(""));
        Assert(edits.Count == 2 && edits[0].Replacements[0] == ("x", "y") && edits[1].Content == "z",
            "Edit dan Write Claude Code harus dikenali, tool lain diabaikan.");
        var results = ClaudeCodeProvider.ParseToolResults(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"},{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":"no"}]}}""");
        Assert(results.Count == 2 && !results[0].IsError && results[1].IsError, "tool_result harus diparse dengan is_error.");

        var quota = ClaudeCodeProvider.ParseRateLimitEvent(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1791049800,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.34,"resetsAt":1791049800},"seven_day":{"utilization":0.42,"resetsAt":1791471600}}}}""");
        Assert(quota is { LimitReached: false } && quota.Windows.Count == 2 &&
               quota.Windows[0] == new ProviderQuotaWindow("session", 0.34, DateTimeOffset.FromUnixTimeSeconds(1791049800)) &&
               quota.Windows[1].Id == "weekly",
            "rate_limit_event Claude Code harus jadi jendela session dan weekly.");
        var rejected = ClaudeCodeProvider.ParseRateLimitEvent(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1791049800,"rateLimitType":"five_hour"}}""");
        Assert(rejected is { LimitReached: true } && rejected.Windows.Single().UsedFraction == 1,
            "Batas yang tercapai tanpa unifiedWindows harus tetap dilaporkan.");
        Assert(ClaudeCodeProvider.ParseRateLimitEvent("""{"type":"result"}""") is null, "Baris lain bukan event kuota.");

        using var snapshot = JsonDocument.Parse("""
            {"primary":{"usedPercent":61,"windowDurationMins":300,"resetsAt":1791049800},
             "secondary":{"usedPercent":12,"windowDurationMins":10080}}
            """);
        var codexWindows = CodexProvider.ParseRateLimitSnapshot(snapshot.RootElement);
        Assert(codexWindows.Count == 2 && codexWindows[0].Id == "session" && Math.Abs(codexWindows[0].UsedFraction - 0.61) < 1e-9 &&
               codexWindows[1].Id == "weekly" && codexWindows[1].ResetsAt is null,
            "RateLimitSnapshot Codex harus dipetakan ke session/weekly.");

        var sessionDir = Path.Combine(Path.GetTempPath(), "codexbar-session-test");
        Directory.CreateDirectory(sessionDir);
        var sessionFile = Path.Combine(sessionDir, "11111111-2222-3333-4444-555555555555.jsonl");
        File.WriteAllLines(sessionFile, new[]
        {
            """{"type":"queue-operation","operation":"enqueue","content":"x"}""",
            """{"type":"user","isMeta":true,"entrypoint":"cli","cwd":"C:\\proj\\web","message":{"role":"user","content":"<command-name>/init</command-name>"}}""",
            """{"type":"user","entrypoint":"cli","cwd":"C:\\proj\\web","message":{"role":"user","content":"Perbaiki bug login"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Saya cek dulu."},{"type":"tool_use","id":"t","name":"Read","input":{}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"file"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Sudah diperbaiki."}]}}""",
            """{"type":"ai-title","aiTitle":"Bug login","sessionId":"11111111-2222-3333-4444-555555555555"}"""
        });
        var info = ClaudeSessionFiles.Parse(new FileInfo(sessionFile));
        Assert(info is { Cwd: @"C:\proj\web", Entrypoint: "cli", Title: "Bug login", Source: "Terminal" },
            $"Ringkasan sesi Claude Code salah: {info}");
        var history = ClaudeSessionFiles.ReadMessages(sessionFile);
        Assert(history.Count == 2 && history[0] == new ProviderThreadMessage("user", "Perbaiki bug login") &&
               history[1].Text == "Saya cek dulu.\n\nSudah diperbaiki.",
            "Riwayat sesi harus berisi prompt manusia dan teks asisten saja.");
        Assert(ClaudeSessionFiles.ReadEntrypoint(sessionFile) == "cli", "Entrypoint harus terbaca dari baris awal.");
        Directory.Delete(sessionDir, recursive: true);

        Console.WriteLine("PASS file change and quota tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL file change tests: {exception.Message}");
        return 1;
    }
}

static int RunClaudeCodeProviderTests()
{
    try
    {
        var models = ClaudeCodeProvider.KnownModels();
        Assert(models.Count(m => m.IsDefault) == 1 &&
               models.Single(m => m.IsDefault).Model == ClaudeCodeProvider.DefaultModel,
            "Harus ada tepat satu model default Claude Code yang konsisten dengan DefaultModel.");

        var first = ClaudeCodeProvider.BuildArguments(
            "11111111-1111-1111-1111-111111111111", false, "default", null, "workspace-write", []);
        Assert(first.Contains("-p") && first.Contains("--output-format stream-json") &&
               first.Contains("--include-partial-messages") &&
               first.Contains("--session-id 11111111-1111-1111-1111-111111111111") &&
               first.Contains("--permission-mode acceptEdits") &&
               !first.Contains("--model") && !first.Contains("--resume"),
            "Turn pertama harus memakai --session-id, stream-json, dan tanpa --model untuk default.");

        var next = ClaudeCodeProvider.BuildArguments(
            "abc", true, "opus", "minimal", "read-only", [@"C:\Folder Lampiran"]);
        Assert(next.Contains("--resume abc") && next.Contains("--model opus") &&
               next.Contains("--effort low") && next.Contains("--permission-mode plan") &&
               next.Contains(@"--add-dir ""C:\Folder Lampiran"""),
            "Turn lanjutan harus --resume, map effort/sandbox, dan quote path berspasi.");
        Assert(ClaudeCodeProvider.PermissionModeFor("danger-full-access") == "bypassPermissions",
            "Full access harus map ke bypassPermissions.");

        var text = ClaudeCodeProvider.ParseStreamLine(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Halo"}}}""");
        Assert(text.Kind == ClaudeCodeProvider.StreamKind.TextDelta && text.Text == "Halo",
            "text_delta harus jadi TextDelta.");
        var thinking = ClaudeCodeProvider.ParseStreamLine(
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"hmm"}}}""");
        Assert(thinking.Kind == ClaudeCodeProvider.StreamKind.ReasoningDelta && thinking.Text == "hmm",
            "thinking_delta harus jadi ReasoningDelta.");
        var tool = ClaudeCodeProvider.ParseStreamLine(
            """{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"Bash"}}}""");
        Assert(tool.Kind == ClaudeCodeProvider.StreamKind.ToolUse && tool.Text == "Bash",
            "tool_use harus jadi ToolUse dengan nama tool.");
        var result = ClaudeCodeProvider.ParseStreamLine(
            """{"type":"result","subtype":"success","is_error":false,"result":"Halo","usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":7}}""");
        Assert(result.Kind == ClaudeCodeProvider.StreamKind.Result && !result.IsError &&
               result.TotalTokens == 22 && result.OutputTokens == 7,
            "result harus membawa total token (termasuk cache) dan output token.");
        var failed = ClaudeCodeProvider.ParseStreamLine(
            """{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached"}""");
        Assert(failed.IsError && failed.Text!.Contains("usage limit"), "is_error harus jadi error.");
        Assert(ClaudeCodeProvider.ParseStreamLine("""{"type":"assistant","message":{}}""").Kind == ClaudeCodeProvider.StreamKind.None &&
               ClaudeCodeProvider.ParseStreamLine("bukan json").Kind == ClaudeCodeProvider.StreamKind.None,
            "Baris lain/rusak harus diabaikan.");

        var loggedIn = ClaudeCodeProvider.ParseAuthStatus(
            """{"loggedIn":true,"authMethod":"claude.ai","email":"a@b.c","subscriptionType":"max"}""");
        Assert(loggedIn.LoggedIn && loggedIn.SubscriptionType == "max" && loggedIn.AuthMethod == "claude.ai",
            "auth status login harus terbaca.");
        Assert(!ClaudeCodeProvider.ParseAuthStatus("""{"loggedIn":false,"authMethod":"none"}""").LoggedIn &&
               !ClaudeCodeProvider.ParseAuthStatus("error").LoggedIn,
            "auth status belum login / rusak harus dianggap belum login.");

        Console.WriteLine("PASS claude code provider tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
}

static async Task<int> RunOpenAiCompatibleProviderTestsAsync()
{
    try
    {
        var openAiModels = OpenAIProvider.KnownModels();
        Assert(openAiModels.Count >= 1 && openAiModels.Count(m => m.IsDefault) == 1,
            "Harus ada tepat satu model OpenAI default.");
        Assert(openAiModels.Single(m => m.IsDefault).Model == OpenAIProvider.DefaultModel,
            "Model default OpenAI harus konsisten dengan DefaultModel.");

        var openRouterModels = OpenRouterProvider.KnownModels();
        Assert(openRouterModels.Count >= 1 && openRouterModels.Count(m => m.IsDefault) == 1,
            "Harus ada tepat satu model OpenRouter default.");
        Assert(openRouterModels.Single(m => m.IsDefault).Model == OpenRouterProvider.DefaultModel,
            "Model default OpenRouter harus konsisten dengan DefaultModel.");

        var requestJson = OpenAiCompatibleProvider.BuildRequestJson(
            "gpt-4.1",
            [new OpenAiCompatibleProvider.ChatTurn("user", "hai")]);
        using (var doc = JsonDocument.Parse(requestJson))
        {
            var root = doc.RootElement;
            Assert(root.GetProperty("model").GetString() == "gpt-4.1", "Model harus masuk request.");
            Assert(root.GetProperty("stream").GetBoolean(), "stream harus true.");
            Assert(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(),
                "stream_options.include_usage harus true.");
            var first = root.GetProperty("messages")[0];
            Assert(first.GetProperty("role").GetString() == "user" && first.GetProperty("content").GetString() == "hai",
                "Pesan user harus masuk request dengan benar.");
        }

        Assert(OpenAiCompatibleProvider.ParseSseData("""{"choices":[{"delta":{"content":"Halo"}}]}""").TextDelta == "Halo",
            "delta.content harus terbaca.");
        Assert(OpenAiCompatibleProvider.ParseSseData("""{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":7}}""") is
            { InputTokens: 12, OutputTokens: 7 },
            "usage.prompt_tokens/completion_tokens harus terbaca.");
        Assert(OpenAiCompatibleProvider.ParseSseData("""{"error":{"message":"boom"}}""").Error == "boom",
            "Event error harus terbaca.");
        Assert(OpenAiCompatibleProvider.ParseSseData("bukan json").TextDelta is null, "JSON rusak harus aman (default).");

        const string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
            "data: [DONE]\n\n";

        await using var provider = new OpenAIProvider(new HttpClient(new CannedSseHandler(sse)), () => "test-key");
        var text = new StringBuilder();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        long totalTokens = 0;
        provider.EventReceived += (_, ev) =>
        {
            switch (ev)
            {
                case ProviderTextDeltaEvent d: text.Append(d.Delta); break;
                case ProviderTokenUsageEvent u: totalTokens = u.TotalTokens; break;
                case ProviderCompletedEvent c: done.TrySetResult(c.Status); break;
                case ProviderErrorEvent e: done.TrySetException(new InvalidOperationException(e.Message)); break;
            }
        };

        await provider.InitializeAsync();
        var account = await provider.GetAccountAsync();
        Assert(account.IsAuthenticated && account.AuthenticationMethod == "api-key",
            "Akun API-key harus terautentikasi saat key tersedia.");
        var threadId = await provider.StartOrResumeThreadAsync("", null, "read-only", "never");
        await provider.SendPromptAsync(threadId, "hi");
        var status = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(status == "completed" && text.ToString() == "Hello world",
            "Streaming OpenAI harus merakit teks penuh lalu selesai.");
        Assert(totalTokens == 15, "Token usage total harus input+output.");

        await using var noKey = new OpenAIProvider(new HttpClient(new CannedSseHandler(sse)), () => null);
        var errored = false;
        noKey.EventReceived += (_, ev) => { if (ev is ProviderErrorEvent) { errored = true; } };
        await noKey.SendPromptAsync("t", "hi");
        Assert(errored, "Tanpa API key harus memunculkan error, bukan crash.");

        await using var openRouterProvider = new OpenRouterProvider(new HttpClient(new CannedSseHandler(sse)), () => "test-key");
        await openRouterProvider.InitializeAsync();
        Assert(openRouterProvider.Id == "openrouter" && openRouterProvider.DisplayName == "OpenRouter",
            "OpenRouterProvider harus melapor Id/DisplayName yang benar.");

        await AssertLocalConversationPersistsAsync(
            "openai",
            () => new OpenAIProvider(new HttpClient(new CannedSseHandler(sse)), () => "test-key"));

        Console.WriteLine("PASS openai-compatible provider tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
}

/// <summary>
/// Sends one turn on a fresh provider instance under a throwaway thread id, then opens that
/// same thread id on a second fresh instance and asserts the conversation comes back from
/// disk (CodexBar.Providers.LocalConversationStore) instead of being empty.
/// </summary>
static async Task AssertLocalConversationPersistsAsync(string providerId, Func<IAgentProvider> factory)
{
    var threadId = $"test-persist-{Guid.NewGuid():N}";
    try
    {
        await using (var writer = factory())
        {
            await writer.StartOrResumeThreadAsync("", threadId, "read-only", "never");
            await writer.SendPromptAsync(threadId, "hi");
        }

        await using (var reader = factory())
        {
            await reader.StartOrResumeThreadAsync("", threadId, "read-only", "never");
            var history = await reader.ReadThreadAsync(threadId);
            Assert(history.Count == 2 && history[0].Text == "hi" && history[1].Text == "Hello world",
                $"Percakapan lokal {providerId} harus dipulihkan dari disk saat thread id yang sama dibuka lagi.");

            var listed = await reader.ListThreadsAsync();
            var summary = listed.FirstOrDefault(t => t.Id == threadId);
            Assert(summary is not null && summary.Title == "hi" && summary.Preview == "Hello world",
                $"ListThreadsAsync {providerId} harus menampilkan thread yang baru disimpan dengan title/preview dari pesan.");

            var found = await reader.SearchThreadsAsync("hello");
            Assert(found.Any(t => t.Id == threadId),
                $"SearchThreadsAsync {providerId} harus menemukan thread lewat kata dalam preview.");

            var notFound = await reader.SearchThreadsAsync($"tidak-ada-{Guid.NewGuid():N}");
            Assert(notFound.All(t => t.Id != threadId),
                $"SearchThreadsAsync {providerId} tidak boleh mengembalikan thread yang tidak cocok.");
        }
    }
    finally
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBar", "conversations", providerId, $"{threadId}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

static int RunRateLimitTests()
{
    try
    {
        Assert(RateLimit.IsRateLimited("You've hit your usage limit. Try again at 3:23 AM."),
            "'usage limit' harus terdeteksi rate-limited.");
        Assert(RateLimit.IsRateLimited("429 Too Many Requests"), "'429' harus terdeteksi.");
        Assert(!RateLimit.IsRateLimited("Koneksi terputus"), "Error biasa tidak boleh dianggap rate-limit.");
        Assert(!RateLimit.IsRateLimited(null), "Null aman.");

        // "try again at 3:23 AM" — sebelum jam itu hari ini → hari ini.
        var early = new DateTimeOffset(2026, 7, 2, 0, 42, 0, TimeSpan.FromHours(7));
        var atEarly = RateLimit.ParseRetryAt("hit your usage limit. try again at 3:23 AM.", early);
        Assert(atEarly is { } e && e.Day == 2 && e.Hour == 3 && e.Minute == 23,
            "Sebelum jam reset → jadwal hari ini pada jam tersebut.");

        // Sesudah jam itu → besok.
        var late = new DateTimeOffset(2026, 7, 2, 5, 0, 0, TimeSpan.FromHours(7));
        var atLate = RateLimit.ParseRetryAt("try again at 3:23 AM", late);
        Assert(atLate is { } l && l.Day == 3 && l.Hour == 3 && l.Minute == 23,
            "Setelah jam reset lewat → jadwal besok.");

        // PM konversi.
        var pm = RateLimit.ParseRetryAt("try again at 1:00 PM", early);
        Assert(pm is { } p && p.Hour == 13, "PM harus dikonversi ke 24 jam.");

        // Durasi relatif.
        var inHours = RateLimit.ParseRetryAt("try again in 2 hours", early);
        Assert(inHours == early.AddHours(2), "'in 2 hours' harus now + 2 jam.");
        var inMin = RateLimit.ParseRetryAt("try again in 30 minutes", early);
        Assert(inMin == early.AddMinutes(30), "'in 30 minutes' harus now + 30 menit.");

        Assert(RateLimit.ParseRetryAt("tidak ada waktu di sini", early) is null,
            "Tanpa info waktu harus null.");
        Assert(RateLimit.FromRetryAfterSeconds(60, early) == early.AddSeconds(60),
            "Retry-After detik harus now + detik.");

        Console.WriteLine("PASS rate limit tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
}

static int RunBugReportTests()
{
    var root = Path.Combine(Path.GetTempPath(), $"CodexBar-bugreport-{Guid.NewGuid():N}");
    try
    {
        var logsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "codexbar-20260101.log"), "baris log contoh");
        File.WriteAllText(Path.Combine(logsDir, "codexbar-20260102.log"), "baris log lain");
        File.WriteAllText(Path.Combine(logsDir, "ignore.txt"), "bukan log");

        var zipPath = BugReport.Create(root, "CodexBar 9.9.9\nOS: test");
        Assert(File.Exists(zipPath), "Zip laporan bug harus dibuat.");

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert(entries.Contains("info.txt"), "Laporan harus memuat info.txt.");
        Assert(entries.Contains("logs/codexbar-20260101.log") && entries.Contains("logs/codexbar-20260102.log"),
            "Laporan harus memuat semua file .log.");
        Assert(!entries.Any(name => name.EndsWith("ignore.txt", StringComparison.Ordinal)),
            "Laporan hanya memuat file .log, bukan file lain.");

        Console.WriteLine("PASS bug report tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

static int RunCredentialStoreTests()
{
    var target = $"CodexBar-tests-{Guid.NewGuid():N}";
    try
    {
        Assert(CredentialStore.TryRead(target) is null, "Target belum tersimpan harus mengembalikan null.");

        Assert(CredentialStore.Save(target, "s3cr3t-value"), "Save harus berhasil.");
        Assert(CredentialStore.TryRead(target) == "s3cr3t-value", "Read harus mengembalikan nilai yang sama persis.");

        Assert(CredentialStore.Save(target, "overwritten-value"), "Save ulang (overwrite) harus berhasil.");
        Assert(CredentialStore.TryRead(target) == "overwritten-value", "Overwrite harus menggantikan nilai lama.");

        Assert(CredentialStore.Delete(target), "Delete atas target yang ada harus berhasil.");
        Assert(CredentialStore.TryRead(target) is null, "Setelah delete, read harus null.");

        // Round-trip a realistic API-key-shaped secret (mixed charset, moderate length).
        var apiKeyShaped = "sk-ant-" + string.Concat(Enumerable.Repeat("aB3-_xZ9", 12));
        Assert(CredentialStore.Save(target, apiKeyShaped), "Save API key panjang harus berhasil.");
        Assert(CredentialStore.TryRead(target) == apiKeyShaped, "API key panjang harus round-trip persis.");

        Console.WriteLine("PASS credential store tests");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {exception.Message}");
        return 1;
    }
    finally
    {
        CredentialStore.Delete(target);
    }
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
    var child = GetChildProcess(provider);
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

static Process GetChildProcess(CodexProvider provider)
{
    var transport = typeof(CodexProvider).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(provider) as ProcessCodexTransport;
    return transport?.Process ?? throw new InvalidOperationException("Child Codex tidak ditemukan.");
}

sealed class CannedSseHandler(string sse) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
}

sealed class FakeProvider : IAgentProvider
{
    public string Id => ProviderId;
    public string DisplayName => "Fake";
    public ProviderCapabilities Capabilities { get; } = new(true, true, true, true, false, true);
    public bool IsInitialized { get; private set; }
    public bool FailStart { get; set; }
    public bool FailSend { get; set; }
    public bool FailInitialize { get; set; }
    public bool Disposed { get; private set; }
    public string ProviderId { get; init; } = "fake";
    public int InitializeCount { get; private set; }
    public int StartCount { get; private set; }

    public event EventHandler<ProviderEvent>? EventReceived;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (FailInitialize)
        {
            return Task.FromException(new InvalidOperationException("initialize gagal"));
        }

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

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsInitialized = false;
        return ValueTask.CompletedTask;
    }

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

sealed class TestCodexTransport : ICodexTransport
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _error = Channel.CreateUnbounded<string>();
    private readonly List<string> _writtenLines = [];

    public Func<JsonElement, string?>? Responder { get; init; }
    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> WrittenLines
    {
        get
        {
            lock (_writtenLines)
            {
                return _writtenLines.ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsRunning = true;
        return Task.CompletedTask;
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Test transport tidak berjalan.");
        }

        lock (_writtenLines)
        {
            _writtenLines.Add(line);
        }

        using var document = JsonDocument.Parse(line);
        if (Responder?.Invoke(document.RootElement) is { } response)
        {
            await _output.Writer.WriteAsync(response, cancellationToken);
        }
    }

    public ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
        ReadLineAsync(_output.Reader, cancellationToken);

    public ValueTask<string?> ReadErrorLineAsync(CancellationToken cancellationToken) =>
        ReadLineAsync(_error.Reader, cancellationToken);

    public Task StopAsync()
    {
        IsRunning = false;
        _output.Writer.TryComplete();
        _error.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public void Disconnect()
    {
        IsRunning = false;
        _output.Writer.TryComplete();
        _error.Writer.TryComplete();
    }

    private static async ValueTask<string?> ReadLineAsync(
        ChannelReader<string> reader,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            if (reader.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }
}
