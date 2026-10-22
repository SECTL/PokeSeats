using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PokeSeats.UI.Betapayload;

internal enum UpdateStatus { Checking, UpToDate, Available, Downloading, Ready, Installing, Failed }
internal sealed record ReleaseUpdate(string Tag, string Version, string Notes, Uri PageUrl,
    Uri DownloadUrl, long Size, string? Sha256);
internal sealed record UpdateState(UpdateStatus Status, ReleaseUpdate? Release = null,
    string? Error = null, float Progress = 0);

internal sealed class GitHubUpdater : IDisposable
{
    private const string LatestRelease = "https://api.github.com/repos/SECTL/PokeSeats/releases/latest";
    private const long MaximumPackageSize = 1024L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action _onUpdateAvailable;
    private UpdateState _state = new(UpdateStatus.UpToDate);
    private bool _disposed;
    private bool _helperStarted;
    private string? _stagingDirectory;
    private string? _packageRoot;

    public string CurrentVersion { get; }
    public string? Platform { get; }
    public UpdateState State => Volatile.Read(ref _state);

    public GitHubUpdater(Action onUpdateAvailable)
    {
        _onUpdateAvailable = onUpdateAvailable;
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(GitHubUpdater).Assembly;
        CurrentVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Platform = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : null;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PokeSeats-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Check()
    {
        lock (_gate)
        {
            if (_disposed || State.Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing or UpdateStatus.Ready)
                return;
            _state = new UpdateState(UpdateStatus.Checking);
        }
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(LatestRelease, _lifetime.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetState(new UpdateState(UpdateStatus.UpToDate));
                return;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException(UpdateStrings.RateLimited);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: _lifetime.Token).ConfigureAwait(false);
            ReleaseUpdate? release = SelectRelease(document.RootElement, CurrentVersion, Platform);
            lock (_gate)
            {
                if (_disposed)
                    return;
                _state = release is null ? new UpdateState(UpdateStatus.UpToDate) : new UpdateState(UpdateStatus.Available, release);
                if (release is not null)
                    _onUpdateAvailable();
            }
        }
        catch (Exception error)
        {
            SetFailure(UpdateStrings.CheckFailed, error);
        }
    }

