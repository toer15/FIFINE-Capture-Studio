using System.IO;
using FifineCaptureStudio.Core;
using Xunit;

namespace FifineCaptureStudio.Tests;

public class ProcessLaunchTests
{
    [Fact]
    public void Starting_a_process_creates_a_missing_working_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fifine-proc-" + Guid.NewGuid().ToString("N"));

        try
        {
            Assert.False(Directory.Exists(directory));

            using var process = Proc.Start(
                "cmd.exe",
                new[] { "/c", "exit", "0" },
                redirectStdout: false,
                redirectStderr: false,
                redirectStdin: false,
                workingDirectory: directory);

            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
