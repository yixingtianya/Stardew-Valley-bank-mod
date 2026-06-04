using System.Runtime.InteropServices;

namespace BankMod.UI;

internal static class DeviceHelper
{
    public static readonly bool IsAndroid =
        RuntimeInformation.IsOSPlatform(OSPlatform.Create("Android"));

    public const int AndroidViewportWidth = 1280;
    public const int AndroidViewportHeight = 720;

    public static int MenuHeight(int pcHeight, int androidHeight) =>
        IsAndroid ? androidHeight : pcHeight;

    public static int MenuWidth(int pcWidth, int androidWidth) =>
        IsAndroid ? androidWidth : pcWidth;
}
