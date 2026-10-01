using QRCoder;

namespace LocalBridge.Agent;

internal sealed class QrTransferForm : Form
{
    private readonly WebTransferSession _session;
    private readonly Label _remaining = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public QrTransferForm(WebTransferSession session)
    {
        _session = session;
        Text = "LocalBridge - iPhoneで受け取る";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(410, 590);
        MinimumSize = new Size(410, 590);
        MaximizeBox = false;
        TopMost = true;
        Icon = SystemIcons.Application;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 1,
            RowCount = 8,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "iPhoneのカメラで読み取る",
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 18, FontStyle.Bold),
        });
        root.Controls.Add(new Label
        {
            Text = "Safariに保存方法の選択画面が開きます。読み取っただけではダウンロードしません。",
            AutoSize = true,
            MaximumSize = new Size(350, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            ForeColor = Color.DimGray,
        });

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(session.QrUrl, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        using var source = new MemoryStream(code.GetGraphic(10, drawQuietZones: true));
        using var loaded = new Bitmap(source);
        var picture = new PictureBox
        {
            Image = new Bitmap(loaded),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(300, 300),
            Anchor = AnchorStyles.None,
        };
        root.Controls.Add(picture);

        var fileText = string.Join(Environment.NewLine, session.Items.Take(4).Select(item => "・" + item.DisplayName));
        if (session.Items.Count > 4) fileText += Environment.NewLine + $"ほか{session.Items.Count - 4}件";
        root.Controls.Add(new Label
        {
            Text = fileText,
            AutoSize = true,
            MaximumSize = new Size(350, 70),
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleLeft,
        });
        _remaining.Anchor = AnchorStyles.None;
        root.Controls.Add(_remaining);

        var close = new Button { Text = "QRコードを閉じる", AutoSize = true, Anchor = AnchorStyles.None };
        close.Click += (_, _) => Close();
        root.Controls.Add(close);

        _timer.Tick += (_, _) => UpdateRemaining();
        _session.EndRequested += OnEndRequested;
        Shown += (_, _) => { UpdateRemaining(); _timer.Start(); };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            _session.EndRequested -= OnEndRequested;
            _session.Cancel();
            picture.Image?.Dispose();
        };
    }

    private void UpdateRemaining()
    {
        var remaining = _session.ExpiresAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            Close();
            return;
        }
        _remaining.Text = $"有効期限：あと{Math.Ceiling(remaining.TotalMinutes):0}分";
    }

    private void OnEndRequested()
    {
        if (IsDisposed) return;
        try { BeginInvoke(Close); } catch (InvalidOperationException) { }
    }
}
