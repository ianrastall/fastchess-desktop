namespace FastchessDesktop.Tests;

/// <summary>
/// The stand-ins for fastchess and a UCI engine that the native build produces with its tests
/// (native/tests/fake_fastchess.cpp, fake_uci_engine.cpp). The test projects copy them next to
/// the test assembly. Shared by the Core and ViewModels tests.
/// </summary>
internal static class FakePrograms
{
    public static string Fastchess => Locate("fake_fastchess");

    public static string UciEngine => Locate("fake_uci_engine");

    private static string Locate(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        Assert.True(File.Exists(path), $"{path} is missing: build native/ with its tests first (see README).");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        return path;
    }
}
