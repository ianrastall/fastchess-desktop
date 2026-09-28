using System.Diagnostics;
using System.Text;

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

/// <summary>Runs an external tool and streams its output line by line.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts the process and waits for it to exit. Each output line is passed to onLine from a
    /// background thread. Cancelling kills the whole process tree and returns Cancelled = true.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine>? onLine,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(spec.FileName))
            throw new FileNotFoundException($"Program not found: {spec.FileName}", spec.FileName);

        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FileName) ?? "",
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();
        process.Start();

        var stdout = PumpAsync(process.StandardOutput, OutputStream.StandardOutput, onLine);
        var stderr = PumpAsync(process.StandardError, OutputStream.StandardError, onLine);

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stopwatch.Elapsed, cancelled);
    }

    private static async Task PumpAsync(StreamReader reader, OutputStream stream, Action<OutputLine>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            onLine?.Invoke(new OutputLine(stream, line));
    }
}
