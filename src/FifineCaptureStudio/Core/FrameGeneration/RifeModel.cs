using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace FifineCaptureStudio.Core.FrameGeneration;

/// <summary>
/// Provenance and local installation of the RIFE model. The model is never downloaded silently:
/// the UI calls <see cref="DownloadAsync"/> after the user asks for it, and every byte is checked
/// against the pinned SHA-256 before it is moved into place.
/// </summary>
public static class RifeModel
{
    public const string DisplayName = "RIFE 4.25 lite (ONNX)";
    public const string FileName = "rife_v425lite.onnx";
    public const string SourceUrl = "https://huggingface.co/t9749/rife-4.25-lite-onnx";
    public const string DownloadUrl = SourceUrl + "/resolve/main/" + FileName;
    public const string Version = "4.25 lite";
    public const string Architecture = "Practical-RIFE IFNet_HDv3, scales 16/8/4/2/1";
    public const string License = "MIT (Copyright (c) 2021 hzwer)";
    public const string Sha256 = "702802d0862ab78f2d72133c6bc3768e7e89b81756f7cd51d1055a2fe3d06577";
    public const long SizeBytes = 22_677_513;
    public const int InputMultiple = 64;

    public static string ModelDirectory => Path.Combine(AppPaths.Root, "models");
    public static string DefaultPath => Path.Combine(ModelDirectory, FileName);

    public static string Describe() =>
        $"{DisplayName} · {Version} · opset 17 · {License} · {Sha256[..12]}…";

    public static bool IsInstalled(string? path = null) => ResolvePath(path) is not null;

    public static string? ResolvePath(string? explicitPath = null)
    {
        var candidates = new[]
        {
            explicitPath,
            Environment.GetEnvironmentVariable("FIFINE_RIFE_MODEL"),
            DefaultPath,
            Path.Combine(AppPaths.AppDir, "models", FileName)
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool Verify(string path, out string message)
    {
        try
        {
            if (!File.Exists(path))
            {
                message = "Model file not found.";
                return false;
            }

            var length = new FileInfo(path).Length;
            if (length != SizeBytes)
            {
                message = $"Size mismatch: {length:N0} bytes, expected {SizeBytes:N0}.";
                return false;
            }

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, Sha256, StringComparison.Ordinal))
            {
                message = $"SHA-256 mismatch: {hash}.";
                return false;
            }

            message = $"{DisplayName} verified · {hash[..16]}…";
            return true;
        }
        catch (Exception ex)
        {
            message = "Could not verify model: " + ex.Message;
            return false;
        }
    }

    /// <summary>Downloads and atomically installs the pinned model. Called only from an explicit user action.</summary>
    public static async Task<(bool Ok, string Message)> DownloadAsync(IProgress<double>? progress, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(ModelDirectory);
        var target = DefaultPath;
        var temporary = target + ".part";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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

            File.Move(temporary, target, true);
            return (true, $"Installed {DisplayName} · {SizeBytes / 1024 / 1024} MB · {Sha256[..16]}…");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            return (false, "Model download failed: " + ex.Message);
        }
    }

    public static string LicensePath => Path.Combine(ModelDirectory, "RIFE-LICENSE.txt");

    public static void WriteLicenseFile()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ModelDirectory);
            File.WriteAllText(LicensePath,
                "RIFE 4.25 lite ONNX\n" +
                "Source: " + SourceUrl + "\n" +
                "Upstream: https://github.com/hzwer/Practical-RIFE (MIT)\n" +
                "Exported by t9749; file unmodified.\n" +
                "SHA-256: " + Sha256 + "\n\n" +
                "MIT License\n\nCopyright (c) 2021 hzwer\n\n" +
                "Permission is hereby granted, free of charge, to any person obtaining a copy\n" +
                "of this software and associated documentation files (the \"Software\"), to deal\n" +
                "in the Software without restriction, including without limitation the rights\n" +
                "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n" +
                "copies of the Software, and to permit persons to whom the Software is\n" +
                "furnished to do so, subject to the following conditions:\n\n" +
                "The above copyright notice and this permission notice shall be included in all\n" +
                "copies or substantial portions of the Software.\n\n" +
                "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n" +
                "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n" +
                "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n" +
                "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n" +
                "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n" +
                "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n" +
                "SOFTWARE.\n");
        }
        catch
        {
            // Licence text is informational; the model path stays usable without it.
        }
    }
}
