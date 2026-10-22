using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace PokeSeats.UI.Betapayload;

/// <summary>
/// Unpacks a published PokeSeats archive outside the installation and launches a detached
/// helper that waits for this process to exit, replaces the host-managed files, rolls back on
/// copy errors and restarts the host.
/// </summary>
/// <remarks>
/// The archive is untrusted: entry names are validated against traversal, rooted paths,
/// symbolic links, duplicate entries and non-portable characters, and the total expansion is
/// bounded. Entries targeting <c>data</c>, <c>plugins</c> or root settings are rejected, and
/// files already present in the installation but absent from the package are left untouched.
/// The staged package must stay in place until the helper reports a result in
/// <c>update-helper/result.txt</c>; <c>update.log</c> and <c>backup</c> live beside it. On a
/// successful restart the helper deletes the staged package and backup but keeps the log and
/// result; on failure both are kept for diagnosis.
/// </remarks>
internal static class UpdateInstaller
{
    private const string HostAssemblyName = "PokeSeats.UI";
    private const string PackageFolderName = "package";
    private const int BufferSize = 81920;
    private const int MaxEntryCount = 20000;
    private const long MaxEntryBytes = 1024L * 1024 * 1024;
    private const long MaxTotalExtractedBytes = 2L * 1024 * 1024 * 1024;

    private static readonly string[] ProtectedRootDirectories = { "data", "plugins" };

    private static readonly string[] ProtectedRootFiles =
    {
        "config.json",
        "imgui.ini",
        "selected-workspace.txt"
    };

    private static readonly char[] InvalidFileNameChars = { '<', '>', ':', '"', '|', '?', '*' };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Directory of the most recently launched helper; it contains <c>update.log</c>,
    /// <c>result.txt</c> and the <c>backup</c> directory. Null until <see cref="Launch"/> gets
    /// far enough to prepare the helper.
    /// </summary>
    internal static string? LastHelperDirectory { get; private set; }

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into a fresh <c>package</c> directory below
    /// <paramref name="stagingDirectory"/> and returns that package root.
    /// </summary>
    /// <param name="platform">Target runtime identifier, such as <c>win-x64</c> or <c>linux-x64</c>.</param>
    /// <exception cref="InvalidDataException">The archive is malformed, incomplete or unsafe.</exception>
    /// <exception cref="InvalidOperationException">The staging directory is inside the running application.</exception>
    internal static string ExtractPackage(string archivePath, string stagingDirectory, string platform,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("An update archive path is required.", nameof(archivePath));
        if (string.IsNullOrWhiteSpace(stagingDirectory))
            throw new ArgumentException("A staging directory is required.", nameof(stagingDirectory));

        HostPlatform targetPlatform = ParsePlatform(platform);
        string archiveFullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(archiveFullPath))
            throw new FileNotFoundException($"Update archive not found: '{archiveFullPath}'.", archiveFullPath);

        string stagingRoot = Path.GetFullPath(stagingDirectory);
        if (IsFileSystemRoot(stagingRoot))
            throw new ArgumentException("The staging directory must not be a file-system root.",
                nameof(stagingDirectory));
        if (IsSameOrUnder(stagingRoot, AppContext.BaseDirectory))
            throw new InvalidOperationException(
                "The update must be staged outside the application directory so a running installation is never overwritten.");

        string packageRoot = Path.Combine(stagingRoot, PackageFolderName);

        using ZipArchive archive = ZipFile.OpenRead(archiveFullPath);
        IReadOnlyList<ArchiveEntry> entries = ValidateArchive(archive, targetPlatform, cancellationToken);

