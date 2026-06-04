using System;

namespace YuNotes.Models;

public sealed class FolderInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ParentId { get; set; }
}
