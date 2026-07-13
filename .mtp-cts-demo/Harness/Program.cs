using System.Diagnostics;

// mini-cts: a faithful, self-contained stand-in for CTS's "testingplatform"
// backend. The real CTS tool lives on an internal DevDiv feed that public
// GitHub Actions runners cannot reach, so this harness reproduces the two
// things CTS actually does that matter for the hang:
//
//   1. Launch the xUnit v3 / MTP test host as a child process with its
//      standard output/error REDIRECTED (CTS does this to capture and relay
//      console output via --print-console-output).
//   2. Read that output stream to completion (EOF) and wait for the host to
//      finish before reporting results.
//
// The captured hang in CTS is exactly a read/accept that never completes.
// This harness makes the same call: read the host's stdout to EOF. If a
// grandchild process inherits the host->harness pipe handle (Windows), EOF
// never arrives and the read blocks; on Linux/macOS the handle is
// close-on-exec, EOF arrives promptly, and the run completes.
//
// Usage: mini-cts <path-to-test-host-exe-or-dll> [extra args...]
//
// The harness NEVER hangs forever: a bounded watchdog turns a blocked read
// into a clear "HANG DETECTED" verdict with a non-zero exit code.

const int DrainTimeoutSeconds = 25;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: mini-cts <test-host> [args...]");
    return 64;
}

string host = args[0];
string extraArgs = string.Join(' ', args.Skip(1));

var psi = new ProcessStartInfo
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};

// A .dll is launched via `dotnet <dll>`; a native/apphost exe is launched directly.
if (host.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
{
    psi.FileName = "dotnet";
    psi.ArgumentList.Add(host);
    foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
}
else
{
    psi.FileName = host;
    foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
}

Console.WriteLine($"[mini-cts] OS               : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"[mini-cts] launching host   : {psi.FileName} {host} {extraArgs}");

var sw = Stopwatch.StartNew();
using var proc = Process.Start(psi)!;

// Read stdout/stderr asynchronously -- the recommended pattern that a real
// tool uses. These tasks complete only when the write ends of the pipes are
// FULLY closed (host + any handle-inheriting grandchildren).
Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

// The test host process itself exits once tests finish, regardless of the
// lingering grandchild.
bool hostExited = proc.WaitForExit(DrainTimeoutSeconds * 1000);
Console.WriteLine($"[mini-cts] host process exit : exited={hostExited} exitCode={(hostExited ? proc.ExitCode.ToString() : "n/a")} after {sw.Elapsed.TotalSeconds:F1}s");

// Now wait for the output pipes to reach EOF. THIS is the operation that
// hangs on Windows when a grandchild inherited the pipe handle.
bool drained = Task.WaitAll([stdoutTask, stderrTask], DrainTimeoutSeconds * 1000);
sw.Stop();

Console.WriteLine();
Console.WriteLine("================ VERDICT ================");
if (drained)
{
    Console.WriteLine($"[mini-cts] RESULT: COMPLETED  (stdout/stderr reached EOF in {sw.Elapsed.TotalSeconds:F1}s)");
    Console.WriteLine("[mini-cts] The driver's read returned normally -> CTS-style run would succeed.");
    Console.WriteLine("=========================================");
    // Print the host output we captured, for evidence.
    Console.WriteLine("---- captured host stdout ----");
    Console.WriteLine(stdoutTask.Result.Trim());
    return 0;
}
else
{
    Console.WriteLine($"[mini-cts] RESULT: HANG DETECTED  (stdout/stderr did NOT reach EOF within {DrainTimeoutSeconds}s)");
    Console.WriteLine($"[mini-cts]         host process exited={hostExited} but the output pipe is still held open");
    Console.WriteLine("[mini-cts]         by a grandchild that inherited the handle -> CTS-style run HANGS here.");
    Console.WriteLine("=========================================");

    // Best-effort cleanup so we don't leak the lingering child on the runner.
    TryKill(proc);
    return 2;
}

static void TryKill(Process proc)
{
    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
    catch { /* best effort */ }
}
