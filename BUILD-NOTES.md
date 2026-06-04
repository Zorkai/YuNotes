# Build & polish notes

## Initial run

1. Install .NET 8 SDK and (recommended) Visual Studio 2022 with the *.NET Desktop Development* workload + *Windows App SDK C# Templates* component. See `README.md` for direct links.
2. From the project root in PowerShell:
   ```powershell
   dotnet restore
   dotnet build -c Debug
   dotnet run --project src/YuNotes/YuNotes.csproj
   ```
3. On first launch, YuNotes creates `%USERPROFILE%\Documents\YuNotes` for notes and `%LOCALAPPDATA%\YuNotes\settings.json` for app config.

## What's wired and working

- Home page with recent-document grid (sorted by modified date).
- New note, Open `.yunote`, Import PDF (creates a new note with PDF pages as backgrounds).
- Editor with: pen, highlighter, eraser (stroke + pixel), text, image, lasso, rectangle select.
- Shared width slider and color picker (5 presets + custom).
- Page templates: blank / grid / dots / lined / Cornell, rendered via Win2D under strokes.
- Add page, save (`Ctrl+S` via Save button), export to PDF or PNGs.
- Screenshot tool (uses Windows Snipping Tool, picks from clipboard, lets you place it as an image).
- Settings page: configure pen-button bindings (barrel / eraser tip / top), palm rejection (enable, ignore-touch-while-pen, grace ms), default widths, preset colors, documents folder.
- SQLite-backed `.yunote` files — strokes/text/images persist and are re-editable after close + reopen.
- Palm rejection: pen always accepted; touch suppressed for `grace ms` after a pen sample.

## Areas that will need iteration on your hardware

These are scaffolded but device behavior varies — expect to tune on your Asus pen:

- **Pen "top button" detection.** Different pens report the second button differently: some as `IsBarrelButtonPressed` (already mapped), some as `IsXButton1Pressed`, some via Bluetooth as keyboard shortcuts. If the top button doesn't change tool in YuNotes, run the app, hover the pen, press the button, and add a `Debug.WriteLine` in `Input/PenButtonRouter.cs` to inspect `props` flags — then map whichever flag your pen sets.
- **Stroke smoothing.** `Rendering/PageRenderer.DrawStroke` uses a midpoint-bezier approximation that looks good for normal-speed handwriting; for ultra-fast strokes you may want to add Catmull-Rom or smoothing in `Tools/PenTool` before storing points.
- **Background bitmap caching.** `PageRenderer` decodes each PDF-page PNG on every redraw. For long imported PDFs this is a perf cliff. Cache `CanvasBitmap` per page on the `PageCanvas` when first decoded.
- **High-refresh-rate sync.** Win2D's `CanvasControl` already vsyncs to the monitor. If you have a 120Hz / 240Hz display and want true matching, switch `CanvasControl` to `CanvasAnimatedControl` and drive redraws from the `Update` event.
- **Selection move/scale.** Lasso/rect select highlight items but a transform handle (drag/scale/delete) is not yet drawn. The data model already tracks `SelectedStrokeIds` etc. — easiest place to add handles is on top of the `PageCanvas.OnDraw` selection block.
- **Text editing UX.** `TextTool` creates a `TextElement` but inline keyboard editing isn't implemented — for now the text is set when an element is created (you can plug a textbox overlay in `PageCanvas`).
- **Pinch-zoom.** The `ScrollViewer` host has `ZoomMode="Enabled"` which gives native two-finger pinch. The bottom-bar slider also drives `ChangeView`. If you find touch zoom fights palm rejection, exclude touch from the pen handler (already done) — pinch still goes to the ScrollViewer.

## Known build gotchas

- If `dotnet restore` complains about Windows App SDK targets missing, install Visual Studio 2022 with the *Windows App SDK C# Templates* component — that adds the project SDK MSBuild targets.
- If you see `MSB3073 ... rc.exe`, install the *Windows 10/11 SDK* (10.0.22621 or newer) via VS Installer.
- The `WindowsPackageType=None` flag builds an unpackaged exe — easier to run during development. To ship an MSIX later, flip it back and add a `Package.appxmanifest`.

## File layout (quick reference)

| Layer | Folder | Key files |
|------|--------|-----------|
| App shell | `src/YuNotes/` | `App.xaml(.cs)`, `MainWindow.xaml(.cs)` |
| Theme | `Themes/` | `Colors.xaml`, `Styles.xaml` |
| Views | `Views/` | `HomePage`, `EditorPage`, `SettingsPage` |
| Controls | `Controls/` | `InkCanvasControl`, `PageCanvas`, `ColorPicker`, `WidthPicker` |
| Data | `Models/` | `Document`, `Page`, `Stroke`, `AppSettings` |
| Persistence | `Services/` | `DocumentService` (SQLite), `SettingsService` |
| Files | `Services/` | `PdfImportService`, `PdfExportService`, `PngExportService`, `ScreenshotService` |
| Rendering | `Rendering/` | `PageRenderer` (Win2D), `TemplateService` |
| Input | `Input/` | `PalmRejection`, `PenButtonRouter` |
| Tools | `Tools/` | `PenTool`, `HighlighterTool`, `EraserTool`, `TextTool`, `ImageTool`, `LassoTool`, `RectSelectTool` |
