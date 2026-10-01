using LocalBridge.Agent;
using LocalBridge.WindowsShared;
using System.Text.Json;

var requestFile = args.Length == 2 && args[0] == "--request-file" ? args[1] : null;
var initialRequest = requestFile is null ? null : ReadRequest(requestFile);
using var mutex = new Mutex(initiallyOwned: true, @"Local\LocalBridge.Agent", out var firstInstance);
if (!firstInstance)
{
    if (initialRequest is not null)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await IpcClient.SendAsync(initialRequest, CancellationToken.None);
                if (requestFile is not null && File.Exists(requestFile)) File.Delete(requestFile);
                break;
            }
            catch (Exception error) when (error is IOException or TimeoutException)
            {
                await Task.Delay(200);
            }
        }
    }
    return;
}

if (initialRequest is not null && requestFile is not null && File.Exists(requestFile))
{
    File.Delete(requestFile);
}
ApplicationConfiguration.Initialize();
Application.Run(new AgentApplicationContext(initialRequest));

static SendRequest? ReadRequest(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<SendRequest>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
    {
        return null;
    }
}