        TryDeleteDirectory(packageRoot);
        Directory.CreateDirectory(packageRoot);
        try
        {
            ExtractEntries(entries, packageRoot, cancellationToken);
            VerifyPackageFiles(packageRoot, targetPlatform);
            return packageRoot;
        }
        catch
        {
            TryDeleteDirectory(packageRoot);
            throw;
        }
    }

    /// <summary>
    /// Starts a detached helper that waits for this process to exit, copies
    /// <paramref name="packageRoot"/> over <paramref name="applicationDirectory"/>, rolls back
    /// on copy errors and restarts the host.
    /// </summary>
    /// <remarks>
    /// Returns after the helper has been launched; it does not wait for the replacement. Only the
    /// files present in the package are replaced, so local <c>data</c>, <c>plugins</c> and settings
    /// are preserved. Throws when the package or the target does not look like a PokeSeats
    /// installation, or when the helper cannot be started.
    /// </remarks>
    internal static void Launch(string packageRoot, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
            throw new ArgumentException("A package root is required.", nameof(packageRoot));
        if (string.IsNullOrWhiteSpace(applicationDirectory))
            throw new ArgumentException("An application directory is required.", nameof(applicationDirectory));

        HostPlatform platform = OperatingSystem.IsWindows() ? HostPlatform.Windows
            : OperatingSystem.IsLinux() ? HostPlatform.Linux
            : throw new PlatformNotSupportedException("Self-update is only supported on Windows and Linux.");

        LastHelperDirectory = null;

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        string application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        if (IsFileSystemRoot(root))
            throw new ArgumentException("The package root must not be a file-system root.", nameof(packageRoot));
        if (IsFileSystemRoot(application))
            throw new ArgumentException("The application directory must not be a file-system root.",
                nameof(applicationDirectory));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The staged package does not exist: '{root}'.");
        if (!Directory.Exists(application))
            throw new DirectoryNotFoundException($"The application directory does not exist: '{application}'.");
        if (IsSameOrUnder(root, application))
            throw new InvalidOperationException(
                "The staged package must be outside the application directory.");
        if (IsSameOrUnder(application, root))
            throw new InvalidOperationException(
                "The application directory must not be inside the staged package.");

        VerifyPackageFiles(root, platform);

        if (!File.Exists(Path.Combine(application, HostAssemblyName + ".dll")))
            throw new InvalidOperationException(
                $"'{application}' is not a PokeSeats installation: {HostAssemblyName}.dll is missing.");

        if (ContainsProjectFile(application))
            throw new InvalidOperationException(
                "Self-update is not supported from a source checkout; install from a published package.");

        EnsureWritable(application);
        string builtinsDirectory = Path.Combine(application, "builtins");
        if (Directory.Exists(builtinsDirectory))
            EnsureWritable(builtinsDirectory);

        // Keep the helper beside the staged package so the caller can find its log and result.
        // The package root is already known to be outside the application directory.
        string workDirectory = Path.Combine(Path.GetDirectoryName(root)!, "update-helper");
        try
        {
            Directory.CreateDirectory(workDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            workDirectory = Path.Combine(Path.GetTempPath(),
                "PokeSeats-update-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(workDirectory);
        }

        LastHelperDirectory = workDirectory;

        string logPath = Path.Combine(workDirectory, "update.log");
        TryDeleteFile(logPath);
        TryDeleteFile(Path.Combine(workDirectory, "result.txt"));
        string scriptPath = Path.Combine(workDirectory,
            platform == HostPlatform.Windows ? "update.ps1" : "update.sh");
        File.WriteAllText(scriptPath,
            platform == HostPlatform.Windows ? WindowsHelperScript : LinuxHelperScript, Utf8NoBom);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory
        };

        if (platform == HostPlatform.Windows)
        {
            startInfo.FileName = ResolveWindowsPowerShell();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ParentPid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-PackageRoot");
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add("-ApplicationDirectory");
            startInfo.ArgumentList.Add(application);
            startInfo.ArgumentList.Add("-LogPath");
            startInfo.ArgumentList.Add(logPath);
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add(application);
            startInfo.ArgumentList.Add(logPath);
        }

        AppendLog(logPath,
            $"Launching update helper: '{root}' -> '{application}' (host process {Environment.ProcessId}).");
        try
        {
            using Process? helper = Process.Start(startInfo);
            if (helper is null)
                throw new IOException("The update helper process could not be started.");
            AppendLog(logPath,
                $"Update helper started (process {helper.Id}). Local data, external plugins and settings are preserved.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
                                              or PlatformNotSupportedException or ArgumentException)
        {
            AppendLog(logPath, "Unable to start the update helper: " + exception.Message);
            throw new IOException(
                $"Unable to start the update helper. Details were written to '{logPath}'.", exception);
        }
    }

    private static IReadOnlyList<ArchiveEntry> ValidateArchive(ZipArchive archive, HostPlatform platform,
        CancellationToken cancellationToken)
    {
        var entries = new List<ArchiveEntry>(Math.Min(archive.Entries.Count, MaxEntryCount));
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotalBytes = 0;
        int fileCount = 0;
        string? topFolder = null;
        bool allFilesNested = true;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaxEntryCount)
                throw new InvalidDataException(
                    $"The archive contains more than {MaxEntryCount} entries and was rejected.");

            (string path, bool isDirectory) = NormalizeEntryName(entry.FullName);
            if (HasUnsupportedUnixType(entry))
                throw new InvalidDataException(
                    $"The archive contains a symbolic link or special file: '{path}'.");

            if (isDirectory)
            {
                if (entry.Length != 0)
                    throw new InvalidDataException($"The directory entry '{path}' unexpectedly has content.");
                if (filePaths.Contains(path))
                    throw new InvalidDataException(
                        $"The archive entry '{path}' is both a file and a directory.");
                directoryPaths.Add(path);
                entries.Add(new ArchiveEntry(entry, path, IsDirectory: true));
                continue;
            }

            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0x4000)
                throw new InvalidDataException($"The archive entry '{path}' is marked as a directory.");

            RegisterFile(path, filePaths, directoryPaths);
            if (entry.Length < 0 || entry.Length > MaxEntryBytes)
                throw new InvalidDataException(
                    $"The archive entry '{path}' exceeds the {MaxEntryBytes / (1024 * 1024)} MiB limit.");
            if (entry.Length > MaxTotalExtractedBytes - declaredTotalBytes)
                throw new InvalidDataException(
                    $"The archive expands beyond the {MaxTotalExtractedBytes / (1024 * 1024)} MiB limit.");
            declaredTotalBytes += entry.Length;
            fileCount++;

            int separator = path.IndexOf('/');
            if (separator < 0)
            {
                allFilesNested = false;
            }
            else
            {
                string candidate = path[..separator];
                if (topFolder is null)
                    topFolder = candidate;
                else if (!string.Equals(topFolder, candidate, StringComparison.Ordinal))
                    allFilesNested = false;
            }

            entries.Add(new ArchiveEntry(entry, path, IsDirectory: false));
        }

        if (fileCount == 0)
            throw new InvalidDataException("The archive does not contain any files.");

        if (!allFilesNested || topFolder is null)
            return FinalizeEntries(entries, prefix: string.Empty, wrapper: null, platform: platform);

        if (!topFolder.StartsWith("PokeSeats", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported archive layout: the top-level folder '{topFolder}' is not a PokeSeats package folder.");

        string prefix = topFolder + "/";
        foreach (ArchiveEntry item in entries)
        {
            if (item.IsDirectory && string.Equals(item.RelativePath, topFolder, StringComparison.Ordinal))
                continue;
            if (!item.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The archive mixes a top-level package folder with root-level entries.");
        }

        return FinalizeEntries(entries, prefix, topFolder, platform);
    }

    private static IReadOnlyList<ArchiveEntry> FinalizeEntries(List<ArchiveEntry> entries, string prefix,
        string? wrapper, HostPlatform platform)
    {
        var result = new List<ArchiveEntry>(entries.Count);
        var packageFilePaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (ArchiveEntry item in entries)
        {
            string relativePath = item.RelativePath;
            if (wrapper is not null)
            {
                if (string.Equals(relativePath, wrapper, StringComparison.Ordinal))
                    continue;
                relativePath = relativePath[prefix.Length..];
            }

            if (relativePath.Length == 0)
                continue;
            if (IsProtectedPath(relativePath))
                throw new InvalidDataException(
                    $"The archive tries to replace protected user content: '{relativePath}'.");
            if (!item.IsDirectory && !packageFilePaths.Add(relativePath))
                throw new InvalidDataException($"The archive contains a duplicate entry: '{relativePath}'.");

            result.Add(item with { RelativePath = relativePath });
        }

        foreach (string required in RequiredFiles(platform))
        {
            if (!packageFilePaths.Contains(required))
                throw new InvalidDataException($"The archive is incomplete: '{required}' is missing.");
        }

        return result;
    }

    private static void ExtractEntries(IReadOnlyList<ArchiveEntry> entries, string packageRoot,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long totalBytes = 0;
        try
        {
            foreach (ArchiveEntry item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.GetFullPath(Path.Combine(packageRoot,
                    item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsSameOrUnder(destination, packageRoot))
                    throw new InvalidDataException(
                        $"The archive entry '{item.RelativePath}' escapes the staging directory.");

                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using Stream source = item.Entry.Open();
                using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, BufferSize, FileOptions.SequentialScan))
                {
                    long entryBytes = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        entryBytes += read;
                        totalBytes += read;
                        if (entryBytes > MaxEntryBytes || totalBytes > MaxTotalExtractedBytes)
                            throw new InvalidDataException(
                                "The archive expands beyond the configured extraction limits.");
                        target.Write(buffer, 0, read);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void VerifyPackageFiles(string packageRoot, HostPlatform platform)
    {
        foreach (string relativePath in RequiredFiles(platform))
        {
            string path = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                throw new InvalidDataException(
                    $"The package is incomplete: '{relativePath}' is missing or empty.");
        }
    }

    private static IEnumerable<string> RequiredFiles(HostPlatform platform)
    {
        yield return platform == HostPlatform.Windows ? "PokeSeats.UI.exe" : "PokeSeats.UI";
        yield return "PokeSeats.UI.dll";
        yield return "PokeSeats.UI.deps.json";
        yield return "PokeSeats.UI.runtimeconfig.json";
        yield return "builtins/pokeseats.default/plugin.json";
        yield return "builtins/pokeseats.defaultpayload/plugin.json";
    }

    private static (string Path, bool IsDirectory) NormalizeEntryName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            throw new InvalidDataException("The archive contains an entry without a name.");

        string normalized = fullName.Replace('\\', '/');
        bool isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
        if (isDirectory)
            normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0)
            throw new InvalidDataException("The archive contains an empty entry name.");
        if (normalized[0] == '/')
            throw new InvalidDataException($"The archive contains a rooted entry: '{fullName}'.");
        if (normalized.Length > 1000)
            throw new InvalidDataException("The archive contains an excessively long entry name.");

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
                throw new InvalidDataException($"The archive entry '{fullName}' contains an empty path segment.");
            if (segment.Length > 255)
                throw new InvalidDataException($"The archive entry '{fullName}' contains an overlong path segment.");
            if (segment is "." or "..")
                throw new InvalidDataException($"The archive entry '{fullName}' contains a relative path segment.");
            if (segment.IndexOfAny(InvalidFileNameChars) >= 0)
                throw new InvalidDataException(
                    $"The archive entry '{fullName}' contains a character that is not portable to Windows.");
            if (segment[^1] is '.' or ' ')
                throw new InvalidDataException(
                    $"The archive entry '{fullName}' ends a path segment with a dot or space.");
            foreach (char character in segment)
            {
                if (character < ' ' || character == '\u007f')
                    throw new InvalidDataException($"The archive entry '{fullName}' contains a control character.");
            }
        }

        return (string.Join('/', segments), isDirectory);
    }

    private static void RegisterFile(string path, HashSet<string> filePaths, HashSet<string> directoryPaths)
    {
        if (directoryPaths.Contains(path) || !filePaths.Add(path))
            throw new InvalidDataException($"The archive contains a duplicate or conflicting entry: '{path}'.");

        string current = path;
        while (true)
        {
            int separator = current.LastIndexOf('/');
            if (separator < 0)
                break;
            current = current[..separator];
            if (filePaths.Contains(current))
                throw new InvalidDataException(
                    $"The archive entry '{path}' is nested below the file '{current}'.");
            directoryPaths.Add(current);
        }
    }

    private static bool IsProtectedPath(string relativePath)
    {
        foreach (string directory in ProtectedRootDirectories)
        {
            if (relativePath.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (string file in ProtectedRootFiles)
        {
            if (relativePath.Equals(file, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasUnsupportedUnixType(ZipArchiveEntry entry)
    {
        // The high 16 bits of ExternalAttributes hold the Unix mode when the archive was produced
        // on a Unix system: 0xA000 links, 0x1000 FIFOs, 0x2000/0x6000 devices, 0xC000 sockets.
        int type = (entry.ExternalAttributes >> 16) & 0xF000;
        return type is 0x1000 or 0x2000 or 0x6000 or 0xA000 or 0xC000;
    }

    private static HostPlatform ParsePlatform(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("A target runtime identifier is required.", nameof(platform));

        string value = platform.Trim().ToLowerInvariant();
        if (value.StartsWith("win", StringComparison.Ordinal) || value.Contains("windows", StringComparison.Ordinal))
            return HostPlatform.Windows;
        if (value.StartsWith("linux", StringComparison.Ordinal)
            || value.StartsWith("ubuntu", StringComparison.Ordinal)
            || value.StartsWith("debian", StringComparison.Ordinal)
            || value.StartsWith("fedora", StringComparison.Ordinal)
            || value.StartsWith("rhel", StringComparison.Ordinal)
            || value.StartsWith("centos", StringComparison.Ordinal)
            || value.StartsWith("alpine", StringComparison.Ordinal)
            || value.StartsWith("rocky", StringComparison.Ordinal)
            || value.StartsWith("sles", StringComparison.Ordinal)
            || value.StartsWith("opensuse", StringComparison.Ordinal)
            || value.StartsWith("amzn", StringComparison.Ordinal))
            return HostPlatform.Linux;

        throw new PlatformNotSupportedException(
            $"Unsupported update platform '{platform}'. Only Windows and Linux packages are supported.");
    }

    private static string ResolveWindowsPowerShell()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system))
        {
            string candidate = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return "powershell.exe";
    }

    private static bool IsSameOrUnder(string path, string directory)
    {
        StringComparison comparison = PathComparison;
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(candidate, root, comparison))
            return true;
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, comparison);
    }

    private static bool IsFileSystemRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool ContainsProjectFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.csproj").Any()
                   || Directory.EnumerateFiles(directory, "*.sln").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsureWritable(string directory)
    {
        string probe = Path.Combine(directory, ".pokeseats-update-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The application directory '{directory}' is not writable, so the update was not started.",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stray probe file is harmless.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale log or result file is overwritten by the helper when it starts.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover staging directory is harmless; the caller gets the original failure.
        }
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                          + " " + message + Environment.NewLine;
            File.AppendAllText(path, line, Utf8NoBom);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never turn an update failure into a different failure.
        }
    }

    private enum HostPlatform
    {
        Windows,
        Linux
    }

    private sealed record ArchiveEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);

    private const string WindowsHelperScript = @"
param(
    [Parameter(Mandatory = $true)][int]$ParentPid,
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$script:UpdateLogPath = $LogPath
$script:UpdateResultPath = Join-Path (Split-Path -Parent $LogPath) 'result.txt'

function Write-UpdateLog {
    param([string]$Message)
    try {
        $timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        [IO.File]::AppendAllText($script:UpdateLogPath, $timestamp + ' ' + $Message + [Environment]::NewLine, [Text.Encoding]::UTF8)
    } catch {
    }
}

function Complete-Update {
    param([string]$Status, [int]$ExitCode, [string]$Message)
    if ($Message.Length -gt 0) {
        Write-UpdateLog $Message
    }
    try {
        [IO.File]::WriteAllText($script:UpdateResultPath, $Status + [Environment]::NewLine, [Text.Encoding]::UTF8)
    } catch {
    }
    exit $ExitCode
}

function Test-ProtectedPath {
    param([string]$RelativePath)
    $value = $RelativePath.Replace('\', '/')
    if ($value -match '^(data|plugins)(/|$)') { return $true }
    if ($value -match '^(config\.json|imgui\.ini|selected-workspace\.txt)$') { return $true }
    return $false
}

function Test-PathUnder {
    param([string]$Path, [string]$Directory)
    $prefix = $Directory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-ReparseAncestor {
    param([string]$Path, [string]$Directory)
    $current = Split-Path -Parent $Path
    while ($null -ne $current -and $current.Length -ge $Directory.Length) {
        if ($current.Equals($Directory, [StringComparison]::OrdinalIgnoreCase)) { break }
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        }
        $current = Split-Path -Parent $current
    }
    return $false
}

function Restart-PokeSeats {
    param([string]$Directory)
    try {
        $exe = Join-Path $Directory 'PokeSeats.UI.exe'
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            Start-Process -FilePath $exe -WorkingDirectory $Directory | Out-Null
            return $true
        }
        $dll = Join-Path $Directory 'PokeSeats.UI.dll'
        if (Test-Path -LiteralPath $dll -PathType Leaf) {
            $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
            if ($null -ne $dotnet) {
                $quoted = [char]34 + $dll + [char]34
                Start-Process -FilePath $dotnet.Source -ArgumentList $quoted -WorkingDirectory $Directory | Out-Null
                return $true
            }
        }
    } catch {
        Write-UpdateLog ('restart failed: ' + $_.Exception.Message)
    }
    return $false
}

function Invoke-Rollback {
    param(
        [System.Collections.Generic.List[string]]$Copied,
        [hashtable]$Existed,
        [string]$Directory,
        [string]$BackupDirectory
    )
    $failures = 0
    for ($index = $Copied.Count - 1; $index -ge 0; $index--) {
        $relative = $Copied[$index]
        try {
            $destination = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
            if (-not (Test-PathUnder $destination $Directory)) {
                throw 'destination escapes the application directory'
            }
            if ($Existed[$relative]) {
                Copy-Item -LiteralPath (Join-Path $BackupDirectory $relative) -Destination $destination -Force
            } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
                Remove-Item -LiteralPath $destination -Force
            }
        } catch {
            $failures++
            Write-UpdateLog ('rollback failed for ' + $relative + ': ' + $_.Exception.Message)
        }
    }
    return $failures
}

$root = $null
$app = $null
$copied = New-Object 'System.Collections.Generic.List[string]'
$existedAtDestination = @{}
$applyInProgress = $false

try {
    Write-UpdateLog 'helper started'
    $root = [IO.Path]::GetFullPath($PackageRoot)
    $app = [IO.Path]::GetFullPath($ApplicationDirectory)
    if ($root -eq [IO.Path]::GetPathRoot($root)) {
        Complete-Update 'rejected' 2 'invalid package root'
    }
    if ($app -eq [IO.Path]::GetPathRoot($app)) {
        Complete-Update 'rejected' 2 'invalid application directory'
    }
    $root = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $app = $app.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)

    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        Complete-Update 'rejected' 2 ('package root not found: ' + $root)
    }
    if (-not (Test-Path -LiteralPath $app -PathType Container)) {
        Complete-Update 'rejected' 2 ('application directory not found: ' + $app)
    }
    if ($root.Equals($app, [StringComparison]::OrdinalIgnoreCase) -or (Test-PathUnder $root $app)) {
        Complete-Update 'rejected' 2 'refusing to install a package from inside the application directory'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'PokeSeats.UI.exe') -PathType Leaf)) {
        Complete-Update 'rejected' 2 'package does not contain PokeSeats.UI.exe'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $app 'PokeSeats.UI.dll') -PathType Leaf)) {
        Complete-Update 'rejected' 2 'application directory does not look like a PokeSeats installation'
    }

    Write-UpdateLog ('waiting for host process ' + $ParentPid + ' to exit')
    $parent = $null
    try {
        $parent = [System.Diagnostics.Process]::GetProcessById($ParentPid)
    } catch {
        $parent = $null
    }
    if ($null -ne $parent) {
        $exited = $parent.WaitForExit($TimeoutSeconds * 1000)
        $parent.Dispose()
        if (-not $exited) {
            Complete-Update 'timeout' 3 'host process did not exit before the timeout; the installation was not changed'
        }
    }
    Start-Sleep -Milliseconds 700

    $work = Split-Path -Parent $LogPath
    $backup = Join-Path $work 'backup'
    if (-not (Test-Path -LiteralPath $backup -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
    }
    $appPrefix = $app + [IO.Path]::DirectorySeparatorChar

    Write-UpdateLog 'host exited; applying package'
    $applyInProgress = $true
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length + 1)
        if (Test-ProtectedPath $relative) { continue }
        $destination = [IO.Path]::GetFullPath((Join-Path $app $relative))
        if (-not $destination.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('destination escapes the application directory: ' + $relative)
        }
        if (Test-Path -LiteralPath $destination -PathType Container) {
            throw ('destination is a directory in the installation: ' + $relative)
        }
        if (Test-ReparseAncestor $destination $app) {
            throw ('destination has a reparse point ancestor: ' + $relative)
        }
        $leafItem = Get-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        if ($null -ne $leafItem -and (($leafItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw ('destination is a reparse point: ' + $relative)
        }
        $parentDirectory = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parentDirectory -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $parentDirectory | Out-Null
        }
        $existed = Test-Path -LiteralPath $destination -PathType Leaf
        if ($existed) {
            $backupFile = Join-Path $backup $relative
            $backupParent = Split-Path -Parent $backupFile
            if (-not (Test-Path -LiteralPath $backupParent -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $backupParent | Out-Null
            }
            Copy-Item -LiteralPath $destination -Destination $backupFile -Force
        }
        $copied.Add($relative)
        $existedAtDestination[$relative] = $existed
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    $applyInProgress = $false

    Write-UpdateLog ('applied ' + $copied.Count + ' file(s)')
    if (Restart-PokeSeats $app) {
        try {
            Remove-Item -LiteralPath $root -Recurse -Force
            Remove-Item -LiteralPath $backup -Recurse -Force
            Write-UpdateLog 'removed the staged package and backup after a successful update'
        } catch {
            Write-UpdateLog ('could not clean the staged package: ' + $_.Exception.Message)
        }
        Complete-Update 'success' 0 'restarted the application'
    }
    Complete-Update 'applied-restart-failed' 4 'package applied but the application could not be restarted'
} catch {
    Write-UpdateLog ('update failed: ' + $_.Exception.ToString())
    $status = 'failed'
    if ($applyInProgress -and $copied.Count -gt 0) {
        $backup = Join-Path (Split-Path -Parent $LogPath) 'backup'
        $failures = Invoke-Rollback $copied $existedAtDestination $app $backup
        if ($failures -eq 0) {
            $status = 'rolled-back'
            Write-UpdateLog 'rollback restored the previous installation'
        } else {
            $status = 'rollback-incomplete'
            Write-UpdateLog ('rollback was incomplete; ' + $failures + ' file(s) could not be restored')
        }
    }
    if ($null -ne $app -and (Test-Path -LiteralPath $app -PathType Container)) {
        if (Restart-PokeSeats $app) {
            Write-UpdateLog 'restarted the previous application version'
        } else {
            Write-UpdateLog 'could not restart the application after the failure'
        }
    }
    Complete-Update $status 1 ''
}
";

    private const string LinuxHelperScript = @"#!/bin/sh
# PokeSeats self-update helper.
# Arguments: 1 parent PID, 2 package root, 3 application directory, 4 log path.
set -u

PARENT_PID=$1
PACKAGE_ROOT=$2
APPLICATION_DIRECTORY=$3
UPDATE_LOG=$4
TIMEOUT_SECONDS=180
WORK_DIRECTORY=$(dirname ""$UPDATE_LOG"")
RESULT_FILE=""$WORK_DIRECTORY/result.txt""

strip_trailing_slashes() {
    value=$1
    while [ ""${#value}"" -gt 1 ] && [ ""${value%/}"" != ""$value"" ]; do
        value=${value%/}
    done
    printf '%s' ""$value""
}

log_message() {
    printf '%s %s\n' ""$(date -u '+%Y-%m-%dT%H:%M:%SZ')"" ""$*"" >> ""$UPDATE_LOG"" 2>/dev/null || true
}

finish() {
    status=$1
    exit_code=$2
    message=$3
    if [ -n ""$message"" ]; then
        log_message ""$message""
    fi
    printf '%s\n' ""$status"" > ""$RESULT_FILE"" 2>/dev/null || true
    exit ""$exit_code""
}

check_no_symlink_ancestors() {
    relative_directory=$1
    current=$APPLICATION_DIRECTORY
    remaining=$relative_directory
    while [ -n ""$remaining"" ] && [ ""$remaining"" != ""."" ]; do
        case ""$remaining"" in
            */*) segment=${remaining%%/*}; remaining=${remaining#*/} ;;
            *) segment=$remaining; remaining='' ;;
        esac
        current=""$current/$segment""
        if [ -L ""$current"" ]; then return 1; fi
        if [ -e ""$current"" ] && [ ! -d ""$current"" ]; then return 1; fi
        if [ ! -e ""$current"" ]; then return 0; fi
    done
    return 0
}

restart_application() {
    if [ -x ""$APPLICATION_DIRECTORY/PokeSeats.UI"" ]; then
        ( cd ""$APPLICATION_DIRECTORY"" && nohup ./PokeSeats.UI </dev/null >>""$UPDATE_LOG"" 2>&1 & )
        return 0
    fi
    if [ -f ""$APPLICATION_DIRECTORY/PokeSeats.UI.dll"" ] && command -v dotnet >/dev/null 2>&1; then
        ( cd ""$APPLICATION_DIRECTORY"" && nohup dotnet PokeSeats.UI.dll </dev/null >>""$UPDATE_LOG"" 2>&1 & )
        return 0
    fi
    return 1
}

PACKAGE_ROOT=$(strip_trailing_slashes ""$PACKAGE_ROOT"")
APPLICATION_DIRECTORY=$(strip_trailing_slashes ""$APPLICATION_DIRECTORY"")

if [ -z ""$PACKAGE_ROOT"" ] || [ -z ""$APPLICATION_DIRECTORY"" ]; then
    finish rejected 2 'invalid package or application path'
fi

log_message 'helper started'

if [ ! -d ""$PACKAGE_ROOT"" ]; then
    finish rejected 2 ""package root not found: $PACKAGE_ROOT""
fi
if [ ! -d ""$APPLICATION_DIRECTORY"" ]; then
    finish rejected 2 ""application directory not found: $APPLICATION_DIRECTORY""
fi
case ""$PACKAGE_ROOT"" in
    ""$APPLICATION_DIRECTORY""|""$APPLICATION_DIRECTORY""/*)
        finish rejected 2 'refusing to install a package from inside the application directory'
        ;;
esac
if [ ! -f ""$PACKAGE_ROOT/PokeSeats.UI"" ]; then
    finish rejected 2 'package does not contain the PokeSeats.UI apphost'
fi
if [ ! -f ""$APPLICATION_DIRECTORY/PokeSeats.UI.dll"" ]; then
    finish rejected 2 'application directory does not look like a PokeSeats installation'
fi

log_message ""waiting for host process $PARENT_PID to exit""
waited=0
while kill -0 ""$PARENT_PID"" 2>/dev/null; do
    if [ ""$waited"" -ge ""$TIMEOUT_SECONDS"" ]; then
        finish timeout 3 'host process did not exit before the timeout; the installation was not changed'
    fi
    sleep 1
    waited=$((waited + 1))
done
sleep 1

BACKUP_DIRECTORY=""$WORK_DIRECTORY/backup""
if ! mkdir -p ""$BACKUP_DIRECTORY""; then
    finish failed 5 'unable to create the backup directory'
fi

log_message 'host exited; applying package'
: > ""$WORK_DIRECTORY/copied.txt""
apply_failed=0
if ! find ""$PACKAGE_ROOT"" -type f > ""$WORK_DIRECTORY/files.txt""; then
    log_message 'unable to enumerate the package'
    apply_failed=1
fi

if [ ""$apply_failed"" -eq 0 ]; then
    while IFS= read -r source_file; do
        relative=${source_file#""$PACKAGE_ROOT""/}
        case ""$relative"" in
            data|data/*|plugins|plugins/*|config.json|imgui.ini|selected-workspace.txt)
                continue
                ;;
            ..|../*|*/../*|*/..)
                log_message ""unsafe relative path: $relative""
                apply_failed=1
                break
                ;;
            *:*)
                log_message ""unsafe relative path: $relative""
                apply_failed=1
                break
                ;;
        esac
        destination=""$APPLICATION_DIRECTORY/$relative""
        if [ -d ""$destination"" ]; then
            log_message ""destination is a directory in the installation: $relative""
            apply_failed=1
            break
        fi
        if [ -L ""$destination"" ]; then
            log_message ""refusing to replace the symbolic link: $relative""
            apply_failed=1
            break
        fi
        if ! check_no_symlink_ancestors ""$(dirname ""$relative"")""; then
            log_message ""destination has a symbolic link ancestor: $relative""
            apply_failed=1
            break
        fi
        if ! mkdir -p ""$(dirname ""$destination"")""; then
            log_message ""unable to create the directory for $relative""
            apply_failed=1
            break
        fi
        marker='N'
        if [ -f ""$destination"" ]; then
            marker='B'
            if ! mkdir -p ""$(dirname ""$BACKUP_DIRECTORY/$relative"")""; then
                log_message ""unable to create the backup directory for $relative""
                apply_failed=1
                break
            fi
            if ! cp -p ""$destination"" ""$BACKUP_DIRECTORY/$relative""; then
                log_message ""unable to back up $relative""
                apply_failed=1
                break
            fi
        fi
        if ! printf '%s:%s\n' ""$marker"" ""$relative"" >> ""$WORK_DIRECTORY/copied.txt""; then
            log_message ""unable to record rollback state for $relative""
            apply_failed=1
            break
        fi
        if ! cp -f ""$source_file"" ""$destination""; then
            log_message ""unable to replace $relative""
            apply_failed=1
            break
        fi
    done < ""$WORK_DIRECTORY/files.txt""
