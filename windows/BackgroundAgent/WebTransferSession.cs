using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LocalBridge.WindowsShared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalBridge.Agent;

internal sealed record WebTransferItem(
    int Id,
    string SourcePath,
    string DisplayName,
    string MimeType,
    bool IsPhotoOrVideo,
    bool IsVideo,
    bool DeleteAfterSession,
    long Size);

internal sealed class WebTransferSession : IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly object _authorizationLock = new();
    private readonly string _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private readonly ConcurrentDictionary<int, byte> _accessedItems = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IPAddress? _bindAddress;
    private WebApplication? _application;
    private IPAddress? _authorizedClient;
    private int _ended;

    public WebTransferSession(IEnumerable<string> paths, IPAddress? bindAddress = null)
    {
        _bindAddress = bindAddress;
        Items = PrepareItems(paths);
        if (Items.Count == 0) throw new InvalidDataException("送信するファイルがありません。");
        ExpiresAt = DateTimeOffset.Now.Add(Lifetime);
    }

    public IReadOnlyList<WebTransferItem> Items { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string QrUrl { get; private set; } = string.Empty;
    public int AccessedCount => _accessedItems.Count;
    public bool WasAccessed(int id) => _accessedItems.ContainsKey(id);
    public event Action? EndRequested;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var address = _bindAddress ?? FindPrivateIpv4Address();
        var port = ReservePort(address);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebTransferSession).Assembly.FullName,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));
        var app = builder.Build();
        Configure(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _application = app;
        QrUrl = $"http://{address}:{port}/s/{_token}/";
        _ = ExpireAsync();
    }

    public void Cancel() => RequestEnd();

    public async ValueTask DisposeAsync()
    {
        RequestEnd();
        _lifetime.Cancel();
        if (_application is not null)
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _application.StopAsync(stopTimeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await _application.DisposeAsync().ConfigureAwait(false);
        }
        _lifetime.Dispose();
        foreach (var item in Items.Where(item => item.DeleteAfterSession))
        {
            try { if (File.Exists(item.SourcePath)) File.Delete(item.SourcePath); } catch (IOException) { }
        }
    }

    private void Configure(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            await next().ConfigureAwait(false);
        });

        app.MapGet("/s/{token}/", (HttpContext context, string token) =>
        {
            if (!Authorize(context, token)) return Results.NotFound();
            return Results.Content(BuildLandingPage(), "text/html; charset=utf-8", Encoding.UTF8);
        });

        app.MapGet("/s/{token}/preview/{id:int}", (HttpContext context, string token, int id) =>
        {
            if (!Authorize(context, token) || FindItem(id) is not { IsPhotoOrVideo: true } item) return Results.NotFound();
            return Results.Content(BuildPreviewPage(item), "text/html; charset=utf-8", Encoding.UTF8);
        });

        app.MapGet("/s/{token}/content/{id:int}", (HttpContext context, string token, int id) =>
        {
            if (!Authorize(context, token) || FindItem(id) is not { IsPhotoOrVideo: true } item) return Results.NotFound();
            MarkAccessed(context, item.Id);
            return Results.File(item.SourcePath, item.MimeType, enableRangeProcessing: true);
        });

        app.MapGet("/s/{token}/download/{id:int}", (HttpContext context, string token, int id) =>
        {
            if (!Authorize(context, token) || FindItem(id) is not { } item) return Results.NotFound();
            MarkAccessed(context, item.Id);
            return Results.File(item.SourcePath, item.MimeType, item.DisplayName, enableRangeProcessing: true);
        });

        app.MapPost("/s/{token}/complete", (HttpContext context, string token) =>
        {
            if (!Authorize(context, token)) return Results.NotFound();
            context.Response.OnCompleted(() =>
            {
                RequestEnd();
                return Task.CompletedTask;
            });
            const string html = "<!doctype html><html lang=\"ja\"><meta name=\"viewport\" content=\"width=device-width\"><meta charset=\"utf-8\"><title>LocalBridge</title><style>body{font-family:-apple-system,sans-serif;text-align:center;padding:64px 24px;color:#17352c}h1{font-size:28px}</style><h1>受信を終了しました</h1><p>このページを閉じて大丈夫です。</p></html>";
            return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
        });
    }

    private bool Authorize(HttpContext context, string token)
    {
        if (DateTimeOffset.Now > ExpiresAt || !FixedTimeTokenEquals(token)) return false;
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null) return false;
        lock (_authorizationLock)
        {
            _authorizedClient ??= remote;
            return _authorizedClient.Equals(remote);
        }
    }

    private bool FixedTimeTokenEquals(string candidate)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(_token);
        var candidateBytes = Encoding.ASCII.GetBytes(candidate);
        return expectedBytes.Length == candidateBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private void MarkAccessed(HttpContext context, int id)
    {
        context.Response.OnCompleted(() =>
        {
            _accessedItems.TryAdd(id, 0);
            return Task.CompletedTask;
        });
    }

    private WebTransferItem? FindItem(int id) => Items.FirstOrDefault(item => item.Id == id);

    private string BuildLandingPage()
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><title>LocalBridgeで受け取る</title>
            <style>
            :root{color-scheme:light;background:#f4f8f6;color:#17352c;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}body{margin:0;padding:calc(24px + env(safe-area-inset-top)) 18px 48px}main{max-width:680px;margin:auto}h1{font-size:30px;margin:8px 0}.lead{color:#567067;line-height:1.6}.card{background:#fff;border:1px solid #dce9e4;border-radius:18px;padding:18px;margin:16px 0;box-shadow:0 8px 24px #174b3812}.name{font-weight:700;font-size:18px;overflow-wrap:anywhere}.meta{color:#6a7f77;font-size:14px;margin:6px 0 14px}.actions{display:grid;gap:10px}.button,button{display:block;box-sizing:border-box;width:100%;border:0;border-radius:13px;padding:14px 16px;text-align:center;font:inherit;font-weight:700;text-decoration:none;background:#16755b;color:white}.secondary{background:#e8f2ee;color:#135943}.finish{margin-top:28px;background:#344b44}.note{font-size:13px;color:#6a7f77;line-height:1.5}.badge{display:inline-block;border-radius:999px;background:#dff3ea;color:#126047;padding:5px 10px;font-size:12px;font-weight:700}</style></head><body><main>
            <span class="badge">同じWi-Fi内だけで転送</span><h1>保存方法を選んでください</h1>
            <p class="lead">この画面を開いただけではダウンロードしません。写真・動画は写真アプリ用とFiles用から選べます。</p>
            """);
        foreach (var item in Items)
        {
            var name = HtmlEncoder.Default.Encode(item.DisplayName);
            html.Append($"<section class=\"card\"><div class=\"name\">{name}</div><div class=\"meta\">{FormatSize(item.Size)}</div><div class=\"actions\">");
            if (item.IsPhotoOrVideo)
            {
                html.Append($"<a class=\"button\" href=\"preview/{item.Id}\">写真アプリに保存する</a>");
                html.Append($"<a class=\"button secondary\" href=\"download/{item.Id}\">ファイルアプリに保存する</a>");
            }
            else
            {
                html.Append($"<a class=\"button\" href=\"download/{item.Id}\">ファイルアプリに保存する</a>");
            }
            html.Append("</div></section>");
        }
        if (Items.Any(item => item.IsVideo))
        {
            html.Append("<p class=\"note\">動画は、次の画面でダウンロードしてから動画ファイルを開き、その共有メニューで「ビデオを保存」を選びます。</p>");
        }
        if (Items.Any(item => item.IsPhotoOrVideo && !item.IsVideo))
        {
            html.Append("<p class=\"note\">写真は、次のプレビュー画面から「画像を保存」を選びます。</p>");
        }
        html.Append($"<form method=\"post\" action=\"/s/{_token}/complete\"><button class=\"finish\" type=\"submit\">受信を終了する</button></form></main></body></html>");
        return html.ToString();
    }

    private string BuildPreviewPage(WebTransferItem item)
    {
        // Sharing an HTML page containing <video> shares the page URL in Safari,
        // not the movie. LAN HTTP cannot use the secure-context Web Share API.
        if (item.IsVideo) return BuildVideoSavePage(item);

        var name = HtmlEncoder.Default.Encode(item.DisplayName);
        var media = $"<img src=\"../content/{item.Id}\" alt=\"{name}\">";
        const string saveLabel = "画像を保存";
        return $$"""
            <!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><title>{{name}}</title>
            <style>:root{color-scheme:dark;background:#101815;color:#fff;font-family:-apple-system,sans-serif}body{margin:0;padding:calc(18px + env(safe-area-inset-top)) 16px 40px;text-align:center}main{max-width:720px;margin:auto}h1{font-size:21px;overflow-wrap:anywhere}.guide{background:#23342e;border-radius:16px;padding:14px;line-height:1.55;margin:16px 0}img,video{display:block;max-width:100%;max-height:62vh;margin:18px auto;border-radius:12px;background:#000}a{color:#8de0bf}</style></head><body><main>
            <h1>{{name}}</h1><div class="guide">画面下のSafari共有ボタン <strong>□↑</strong> を押し、<strong>「{{saveLabel}}」</strong>を選んでください。</div>{{media}}<p><a href="../">保存方法の選択へ戻る</a></p></main></body></html>
            """;
    }

    private static string BuildVideoSavePage(WebTransferItem item)
    {
        var name = HtmlEncoder.Default.Encode(item.DisplayName);
        return $$"""
            <!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><title>動画を写真アプリに保存</title>
            <style>:root{color-scheme:dark;background:#101815;color:#fff;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}body{margin:0;padding:calc(18px + env(safe-area-inset-top)) 16px 40px}main{max-width:720px;margin:auto}h1{font-size:24px;line-height:1.4}.name{overflow-wrap:anywhere;font-weight:700}.meta,.note{color:#b6cdc2;font-size:14px;line-height:1.6}.guide{background:#23342e;border-radius:16px;padding:18px;margin:20px 0}ol{margin:0;padding-left:24px}li{padding-left:4px;line-height:1.6}li+li{margin-top:22px}li p{margin:6px 0 0}a{color:#8de0bf}.button{display:block;box-sizing:border-box;background:#16755b;color:#fff;border-radius:13px;padding:15px 12px;margin-top:12px;text-align:center;font-weight:700;text-decoration:none}.back{display:inline-block;padding:10px 0}summary{cursor:pointer;font-weight:700}details p{line-height:1.6}</style></head><body><main>
            <h1>動画を写真アプリに保存</h1><p class="name">{{name}}</p><p class="meta">{{FormatSize(item.Size)}}</p>
            <p class="note">まず動画をiPhoneにダウンロードし、開いた動画の共有メニューから写真アプリへ保存します。</p>
            <div class="guide"><ol>
            <li><strong>動画をダウンロードする</strong><p>下のボタンを押し、確認が出たら「ダウンロード」を選んで、完了するまで待ちます。</p><a class="button" href="../download/{{item.Id}}" download="{{name}}">動画をダウンロード</a></li>
            <li><strong>ダウンロードした動画を開く</strong><p>Safariのアドレス欄付近にあるダウンロードボタン <strong>↓</strong> から「ダウンロード」を開き、<span class="name">{{name}}</span> を押します。</p></li>
            <li><strong>動画の共有から「ビデオを保存」</strong><p>開いた動画の共有ボタン <strong>□↑</strong> を押し、メニューを下へスクロールして<strong>「ビデオを保存」</strong>を選びます。</p></li>
            </ol></div>
            <details><summary>「ビデオを保存」が見つからないとき</summary><p>この案内ページの共有ボタンでは、動画を保存できません。「ファイル」アプリでダウンロードした動画を長押しし、「共有」→「ビデオを保存」を試してください。</p><p>動画は「ファイル」アプリの「最近使った項目」や、Safariで設定したダウンロード先にあります。動画を再生できない場合は、iPhoneが対応する動画形式への変換が必要なことがあります。</p></details>
            <p class="note">ダウンロードが完了するまで、WindowsのQR画面を開いたままにしてください。</p><a class="back" href="../">保存方法の選択へ戻る</a></main></body></html>
            """;
    }

    private async Task ExpireAsync()
    {
        try
        {
            await Task.Delay(Lifetime, _lifetime.Token).ConfigureAwait(false);
            RequestEnd();
        }
        catch (OperationCanceledException) { }
    }

    private void RequestEnd()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        EndRequested?.Invoke();
    }

    private static IReadOnlyList<WebTransferItem> PrepareItems(IEnumerable<string> paths)
    {
        var output = new List<WebTransferItem>();
        foreach (var source in paths)
        {
            var fullPath = Path.GetFullPath(source);
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                var mime = GetMimeType(file.Extension);
                output.Add(new WebTransferItem(output.Count, fullPath, file.Name, mime, IsMedia(mime), mime.StartsWith("video/", StringComparison.Ordinal), false, file.Length));
                continue;
            }
            if (!Directory.Exists(fullPath)) throw new FileNotFoundException("選択したファイルまたはフォルダーが見つかりません。", fullPath);
            AppPaths.EnsureDirectories();
            var directory = new DirectoryInfo(fullPath);
            var temporary = Path.Combine(AppPaths.TempDirectory, Guid.NewGuid().ToString("N") + ".zip");
            ZipFile.CreateFromDirectory(fullPath, temporary, CompressionLevel.NoCompression, includeBaseDirectory: true);
            var archive = new FileInfo(temporary);
            var displayName = (string.IsNullOrWhiteSpace(directory.Name) ? "受信フォルダー" : directory.Name) + ".zip";
            output.Add(new WebTransferItem(output.Count, temporary, displayName, "application/zip", false, false, true, archive.Length));
        }
        return output;
    }

    private static IPAddress FindPrivateIpv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(network => new
            {
                HasGateway = network.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)),
                Addresses = network.GetIPProperties().UnicastAddresses.Select(value => value.Address),
            })
            .OrderByDescending(candidate => candidate.HasGateway)
            .SelectMany(candidate => candidate.Addresses)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .ToArray();
        return candidates.FirstOrDefault(IsPrivateIpv4)
            ?? candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("同じWi-Fiで使えるIPv4アドレスが見つかりません。");
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static int ReservePort(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static bool IsMedia(string mimeType) => mimeType.StartsWith("image/", StringComparison.Ordinal) || mimeType.StartsWith("video/", StringComparison.Ordinal);

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        ".webp" => "image/webp",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
