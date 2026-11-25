using System.IO;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

public static class AzurPromiliaImageData
{
    public static byte[] AzurPromiliaAppIconData { get; } = LoadIcon();
    public static byte[] AzurPromiliaPosterData => AzurPromiliaAppIconData;

    private static byte[] LoadIcon()
    {
        using Stream resource = typeof(AzurPromiliaImageData).Assembly.GetManifestResourceStream("AzurPromilia.Icon.png")
            ?? throw new FileNotFoundException("The embedded Azur Promilia icon is missing.");
        using MemoryStream buffer = new();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }
}
