using System.IO;
using System.IO.MemoryMappedFiles;

namespace TradeDesktop.App.Helpers;

public static class SharedMemoryChecker
{
    public static bool MapExists(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return false;
        }

        try
        {
            using var _ = MemoryMappedFile.OpenExisting(mapName.Trim());
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}