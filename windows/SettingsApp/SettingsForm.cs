using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using LocalBridge.WindowsShared;

namespace LocalBridge.Settings;

internal sealed class SettingsForm : Form
{
    private readonly Label _receiverStatus = new() { Text = "iPhoneアプリ不要・毎回QRで受信", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), ForeColor = Color.DarkGreen };
    private readonly TextBox _menuTitle = new() { Width = 300 };
    private readonly CheckBox _startWithWindows = new() { Text = "Windowsへのログイン時に自動起動", AutoSize = true };
    private readonly CheckBox _notifications = new() { Text = "送信完了を通知", AutoSize = true };
    private readonly Label _operationStatus = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(500, 0) };

    public SettingsForm()
    {
        Text = "LocalBridge 設定";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 520);
        ClientSize = new Size(620, 520);
        Icon = SystemIcons.Application;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 10,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(Heading("受信方式"));
        root.Controls.Add(_receiverStatus);
        root.Controls.Add(new Label
        {
            Text = "Windowsでファイルを右クリックすると小さなQRコードが表示されます。iPhoneのカメラで読み取り、Safariで保存方法を選びます。MacやiPhoneアプリは不要です。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
        });

        root.Controls.Add(Heading("日常の動作"));
        root.Controls.Add(Labeled("右クリックに表示する名前", _menuTitle));
        root.Controls.Add(_startWithWindows);
        root.Controls.Add(_notifications);

        var actionRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var saveButton = new Button { Text = "設定を保存", AutoSize = true };
        var testButton = new Button { Text = "ファイルを選んでテスト送信", AutoSize = true };
        var diagnosticsButton = new Button { Text = "診断情報を書き出す", AutoSize = true };
        var historyButton = new Button { Text = "送信履歴を開く", AutoSize = true };
        saveButton.Click += (_, _) => SaveSettings();
        testButton.Click += async (_, _) => await SendTestAsync();
        diagnosticsButton.Click += (_, _) => ExportDiagnostics();
        historyButton.Click += (_, _) => OpenDataFolder();
        actionRow.Controls.Add(saveButton);
        actionRow.Controls.Add(testButton);
        actionRow.Controls.Add(diagnosticsButton);
        actionRow.Controls.Add(historyButton);
        root.Controls.Add(actionRow);
        root.Controls.Add(_operationStatus);

        LoadState();
        SettingsStore.Save(SettingsStore.Load());
        EnsureAgentRunning();
    }

    private void LoadState()
    {
        var settings = SettingsStore.Load();
        _menuTitle.Text = settings.MenuTitle;
        _startWithWindows.Checked = settings.StartWithWindows;
        _notifications.Checked = settings.CompletionNotifications;
    }

    private void SaveSettings()
    {
        var settings = new AppSettings(
            _menuTitle.Text,
            _startWithWindows.Checked,
            SettingsStore.Load().RetrySeconds,
            _notifications.Checked);
        SettingsStore.Save(settings);
        if (settings.StartWithWindows) EnsureAgentRunning();
        _operationStatus.Text = "設定を保存しました。右クリック名は次にメニューを開いたときから反映されます。";
    }

    private async Task SendTestAsync()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "iPhoneへテスト送信するファイル" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            EnsureAgentRunning();
            await Task.Delay(400);
            await IpcClient.SendAsync(SendRequest.Create(dialog.FileNames), CancellationToken.None);
            _operationStatus.Text = "QRコードを表示しました。iPhoneのカメラで読み取ってください。";
        }
        catch (Exception error)
        {
            _operationStatus.Text = "テスト送信を開始できません: " + error.Message;
        }
    }

    private void ExportDiagnostics()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "ZIPファイル (*.zip)|*.zip",
            FileName = $"LocalBridge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
            var summary = JsonSerializer.Serialize(new
            {
                createdAt = DateTimeOffset.Now,
                windowsVersion = Environment.OSVersion.VersionString,
                runtime = Environment.Version.ToString(),
                receiverMode = "web-qr",
                settings = SettingsStore.Load(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            var summaryEntry = archive.CreateEntry("summary.json");
            using (var writer = new StreamWriter(summaryEntry.Open())) writer.Write(summary);
            AddIfExists(archive, AppPaths.HistoryFile, "history.jsonl");
            if (Directory.Exists(AppPaths.LogsDirectory))
            {
                foreach (var log in Directory.EnumerateFiles(AppPaths.LogsDirectory, "*.log"))
                    AddIfExists(archive, log, "logs/" + Path.GetFileName(log));
            }
            _operationStatus.Text = "診断情報を書き出しました。秘密鍵や機器登録用の秘密情報は含まれていません。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _operationStatus.Text = "診断情報を書き出せません: " + error.Message;
        }
    }

    private static void AddIfExists(ZipArchive archive, string source, string entryName)
    {
        if (File.Exists(source)) archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
    }

    private static void EnsureAgentRunning()
    {
        if (!File.Exists(AppPaths.AgentExecutable)) return;
        var running = Process.GetProcessesByName("LocalBridge.Agent").Length > 0;
        if (!running) Process.Start(new ProcessStartInfo(AppPaths.AgentExecutable) { UseShellExecute = true });
    }

    private static void OpenDataFolder()
    {
        AppPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 15, FontStyle.Bold),
        Margin = new Padding(0, 18, 0, 8),
    };

    private static Control Labeled(string label, Control control)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
        panel.Controls.Add(control);
        return panel;
    }
}
