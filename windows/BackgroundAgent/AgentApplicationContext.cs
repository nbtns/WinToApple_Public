using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using LocalBridge.Protocol;
using LocalBridge.WindowsShared;

namespace LocalBridge.Agent;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<SendRequest> _queue = Channel.CreateUnbounded<SendRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly NotifyIcon _trayIcon;
    private readonly Control _dispatcher = new();
    private SendRequest? _lastFailedRequest;

    public AgentApplicationContext(SendRequest? initialRequest = null)
    {
        AppPaths.EnsureDirectories();
        _dispatcher.CreateControl();
        var menu = new ContextMenuStrip();
        menu.Items.Add("接続状態", null, (_, _) => ShowStatus());
        menu.Items.Add("設定", null, (_, _) => OpenSettings());
        menu.Items.Add("送信履歴", null, (_, _) => OpenDataFolder());
        menu.Items.Add("再試行", null, (_, _) => RetryLast());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LocalBridge: 待機中",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => OpenSettings();
        _trayIcon.BalloonTipClicked += (_, _) => RetryLast();

        _ = Task.Run(() => PipeLoopAsync(_shutdown.Token));
        _ = Task.Run(() => WorkerLoopAsync(_shutdown.Token));
        if (initialRequest is not null) _queue.Writer.TryWrite(initialRequest);
        DrainPendingRequests();
    }

    protected override void ExitThreadCore()
    {
        _shutdown.Cancel();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _dispatcher.Dispose();
        _shutdown.Dispose();
        base.ExitThreadCore();
    }

    private async Task PipeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    AppPaths.PipeName,
                    PipeDirection.In,
                    4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    1024 * 1024,
                    1024 * 1024);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var lengthBytes = new byte[4];
                await ReadExactlyAsync(pipe, lengthBytes, cancellationToken).ConfigureAwait(false);
                var length = BitConverter.ToInt32(lengthBytes);
                if (length is <= 0 or > 1024 * 1024) throw new InvalidDataException("右クリックからの送信要求が不正です。");
                var payload = new byte[length];
                await ReadExactlyAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<SendRequest>(payload, JsonOptions)
                    ?? throw new InvalidDataException("右クリックからの送信要求を解釈できません。");
                ValidateRequest(request);
                await _queue.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                LogError("IPC", error);
                ShowNotification("送信を開始できません", error.Message, ToolTipIcon.Error);
            }
        }
    }

    private void DrainPendingRequests()
    {
        var pendingDirectory = Path.Combine(AppPaths.DataDirectory, "Pending");
        if (!Directory.Exists(pendingDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(pendingDirectory, "*.json"))
        {
            try
            {
                var request = JsonSerializer.Deserialize<SendRequest>(File.ReadAllBytes(file), JsonOptions);
                if (request is not null)
                {
                    ValidateRequest(request);
                    _queue.Writer.TryWrite(request);
                }
                File.Delete(file);
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                LogError("Pending", error);
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessRequestAsync(request, cancellationToken).ConfigureAwait(false);
                _lastFailedRequest = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _lastFailedRequest = request;
                LogError("Transfer", error);
                ShowNotification("iPhoneへ送信できませんでした", error.Message + "\nクリックすると再試行します。", ToolTipIcon.Error);
            }
        }
    }

    private async Task ProcessRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        ShowNotification("iPhoneで受け取る", "QRコードをカメラで読み取ってください。", ToolTipIcon.Info);
        await using var session = new WebTransferSession(request.Paths);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);

        var formClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            var form = new QrTransferForm(session);
            form.FormClosed += (_, _) => formClosed.TrySetResult();
            form.Show();
            form.Activate();
        });

        await formClosed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in session.Items)
        {
            if (!session.WasAccessed(item.Id)) continue;
            TransferHistory.Append(new TransferHistoryEntry(
                DateTimeOffset.UtcNow,
                item.DisplayName,
                item.Size,
                true,
                "Web QR受信ページからアクセス",
                null,
                "web-qr"));
        }
        UpdateTrayText("LocalBridge: 待機中");
        if (session.AccessedCount > 0 && SettingsStore.Load().CompletionNotifications)
        {
            ShowNotification("iPhoneで受信しました", $"{session.AccessedCount}件のファイルへアクセスしました。", ToolTipIcon.Info);
        }
    }

    private async Task ProcessNativeRequestAsync(SendRequest request, CancellationToken cancellationToken)
    {
        var paired = PairingStore.Load() ?? throw new InvalidOperationException("iPhoneが未登録です。LocalBridgeの設定から機器登録してください。");
        var settings = SettingsStore.Load();
        using var windowsIdentity = WindowsIdentityStore.LoadOrCreate();
        var authenticatedIdentity = new AuthenticatedClientIdentity(
            windowsIdentity.DeviceId,
            windowsIdentity.SigningKey,
            paired.DeviceId,
            paired.PublicKey);

        ShowNotification($"{paired.DisplayName}へ送信", $"{request.Paths.Length}件の送信を開始します。", ToolTipIcon.Info);
        var completed = 0;
        foreach (var sourcePath in request.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = PrepareSource(sourcePath);
            try
            {
                var started = Stopwatch.StartNew();
                TransferResult? result = null;
                Exception? lastError = null;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.RetrySeconds);
                do
                {
                    try
                    {
                        var endpoint = await FindIPhoneAsync(deadline, cancellationToken).ConfigureAwait(false);
                        var progress = new Progress<(long Sent, long Total)>(value => UpdateProgress(prepared.DisplayName, value.Sent, value.Total));
                        result = await new AuthenticatedFileSender().SendAsync(
                            prepared.Path,
                            prepared.IsFolderArchive,
                            prepared.DisplayName,
                            endpoint.Address.ToString(),
                            endpoint.Port,
                            authenticatedIdentity,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception error) when (error is IOException or SocketException or CryptographicException)
                    {
                        lastError = error;
                        if (DateTimeOffset.UtcNow >= deadline) break;
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    }
                } while (DateTimeOffset.UtcNow < deadline);

                if (result is null) throw new IOException("iPhoneが見つからないか、転送が中断されました。iPhoneでLocalBridgeを開いてください。", lastError);
                if (!result.Success) throw new IOException(result.Error ?? "iPhoneがファイルを保存できませんでした。");
                started.Stop();
                completed++;
                var size = new FileInfo(prepared.Path).Length;
                double? speed = started.Elapsed.TotalSeconds <= 0 ? null : size / 1024d / 1024d / started.Elapsed.TotalSeconds;
                TransferHistory.Append(new TransferHistoryEntry(
                    DateTimeOffset.UtcNow,
                    prepared.DisplayName,
                    size,
                    true,
                    null,
                    speed,
                    paired.DeviceId.ToString("N")[..8]));
            }
            catch (Exception error)
            {
                var size = File.Exists(prepared.Path) ? new FileInfo(prepared.Path).Length : 0;
                TransferHistory.Append(new TransferHistoryEntry(
                    DateTimeOffset.UtcNow,
                    prepared.DisplayName,
                    size,
                    false,
                    error.GetType().Name + ": " + error.Message,
                    null,
                    paired.DeviceId.ToString("N")[..8]));
                throw;
            }
            finally
            {
                if (prepared.DeleteAfterSend && File.Exists(prepared.Path)) File.Delete(prepared.Path);
            }
        }

        UpdateTrayText("LocalBridge: 待機中");
        if (settings.CompletionNotifications)
            ShowNotification("送信が完了しました", $"{paired.DisplayName}へ{completed}件送りました。", ToolTipIcon.Info);
    }

    private static PreparedSource PrepareSource(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (File.Exists(fullPath)) return new PreparedSource(fullPath, Path.GetFileName(fullPath), false, false);
        if (!Directory.Exists(fullPath)) throw new FileNotFoundException("選択したファイルまたはフォルダが見つかりません。", fullPath);

        AppPaths.EnsureDirectories();
        var directoryName = new DirectoryInfo(fullPath).Name;
        var temporary = Path.Combine(AppPaths.TempDirectory, Guid.NewGuid().ToString("N") + ".localbridge-folder.zip");
        ZipFile.CreateFromDirectory(fullPath, temporary, CompressionLevel.NoCompression, includeBaseDirectory: true);
        return new PreparedSource(temporary, directoryName + ".localbridge-folder.zip", true, true);
    }

    private static async Task<DiscoveredService> FindIPhoneAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var service = await new MdnsDiscovery().FindAsync(
                TimeSpan.FromSeconds(Math.Clamp(remaining.TotalSeconds, 1, 4)),
                cancellationToken).ConfigureAwait(false);
            if (service is not null) return service;
        }
        throw new IOException("登録済みiPhoneが見つかりません。同じWi-FiでiPhoneのLocalBridgeを開いてください。");
    }

    private static void ValidateRequest(SendRequest request)
    {
        if (request.Paths is not { Length: > 0 and <= 256 }) throw new InvalidDataException("一度に送れる項目は1〜256件です。");
        if (DateTimeOffset.UtcNow - request.CreatedAt > TimeSpan.FromMinutes(10)) throw new InvalidDataException("古い送信要求を拒否しました。");
        foreach (var path in request.Paths)
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                throw new FileNotFoundException("選択した項目が見つかりません。", path);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private void RetryLast()
    {
        var request = _lastFailedRequest;
        if (request is not null) _queue.Writer.TryWrite(request with { CreatedAt = DateTimeOffset.UtcNow });
    }

    private void ShowStatus()
    {
        ShowNotification("LocalBridgeの状態", "Web QR受信モードです。右クリック後に表示されるQRをiPhoneで読み取ります。", ToolTipIcon.Info);
    }

    private static void OpenSettings()
    {
        if (!File.Exists(AppPaths.SettingsExecutable)) return;
        Process.Start(new ProcessStartInfo(AppPaths.SettingsExecutable) { UseShellExecute = true });
    }

    private static void OpenDataFolder()
    {
        AppPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private void UpdateProgress(string fileName, long sent, long total)
    {
        var percent = total <= 0 ? 100 : (int)Math.Clamp(sent * 100 / total, 0, 100);
        UpdateTrayText($"送信中 {percent}%: {fileName}");
    }

    private void UpdateTrayText(string text)
    {
        RunOnUiThread(() =>
        {
            try { _trayIcon.Text = text[..Math.Min(text.Length, 63)]; } catch (InvalidOperationException) { }
        });
    }

    private void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        RunOnUiThread(() =>
        {
            try
            {
                _trayIcon.BalloonTipTitle = title;
                _trayIcon.BalloonTipText = message;
                _trayIcon.BalloonTipIcon = icon;
                _trayIcon.ShowBalloonTip(5000);
            }
            catch (InvalidOperationException) { }
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcher.IsDisposed) return;
        if (_dispatcher.InvokeRequired)
        {
            try { _dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { }
        }
        else
        {
            action();
        }
    }

    private static void LogError(string area, Exception error)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var logPath = Path.Combine(AppPaths.LogsDirectory, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} [{area}] {error.GetType().Name}: {error.Message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PreparedSource(string Path, string DisplayName, bool IsFolderArchive, bool DeleteAfterSend);
}
