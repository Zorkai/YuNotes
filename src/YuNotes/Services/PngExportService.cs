using System.IO;

namespace YuNotes.Services;

public sealed class PngExportService
{
    public void ExportPage(byte[] composedPng, string outputPath) =>
        File.WriteAllBytes(outputPath, composedPng);

    public void ExportAll(byte[][] composedPngs, string folder, string baseName)
    {
        Directory.CreateDirectory(folder);
        for (int i = 0; i < composedPngs.Length; i++)
            File.WriteAllBytes(Path.Combine(folder, $"{baseName}-{i + 1:D3}.png"), composedPngs[i]);
    }
}
