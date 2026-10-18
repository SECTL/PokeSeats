using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PokeSeats.UI;

/// <summary>
/// Best-effort installation statistics for SECTL, using the configured platform ID for
/// version reports and the platform document ID (Client ID) for online heartbeats.
/// </summary>
internal sealed class SectlStatsReporter : IDisposable
{
    private const string ApiBaseUrl = "https://appwrite.sectl.cn";
    private const string DeviceTypeWindows = "windows-desktop";
    private const string DeviceTypeMacOs = "macos-desktop";
    private const string DeviceTypeLinux = "linux-desktop";
    private const string DeviceTypeUnknown = "unknown-desktop";

    private static readonly Uri VersionUri = new($"{ApiBaseUrl}/api/stats/version");
    private static readonly Uri OnlineUri = new($"{ApiBaseUrl}/api/stats/online");
    private static readonly TimeSpan OnlineInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppConfigStore _config;
    private readonly HttpClient _httpClient = new() { Timeout = RequestTimeout };
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private bool _disposed;

    public SectlStatsReporter(AppConfigStore config)
    {
        _config = config;
    }

    public void Start()
    {
        if (_disposed || _worker is not null || string.IsNullOrWhiteSpace(_config.SectlPlatformId))
            return;

        _worker = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stop.Cancel();
        _httpClient.CancelPendingRequests();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Reporting is best-effort and must never make application shutdown fail.
        }
        _httpClient.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReportVersionAsync(cancellationToken).ConfigureAwait(false);
            await ReportOnlineAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(OnlineInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await ReportOnlineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SECTL statistics reporter stopped: {exception.Message}");
        }
    }

    private async Task ReportVersionAsync(CancellationToken cancellationToken)
    {
        string? platformId = _config.SectlPlatformId;
        if (string.IsNullOrWhiteSpace(platformId))
            return;

        var payload = new VersionPayload(
            platformId,
            GetApplicationVersion(),
            _config.DeviceId.ToString("D").ToLowerInvariant());

        await PostAsync(VersionUri, payload, "version", cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportOnlineAsync(CancellationToken cancellationToken)
    {
        string? platformId = _config.SectlClientId;
        if (string.IsNullOrWhiteSpace(platformId))
            return;

        var payload = new OnlinePayload(
            platformId,
            _config.DeviceId.ToString("D").ToLowerInvariant(),
            DetectDeviceType());

        await PostAsync(OnlineUri, payload, "online status", cancellationToken).ConfigureAwait(false);
    }

    private async Task PostAsync<T>(Uri uri, T payload, string description, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .PostAsJsonAsync(uri, payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"SECTL {description} report failed: HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"SECTL {description} report failed: {exception.Message}");
        }
    }

    private static string GetApplicationVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string DetectDeviceType()
    {
        if (OperatingSystem.IsWindows())
            return DeviceTypeWindows;
        if (OperatingSystem.IsMacOS())
            return DeviceTypeMacOs;
        if (OperatingSystem.IsLinux())
            return DeviceTypeLinux;
        return DeviceTypeUnknown;
    }

    private sealed record VersionPayload(
        [property: JsonPropertyName("platform_id")] string PlatformId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("device_uuid")] string DeviceUuid);

    private sealed record OnlinePayload(
        [property: JsonPropertyName("platform_id")] string PlatformId,
        [property: JsonPropertyName("device_uuid")] string DeviceUuid,
        [property: JsonPropertyName("device_type")] string DeviceType);
}
