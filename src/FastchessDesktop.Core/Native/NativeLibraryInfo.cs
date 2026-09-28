using System.Runtime.InteropServices;

namespace FastchessDesktop.Core.Native;

/// <summary>Version information for the loaded fcd_core library.</summary>
public static class NativeLibraryInfo
{
    private static readonly Lazy<(int Abi, string Version)> Info = new(() =>
        (NativeMethods.AbiVersion(), Marshal.PtrToStringUTF8(NativeMethods.Version()) ?? "?"));

    public static string Version => Info.Value.Version;

    public static int AbiVersion => Info.Value.Abi;

    /// <summary>Throws if the native library is missing or built for another ABI version.</summary>
    public static void EnsureCompatible()
    {
        int abi;
        try
        {
            abi = AbiVersion;
        }
        catch (DllNotFoundException e)
        {
            throw new FcdException(FcdStatus.Internal,
                "fcd_core native library not found next to the application. Build the native project first. " + e.Message);
        }
        if (abi != NativeMethods.ExpectedAbiVersion)
            throw new FcdException(FcdStatus.Internal,
                $"fcd_core ABI version {abi} does not match expected version {NativeMethods.ExpectedAbiVersion}.");
    }
}