    internal static ReleaseUpdate? SelectRelease(JsonElement release, string currentVersion, string? platform)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!ReleaseVersion.TryParse(tag, out ReleaseVersion remote) || remote.Prerelease is not null)
            throw new InvalidDataException(UpdateStrings.InvalidVersion);
        if (!ReleaseVersion.TryParse(currentVersion, out ReleaseVersion current))
            throw new InvalidDataException(UpdateStrings.InvalidVersion);
        if (remote.CompareTo(current) <= 0)
            return null;
        if (platform is null)
            throw new PlatformNotSupportedException(UpdateStrings.UnsupportedPlatform);
        string expected = $"PokeSeats-{tag.TrimStart('v', 'V')}-{platform}.zip";
        string taggedName = $"PokeSeats-{tag}-{platform}.zip";
        JsonElement? selected = null;
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            if (!string.Equals(name, expected, StringComparison.Ordinal)
                && !string.Equals(name, taggedName, StringComparison.Ordinal))
                continue;
            if (selected is not null)
                throw new InvalidDataException(UpdateStrings.InvalidPackage);
            selected = asset;
        }
        if (selected is not { } package)
            throw new InvalidDataException(string.Format(UpdateStrings.MissingAsset, expected));
        long size = package.GetProperty("size").GetInt64();
        string? url = package.GetProperty("browser_download_url").GetString();
        if (size <= 0 || size > MaximumPackageSize || !Uri.TryCreate(url, UriKind.Absolute, out Uri? download)
            || download.Scheme != Uri.UriSchemeHttps || download.Host != "github.com"
            || !download.AbsolutePath.StartsWith("/SECTL/PokeSeats/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException(UpdateStrings.InvalidPackage);
        string? digest = package.TryGetProperty("digest", out JsonElement digestValue) ? digestValue.GetString() : null;
        string? sha256 = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null;
        return new ReleaseUpdate(tag, tag.TrimStart('v', 'V'),
            release.TryGetProperty("body", out JsonElement notes) ? notes.GetString() ?? "" : "",
            new Uri("https://github.com/SECTL/PokeSeats/releases/tag/" + Uri.EscapeDataString(tag)), download, size, sha256);
    }

    public void Download()
    {
        ReleaseUpdate? release;
        lock (_gate)
        {
            release = State.Release;
            if (_disposed || release is null || State.Status is not (UpdateStatus.Available or UpdateStatus.Failed))
                return;
            _state = new UpdateState(UpdateStatus.Downloading, release);
        }
        _ = DownloadAsync(release);
    }

    private async Task DownloadAsync(ReleaseUpdate release)
    {
        string directory = Path.Combine(Path.GetTempPath(), "PokeSeats-update-" + Guid.NewGuid().ToString("N"));
        bool keep = false;
        try
        {
            if (release.Sha256 is not { Length: 64 } || !release.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException(UpdateStrings.MissingDigest);
            Directory.CreateDirectory(directory);
            string archivePath = Path.Combine(directory, "release.zip");
            // Downloads get a longer deadline than the small API request, including the response body.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException(UpdateStrings.SizeMismatch);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                float reportedProgress = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > release.Size)
                        throw new InvalidDataException(UpdateStrings.SizeMismatch);
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    float progress = (float)received / release.Size;
                    if (progress - reportedProgress >= 0.01f)
                    {
                        SetState(new UpdateState(UpdateStatus.Downloading, release, Progress: progress));
                        reportedProgress = progress;
                    }
                }
                if (received != release.Size)
                    throw new InvalidDataException(UpdateStrings.SizeMismatch);
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(UpdateStrings.DigestMismatch);
            string root = await Task.Run(() => UpdateInstaller.ExtractPackage(archivePath,
                Path.Combine(directory, "package"), Platform!, _lifetime.Token), _lifetime.Token).ConfigureAwait(false);
            File.Delete(archivePath);
            lock (_gate)
            {
                if (_disposed)
                    return;
                _stagingDirectory = directory;
                _packageRoot = root;
                _state = new UpdateState(UpdateStatus.Ready, release, Progress: 1);
                keep = true;
            }
        }
        catch (Exception error)
        {
            SetFailure(UpdateStrings.DownloadFailed, error, release);
        }
        finally
        {
            if (!keep)
                DeleteStaging(directory);
        }
    }

    public bool Install()
    {
        lock (_gate)
        {
            if (_disposed || State.Status != UpdateStatus.Ready || _packageRoot is null)
                return false;
            try
            {
                UpdateInstaller.Launch(_packageRoot, AppContext.BaseDirectory);
                _helperStarted = true;
                _state = State with { Status = UpdateStatus.Installing };
                return true;
            }
            catch (Exception error)
            {
                _state = State with { Error = string.Format(UpdateStrings.InstallFailed, error.Message) };
                return false;
            }
        }
    }

    private void SetState(UpdateState state)
    {
        lock (_gate)
            if (!_disposed)
                Volatile.Write(ref _state, state);
    }

    private void SetFailure(string format, Exception error, ReleaseUpdate? release = null)
        => SetState(new UpdateState(UpdateStatus.Failed, release, string.Format(format, error.Message)));

    private static void DeleteStaging(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            _http.Dispose();
            // A launched helper owns the staged files until the application has exited.
            if (!_helperStarted && _stagingDirectory is not null)
                DeleteStaging(_stagingDirectory);
        }
    }
}

internal readonly record struct ReleaseVersion(Version Number, string? Prerelease) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string text, out ReleaseVersion result)
    {
        result = default;
        string version = text.TrimStart('v', 'V').Split('+', 2)[0];
        string[] parts = version.Split('-', 2);
        string[] numbers = parts[0].Split('.');
        if (numbers.Length != 3 || numbers.Any(n => n.Length == 0 || n.Length > 1 && n[0] == '0'
                || n.Any(c => c < '0' || c > '9'))
            || !Version.TryParse(parts[0], out Version? parsed))
            return false;
        string? prerelease = parts.Length == 2 ? parts[1] : null;
        if (prerelease is not null && prerelease.Split('.').Any(p => p.Length == 0
                || p.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9') && c != '-')
                || p.Length > 1 && p[0] == '0' && p.All(char.IsDigit)))
            return false;
        result = new ReleaseVersion(parsed, prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int number = Number.CompareTo(other.Number);
        if (number != 0)
            return number;
        if (Prerelease is null)
            return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null)
            return -1;
        string[] left = Prerelease.Split('.');
        string[] right = other.Prerelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = left[index].All(char.IsDigit);
            bool rightNumeric = right[index].All(char.IsDigit);
            int comparison = leftNumeric && rightNumeric
                ? left[index].Length.CompareTo(right[index].Length)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : 0;
            if (comparison == 0)
                comparison = string.CompareOrdinal(left[index], right[index]);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
}
