using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using LocalBridge.Agent;
using QRCoder;

var temporary = Path.Combine(Path.GetTempPath(), "LocalBridge-WebTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var imagePath = Path.Combine(temporary, "写真テスト.jpg");
var textPath = Path.Combine(temporary, "メモ.txt");
await File.WriteAllBytesAsync(imagePath, [0xff, 0xd8, 0xff, 0xe0, 0x01, 0x02, 0x03, 0x04]);
await File.WriteAllTextAsync(textPath, "LocalBridge web transfer test", Encoding.UTF8);
// Transport fixtures only: codec playback and the iOS share sheet need a real iPhone.
var videoBytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
var videos = new[]
{
    (Name: "動画 & 家族 🎬.mp4", Mime: "video/mp4"),
    (Name: "撮影テスト.MOV", Mime: "video/quicktime"),
    (Name: "編集済み.m4v", Mime: "video/mp4"),
};
var videoPaths = videos.Select(video => Path.Combine(temporary, video.Name)).ToArray();
foreach (var videoPath in videoPaths) await File.WriteAllBytesAsync(videoPath, videoBytes);

try
{
    await using var session = new WebTransferSession([imagePath, textPath, .. videoPaths], IPAddress.Loopback);
    await session.StartAsync(CancellationToken.None);
    using (var generator = new QRCodeGenerator())
    using (var qrData = generator.CreateQrCode(session.QrUrl, QRCodeGenerator.ECCLevel.Q))
    using (var qr = new PngByteQRCode(qrData))
    {
        var png = qr.GetGraphic(4);
        var pngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        AssertEqual(true, png.AsSpan(0, 8).SequenceEqual(pngSignature));
    }
    using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
    using var client = new HttpClient(handler);

    var landing = await client.GetStringAsync(session.QrUrl);
    AssertContains("保存方法を選んでください", landing);
    AssertContains("写真アプリに保存する", landing);
    AssertContains("ファイルアプリに保存する", landing);
    AssertEqual(0, session.AccessedCount);

    var preview = await client.GetStringAsync(session.QrUrl + "preview/0");
    AssertContains("画像を保存", preview);
    AssertContains("../content/0", preview);
    using var invalidPreview = await client.GetAsync(session.QrUrl + "preview/1");
    AssertEqual(HttpStatusCode.NotFound, invalidPreview.StatusCode);

    for (var index = 0; index < videos.Length; index++)
    {
        var id = index + 2;
        var video = videos[index];
        AssertContains($"href=\"preview/{id}\"", landing);
        var savePageUrl = new Uri(session.QrUrl + $"preview/{id}");
        using var savePageResponse = await client.GetAsync(savePageUrl);
        var savePage = await savePageResponse.Content.ReadAsStringAsync();
        AssertEqual("text/html", savePageResponse.Content.Headers.ContentType?.MediaType ?? string.Empty);
        AssertContains("動画をダウンロード", savePage);
        AssertContains("ビデオを保存", savePage);
        AssertContains("開いた動画の共有ボタン", savePage);
        AssertEqual(false, savePage.Contains("<video", StringComparison.Ordinal));
        AssertEqual(false, savePage.Contains("../content/", StringComparison.Ordinal));
        AssertEqual(false, savePage.Contains("navigator.share", StringComparison.Ordinal));
        AssertEqual(false, session.WasAccessed(id));

        // Follow the actual primary action. It must deliver the video attachment,
        // not an HTML page or a URL to share, including when the name needs escaping.
        var downloadLink = Regex.Match(savePage, "href=\"([^\"]+)\" download=\"([^\"]+)\"");
        AssertEqual(true, downloadLink.Success);
        AssertEqual(video.Name, WebUtility.HtmlDecode(downloadLink.Groups[2].Value));
        AssertContains(HtmlEncoder.Default.Encode(video.Name), savePage);
        var downloadUrl = new Uri(savePageUrl, WebUtility.HtmlDecode(downloadLink.Groups[1].Value));
        using var videoResponse = await client.GetAsync(downloadUrl);
        AssertEqual(HttpStatusCode.OK, videoResponse.StatusCode);
        AssertEqual(video.Mime, videoResponse.Content.Headers.ContentType?.MediaType ?? string.Empty);
        AssertEqual("attachment", videoResponse.Content.Headers.ContentDisposition?.DispositionType ?? string.Empty);
        AssertEqual(video.Name, videoResponse.Content.Headers.ContentDisposition?.FileNameStar ?? string.Empty);
        var downloadedVideo = await videoResponse.Content.ReadAsByteArrayAsync();
        AssertEqual(true, videoBytes.SequenceEqual(downloadedVideo));
        await AssertAccessedAsync(session, id);

        using var videoRangeRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        videoRangeRequest.Headers.Range = new RangeHeaderValue(32, 63);
        using var videoRangeResponse = await client.SendAsync(videoRangeRequest);
        AssertEqual(HttpStatusCode.PartialContent, videoRangeResponse.StatusCode);
        AssertEqual("bytes 32-63/256", videoRangeResponse.Content.Headers.ContentRange?.ToString() ?? string.Empty);
        var downloadedRange = await videoRangeResponse.Content.ReadAsByteArrayAsync();
        AssertEqual(true, videoBytes[32..64].SequenceEqual(downloadedRange));

        using var invalidVideo = await client.GetAsync(downloadUrl.AbsoluteUri.Replace("/s/", "/s/invalid-"));
        AssertEqual(HttpStatusCode.NotFound, invalidVideo.StatusCode);
    }
    using var mediaResponse = await client.GetAsync(session.QrUrl + "content/0");
    AssertEqual(HttpStatusCode.OK, mediaResponse.StatusCode);
    AssertEqual("image/jpeg", mediaResponse.Content.Headers.ContentType?.MediaType ?? string.Empty);

    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, session.QrUrl + "download/1");
    rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
    using var rangeResponse = await client.SendAsync(rangeRequest);
    AssertEqual(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
    AssertEqual(4, (await rangeResponse.Content.ReadAsByteArrayAsync()).Length);
    await AssertAccessedAsync(session, 1);

    var invalidUrl = session.QrUrl.Replace("/s/", "/s/invalid-");
    using var invalid = await client.GetAsync(invalidUrl);
    AssertEqual(HttpStatusCode.NotFound, invalid.StatusCode);

    using var completed = await client.PostAsync(session.QrUrl + "complete", null);
    AssertEqual(HttpStatusCode.OK, completed.StatusCode);
    Console.WriteLine("PASS: QR landing page does not auto-download");
    Console.WriteLine("PASS: one-time URL renders as a PNG QR code");
    Console.WriteLine("PASS: photo and Files choices are shown");
    Console.WriteLine("PASS: video save action downloads MP4/MOV/M4V attachments with original UTF-8 filenames");
    Console.WriteLine("PASS: video guide does not fetch media or ask to share the HTML preview");
    Console.WriteLine("PASS: video bytes, HTTP Range resume, and download token rejection");
    Console.WriteLine("PASS: HTTP Range download and token rejection");
}
finally
{
    if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
}

static void AssertContains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal)) throw new Exception($"Expected page to contain: {expected}");
}

static async Task AssertAccessedAsync(WebTransferSession session, int id)
{
    // Kestrel's OnCompleted callback can run just after the client receives the body.
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!session.WasAccessed(id)) await Task.Delay(10, timeout.Token);
}

static void AssertEqual<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected: {expected}; Actual: {actual}");
}