fi

if [ ""$apply_failed"" -ne 0 ]; then
    rollback_failures=0
    rollback_status=failed
    if [ -s ""$WORK_DIRECTORY/copied.txt"" ]; then
        while IFS= read -r entry; do
            kind=${entry%%:*}
            relative=${entry#*:}
            destination=""$APPLICATION_DIRECTORY/$relative""
            if [ ""$kind"" = 'B' ]; then
                if ! cp -p ""$BACKUP_DIRECTORY/$relative"" ""$destination""; then
                    rollback_failures=$((rollback_failures + 1))
                fi
            else
                if ! rm -f ""$destination""; then
                    rollback_failures=$((rollback_failures + 1))
                fi
            fi
        done < ""$WORK_DIRECTORY/copied.txt""
        if [ ""$rollback_failures"" -eq 0 ]; then
            rollback_status=rolled-back
            log_message 'rollback restored the previous installation'
        else
            rollback_status=rollback-incomplete
            log_message ""rollback was incomplete; $rollback_failures file(s) could not be restored""
        fi
    else
        log_message 'the update failed before any file was replaced'
    fi
    if restart_application; then
        log_message 'restarted the previous application version'
    else
        log_message 'could not restart the application after the failure'
    fi
    finish ""$rollback_status"" 1 ''
fi

if [ -f ""$APPLICATION_DIRECTORY/PokeSeats.UI"" ]; then
    if ! chmod +x ""$APPLICATION_DIRECTORY/PokeSeats.UI""; then
        log_message 'warning: could not mark the PokeSeats.UI apphost executable'
    fi
fi

log_message 'package applied'
if restart_application; then
    if ! rm -rf ""$PACKAGE_ROOT"" ""$BACKUP_DIRECTORY""; then
        log_message 'warning: could not remove the staged package and backup'
    fi
    finish success 0 'restarted the application'
fi
finish applied-restart-failed 4 'package applied but the application could not be restarted'
";
}
