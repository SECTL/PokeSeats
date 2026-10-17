using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace PokeSeats.UI;

/// <summary>
/// Derives a stable per-device identifier from the operating system's machine id: Windows
/// <c>MachineGuid</c>, Linux <c>machine-id</c>, macOS <c>IOPlatformUUID</c>, with the host name
/// as a last resort. The raw machine id never leaves this class; only its name-based UUID is
/// persisted. Copying the <c>data</c> folder to another device therefore yields that device's
/// identifier instead of duplicating one.
/// </summary>
internal static class DeviceIdentity
{
    // Fixed namespace for PokeSeats device identifiers. Changing it breaks every existing id.
    private static readonly Guid Namespace = new("5b0c5e1a-9f84-4c2f-8f1e-3c6d2a7b4e90");

    /// <summary>Returns the derived id, or <c>null</c> when no machine id is available.</summary>
    public static Guid? TryDerive()
    {
        string? machineId = ReadMachineIdentifier();
        return machineId is null ? null : CreateNameUuid(machineId);
    }

    private static string? ReadMachineIdentifier()
    {
        string? platformId = null;
        if (OperatingSystem.IsWindows())
        {
            string? windows = ReadWindowsMachineGuid();
            if (windows is not null)
                platformId = "windows:" + windows;
        }
        else if (OperatingSystem.IsLinux())
        {
            string? linux = ReadFirstExistingFile("/etc/machine-id", "/var/lib/dbus/machine-id");
            if (linux is not null)
                platformId = "linux:" + linux;
        }
        else if (OperatingSystem.IsMacOS())
        {
            string? mac = ReadMacOsPlatformUuid();
            if (mac is not null)
                platformId = "macos:" + mac;
        }

        if (platformId is not null)
            return platformId;

        string host = Environment.MachineName.Trim();
        return host.Length == 0 ? null : "host:" + host;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey? key = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
                if (key?.GetValue("MachineGuid") is string value)
                {
                    value = value.Trim();
                    if (value.Length > 0)
                        return value;
                }
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
            {
                // The other registry view may still expose the value.
            }
        }
        return null;
    }

    private static string? ReadFirstExistingFile(params string[] paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                string value = File.ReadAllText(path).Trim();
                if (value.Length > 0)
                    return value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    [SupportedOSPlatform("macos")]
    private static string? ReadMacOsPlatformUuid()
    {
        try
        {
            var startInfo = new ProcessStartInfo("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process? process = Process.Start(startInfo);
            if (process is null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode != 0)
                return null;

            const string marker = "\"IOPlatformUUID\"";
            foreach (string line in output.Split('\n'))
            {
                int key = line.IndexOf(marker, StringComparison.Ordinal);
                if (key < 0)
                    continue;

                int firstQuote = line.IndexOf('"', key + marker.Length);
                int secondQuote = firstQuote < 0 ? -1 : line.IndexOf('"', firstQuote + 1);
                if (secondQuote <= firstQuote)
                    continue;

                string value = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
                if (value.Length > 0)
                    return value;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Unable to read the macOS platform UUID: {exception.Message}");
            return null;
        }
    }

    private static Guid CreateNameUuid(string name)
    {
        byte[] input = Encoding.UTF8.GetBytes(Namespace.ToString("N") + "\n" + name);
        byte[] hash = SHA1.HashData(input);

        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5, name-based
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        return new Guid(
            (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]),
            (ushort)(bytes[4] << 8 | bytes[5]),
            (ushort)(bytes[6] << 8 | bytes[7]),
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
    }
}
