using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Repro.Tests;

public class TrivialTests
{
    [Fact]
    public void One_Plus_One_Is_Two() => Assert.Equal(2, 1 + 1);

    [Fact]
    public void True_Is_True() => Assert.True(true);
}

/// <summary>
/// Reproduces the MSBuild-style "child process inherits the parent's redirected
/// handles and outlives it" pattern. A real MSBuild unit test frequently spawns
/// child processes (ToolTask, Exec, node reuse, etc.). When the test host is
/// itself launched with redirected stdout/stderr by a driver tool (which is
/// exactly what CTS's testingplatform backend does via --print-console-output),
/// any grandchild that inherits those handles keeps the pipe's write end open.
///
/// * Linux/macOS: fds are close-on-exec; the grandchild does NOT inherit the
///   host->driver pipe, so the driver's read reaches EOF and the run completes.
/// * Windows: Process.Start with redirected streams sets bInheritHandles=TRUE,
///   so the grandchild inherits the host->driver stdout write handle. The
///   driver's read never sees EOF while the grandchild lives -> hang.
///
/// Set REPRO_SPAWN_CHILD=0 to disable spawning (used for the control run).
/// </summary>
public class SpawnsLingeringChildTests
{
    [Fact]
    public void Test_That_Spawns_A_Lingering_Child()
    {
        if (Environment.GetEnvironmentVariable("REPRO_SPAWN_CHILD") == "0")
        {
            // Control run: behave like an ordinary test that spawns nothing.
            Assert.True(true);
            return;
        }

        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ping -n 120 == ~120s of lifetime, no extra tools required.
            psi = new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1");
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sleep", "120");
        }

        // Redirecting the child's streams is what forces bInheritHandles=TRUE on
        // Windows -- the same thing MSBuild's ToolTask/Exec do.
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        Process child = Process.Start(psi)!;

        // Intentionally do NOT wait for or kill the child. The test returns
        // immediately; the grandchild lingers, holding the inherited handle.
        Assert.True(child.Id > 0);
    }
}
