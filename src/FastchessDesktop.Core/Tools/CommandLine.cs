using FastchessDesktop.Core.Native;

namespace FastchessDesktop.Core.Tools;

/// <summary>Windows command-line quoting and splitting (CommandLineToArgvW rules), implemented in fcd_core.</summary>
public static class CommandLine
{
    /// <summary>Joins arguments into one command line, quoting where required.</summary>
    public static string Format(IEnumerable<string> arguments)
    {
        var status = NativeMethods.CmdlineFormat(NativeMethods.ToJson(arguments), out var text);
        return NativeMethods.TakeChecked(status, text);
    }

    public static string Quote(string argument)
    {
        var status = NativeMethods.CmdlineQuote(argument, out var text);
        return NativeMethods.TakeChecked(status, text);
    }

    /// <summary>Splits user-typed text such as "-t tags.txt --plycount" into arguments.</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var status = NativeMethods.CmdlineSplit(commandLine, out var json);
        return NativeMethods.TakeStringList(status, json);
    }
}
