using System;
using System.IO;
using System.Text.Json;
using YuNotes.Models;

namespace YuNotes.Services;

public sealed class SettingsService
{
    private readonly string _path;
    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuNotes");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) Current = loaded;
            }
        }
        catch { /* keep defaults on corruption */ }

        if (string.IsNullOrEmpty(Current.DocumentsFolder))
        {
            Current.DocumentsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YuNotes");
        }
        Save();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
        Changed?.Invoke();
    }
}
