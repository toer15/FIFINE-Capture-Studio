using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace FifineCaptureStudio.Core.Monitoring;

/// <summary>
/// Intel PresentMon 2.6.0 portable console capture. It uses ETW and does not inject into, patch or
/// modify the target game. The binary is never downloaded silently: the UI calls
/// <see cref="InstallAsync"/> only after the user asks for it, and the pinned hash is verified first.
/// PresentMon is MIT licensed, Copyright (C) 2017-2024 Intel Corporation.
/// </summary>
public static class PresentMonTool
{
    public const string Version = "2.6.0";
    public const string FileName = "PresentMon.exe";
    public const string DownloadUrl =
        "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
    public const string License = "MIT (Copyright (C) 2017-2024 Intel Corporation)";
    public const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const long SizeBytes = 980_320;

    public static string ToolDirectory => Path.Combine(AppPaths.Root, "tools", "PresentMon-" + Version);
    public static string InstalledPath => Path.Combine(ToolDirectory, FileName);

    public static string? ResolvePath()
    {
        var candidates = new[]
        {
            InstalledPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon", FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Intel", "PresentMon", FileName)
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static bool IsInstalled => ResolvePath() is not null;

    public static bool Verify(string path, out string message)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length != SizeBytes)
            {
                message = $"PresentMon size mismatch: {length:N0} bytes, expected {SizeBytes:N0}.";
                return false;
            }

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, Sha256, StringComparison.Ordinal))
            {
                message = "PresentMon SHA-256 mismatch: " + hash + ".";
                return false;
            }

            message = $"PresentMon {Version} verified · {hash[..16]}…";
            return true;
        }
        catch (Exception ex)
        {
            message = "Could not verify PresentMon: " + ex.Message;
            return false;
        }
    }

    /// <summary>Downloads the pinned portable executable after an explicit UI action.</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(
        IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(ToolDirectory);
        var temporary = InstalledPath + ".part";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? SizeBytes;

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = File.Create(temporary))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(total > 0 ? copied / (double)total : 0);
                }
            }

            if (!Verify(temporary, out var verification))
            {
                File.Delete(temporary);
                return (false, verification);
            }

            File.Move(temporary, InstalledPath, true);
            WriteLicense();
            return (true, $"Installed PresentMon {Version} · {SizeBytes / 1024} KB · {Sha256[..16]}…");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return (false, "PresentMon download failed: " + ex.Message);
        }
    }

    public static void WriteLicense()
    {
        try
        {
            Directory.CreateDirectory(ToolDirectory);
            File.WriteAllText(Path.Combine(ToolDirectory, "LICENSE.txt"),
                "PresentMon " + Version + "\n" +
                "Source: https://github.com/GameTechDev/PresentMon\n" +
                "Download: " + DownloadUrl + "\n" +
                "SHA-256: " + Sha256 + "\n\n" +
                "MIT License\n\nCopyright (C) 2017-2024 Intel Corporation\n\n" +
                "Permission is hereby granted, free of charge, to any person obtaining a copy of this software\n" +
                "and associated documentation files (the \"Software\"), to deal in the Software without\n" +
                "restriction, including without limitation the rights to use, copy, modify, merge, publish,\n" +
                "distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the\n" +
                "Software is furnished to do so, subject to the following conditions:\n\n" +
                "The above copyright notice and this permission notice shall be included in all copies or\n" +
                "substantial portions of the Software.\n\n" +
                "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,\n" +
                "INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR\n" +
                "PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE\n" +
                "FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR\n" +
                "OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER\n" +
                "DEALINGS IN THE SOFTWARE.\n");
        }
        catch
        {
            // Informational only.
        }
    }
}
