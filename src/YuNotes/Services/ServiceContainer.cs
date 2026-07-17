namespace YuNotes.Services;

public sealed class ServiceContainer
{
    public SettingsService Settings { get; } = new();
    public DocumentService Documents { get; } = new();
    public PdfImportService PdfImport { get; } = new();
    public PdfExportService PdfExport { get; } = new();
    public PdfContainerService PdfContainer { get; } = new();
    public PdfTextExtractor PdfText { get; } = new();
    public PngExportService PngExport { get; } = new();
    public TemplateService Templates { get; } = new();
    public HistoryService History { get; } = new();

    public void Initialize()
    {
        Settings.Load();
        Documents.EnsureFolder(Settings.Current.DocumentsFolder);
    }
}
