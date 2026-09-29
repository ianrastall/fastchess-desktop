using System.Runtime.InteropServices;
using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

public enum OutputStream
{
    StandardOutput,
    StandardError,
}

public readonly record struct OutputLine(OutputStream Stream, string Text);

/// <summary>What to run. Arguments are passed individually, so no manual quoting is needed.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null)
{
    /// <summary>A copy-pasteable command line for logs and previews.</summary>
    public string DisplayCommand => CommandLine.Format([FileName, .. Arguments]);
}

public sealed record ProcessResult(int ExitCode, TimeSpan Duration, bool Cancelled);

/// <summary>Runs an external tool through fcd_core and streams its output line by line.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts the process and waits for it to exit. Each output line is passed to onLine from a
    /// background thread, one at a time. Cancelling stops the whole process tree and returns
    /// Cancelled = true. A missing program throws FileNotFoundException.
    /// </summary>
    public static Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine>? onLine,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var cancel = new NativeCancellation(cancellationToken);
        var state = GCHandle.Alloc(new CallbackBridge.State(onLine, null));
        try
        {
            FcdStatus status;
            NativeMethods.FcdProcessResult r;
            unsafe
            {
                status = NativeMethods.ProcessRun(spec.FileName, NativeMethods.ToJson(spec.Arguments), spec.WorkingDirectory,
                    CallbackBridge.Line, GCHandle.ToIntPtr(state), cancel.Handle, out r);
            }
            NativeMethods.CheckProcess(status, cancellationToken);
            return new ProcessResult(r.ExitCode, TimeSpan.FromMilliseconds(r.DurationMs), r.Cancelled != 0);
        }
        finally
        {
            state.Free();
        }
    }, CancellationToken.None);
}
