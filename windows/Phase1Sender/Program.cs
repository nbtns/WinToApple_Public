using LocalBridge.Protocol;

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    var options = ParseArguments(args);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    string host;
    int port;
    if (options.Host is not null)
    {
        host = options.Host;
        port = options.Port ?? throw new ArgumentException("--hostを使う場合は--portも指定してください。");
        Console.WriteLine($"診断用の指定先へ接続します: {host}:{port}");
    }
    else
    {
        Console.WriteLine("同じWi-FiからiPhoneを探しています…");
        var service = await new MdnsDiscovery().FindAsync(TimeSpan.FromSeconds(8), cancellation.Token);
        if (service is null)
        {
            Console.Error.WriteLine("iPhoneが見つかりません。iPhoneでLocalBridgeを開き、同じWi-Fiとローカルネットワーク許可を確認してください。");
            return 2;
        }

        host = service.Address.ToString();
        port = service.Port;
        Console.WriteLine($"見つかりました: {service.InstanceName} ({host}:{port})");
    }

    var lastPercent = -1;
    var progress = new Progress<(long Sent, long Total)>(value =>
    {
        var percent = value.Total == 0 ? 100 : (int)(value.Sent * 100 / value.Total);
        if (percent != lastPercent)
        {
            lastPercent = percent;
            Console.Write($"\r送信中: {percent,3}%");
        }
    });

    var result = await new FileSender().SendAsync(options.FilePath, host, port, progress, cancellation.Token);
    Console.WriteLine();
    if (!result.Success)
    {
        Console.Error.WriteLine($"送信に失敗しました: {result.Error ?? "iPhoneで拒否されました。"}");
        return 3;
    }

    Console.WriteLine($"送信が完了しました。iPhoneでの保存名: {result.SavedName}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("送信を中止しました。");
    return 130;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine($"エラー: {exception.Message}");
    return 1;
}

static Options ParseArguments(string[] arguments)
{
    if (arguments.Length == 0 || arguments[0] is "-h" or "--help")
    {
        throw new ArgumentException("使い方: LocalBridge.Phase1Sender <file> [--host <ip-or-host>] [--port <port>]");
    }

    string? host = null;
    int? port = null;
    for (var index = 1; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--host" when index + 1 < arguments.Length:
                host = arguments[++index];
                break;
            case "--port" when index + 1 < arguments.Length && int.TryParse(arguments[++index], out var parsedPort) && parsedPort is > 0 and <= 65535:
                port = parsedPort;
                break;
            default:
                throw new ArgumentException($"不明または不正な引数です: {arguments[index]}");
        }
    }

    return new Options(Path.GetFullPath(arguments[0]), host, port);
}

internal sealed record Options(string FilePath, string? Host, int? Port);

