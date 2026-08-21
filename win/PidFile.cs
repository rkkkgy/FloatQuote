using System.IO;

namespace FloatQuote;

public static class PidFile
{
    public static string Path => System.IO.Path.Combine(ConfigStore.AppDir, ".floatquote.pid");

    public static void Write()
    {
        try { File.WriteAllText(Path, Environment.ProcessId.ToString()); }
        catch (IOException) { }
    }

    public static void Remove()
    {
        try { File.Delete(Path); }
        catch (IOException) { }
    }
}
