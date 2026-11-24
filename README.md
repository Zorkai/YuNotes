<div align="center">

# ✒️ YuNotes

**A Windows digital notebook with low-latency pen inking, PDF import/export,<br>and a clean Notability-style UI — built with WinUI 3 and Win2D.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![WinUI](https://img.shields.io/badge/WinUI-3-0078D4?logo=microsoft&logoColor=white)

**[⬇️ Download](#-download)  ·  [✨ Features](#-features)  ·  [🛠️ Build](#-build-from-source)**

</div>

---

## ⬇️ Download

<div align="center">

[<img src="https://crushee.app/assets/img/ms-store.svg" alt="Get it from the Microsoft Store" width="150">](https://apps.microsoft.com/detail/9MZZ8X385HB0)

[![Download from GitHub Releases](https://img.shields.io/badge/GitHub-Releases-24292e?logo=github&logoColor=white&style=for-the-badge)](../../releases)

</div>

- 🏬 **[Microsoft Store](https://apps.microsoft.com/detail/9MZZ8X385HB0)** — recommended: one-click install, signed by Microsoft, automatic updates.
- 📦 **[GitHub Releases](../../releases)** — the latest build of `main`, published automatically on every push. Download `YuNotes.cer`, install it once to *Local Machine → Trusted People*, then open the `.msix`. From then on YuNotes updates itself: it checks for a new release at startup and installs it when you close the app (turn this off in **Settings → Updates**).

> 💻 Requires **Windows 10 1809 (build 17763)** or later. Windows 11 recommended.

---

![Home screen](https://i.imgur.com/Z27quaE.png)

## ✨ Features

**✍️ Inking**
- Pen, highlighter and eraser: erase whole strokes or only what you touch, with a preview of what will be erased
- Pressure-sensitive ink with an adjustable pressure curve and line smoothing
- Pen favorites: save pen color/width combos and switch with **Ctrl+1–5**
- Shapes: rectangle, ellipse, line, triangle, arrow, axes and grid, or draw freehand and hold to snap it into a clean shape
- Ruler: drag and rotate it, and the pen snaps to its edge
- Laser pointer with **Dot** and **Trail** modes; the ink never touches the note
- Tape for studying: cover answers or key terms, tap a strip to reveal it and tap again to hide it (or cover / reveal everything at once); notes reopen fully covered, and exports show what's under the tape
- Pen button bindings: map barrel, eraser tip and top button to any tool

**🎯 Selection & editing**
- Lasso and rectangle select; move, resize, rotate, duplicate, recolor and change line width of the selection
- Type text or paste images straight onto the page
- Stickers: save a lasso selection (a diagram, a stamp, a signature) and drop an editable copy into any note
- Screenshot tool: capture a region and copy, save or insert it into the page
- Undo and redo for ink, page changes and templates

**📄 Documents**
- Import PDFs as note backgrounds, select and copy their text, and add pages from other PDFs
- Export to PDF (ink as vectors, text boxes as real selectable text) or PNG, for all pages or a page range
- Share a note as a PDF or as images through the Windows share window (Mail, Teams, Nearby sharing, …)
- Page templates: blank, grid, dots, lined, Cornell, with adjustable spacing
- Widen a page to the left or right (it snaps to match neighbouring widened pages), or keep scrolling past the end to add a page
- Page sorter: reorder, delete and jump between pages; bookmark pages
- Autosave and version history: go back to an earlier version of a note
- `.yunote` files are SQLite, so every stroke stays editable after you close and reopen

**🔎 Finding things**
- Find in note (**Ctrl+F**) searches your handwriting, PDF text and text boxes
- Search from Home looks inside every note, not just titles
- Tags: tag notes from Home, filter by tag across folders from the sidebar, or search `#tag`
- Smart folders: saved searches such as "tagged #exam, edited in the last 7 days" that gather matching notes from every folder

**🗂️ Organizing**
- Home screen with folders (with colors), pinned notes, a "continue where you left off" row, and sorting by date, name or folder
- Trash and Archive, with restore
- Tabs: keep several notes open in one window (**Ctrl+T**, **Ctrl+W**, **Ctrl+Tab**, or Ctrl+click a note on Home); each tab keeps its own undo history
- Side by side: open a second note read-only next to the one you're editing

**🎤 Presenting**
- Presentation mode (**F5**): full screen, page fitted to the screen, laser pointer ready; arrow keys step pages and **Esc** exits

**🖐️ Input & look**
- Configurable palm rejection; touch pans and zooms, your hand never draws
- Pinch-to-zoom and an optional zoom slider
- Floating toolbar you can dock to any edge or drag anywhere, resize, and reorder or hide tools in
- Light, dark or system theme with a liquid-glass toolbar
- Automatic updates for GitHub installs (can be turned off in Settings)

![Editor](https://i.imgur.com/pRc8cRL.png)
---

## 🛠️ Build from source

### 📋 Prerequisites

| Tool | Notes |
|------|-------|
| [.NET 8 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) | Verify with `dotnet --version` → `8.0.x` |
| [Visual Studio 2022](https://visualstudio.microsoft.com/vs/community/) | Workload: **.NET Desktop Development** · Component: **Windows App SDK C# Templates** |

> 💡 **VS Code alternative:** install the C# Dev Kit extension and the [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (x64, Runtime + Singleton installer).

### ▶️ Run

Open `YuNotes.sln` in Visual Studio and press **F5**. Visual Studio deploys YuNotes as a package, which it needs to find its icon font.

From the command line, build an unpackaged copy into its own folder and run that:

```powershell
dotnet build src/YuNotes/YuNotes.csproj -c Debug -p:WindowsPackageType=None -p:OutDir="$PWD\src\YuNotes\bin\x64\DevUnpackaged"
```

```powershell
.\src\YuNotes\bin\x64\DevUnpackaged\YuNotes.exe
```

> ⚠️ Plain `dotnet run` starts the packaged app without its package, so every toolbar icon shows as an empty box.

### 🚀 First run

| Data | Location |
|------|----------|
| Notes (`.yunote` files) | `%USERPROFILE%\Documents\YuNotes` |
| App settings | `%LOCALAPPDATA%\YuNotes\settings.json` |
| Pen / palm rejection / color presets | Settings page — gear icon, top right |

---

## 📁 Project layout

```
src/YuNotes/
├── Views/        HomePage · EditorPage (split into partial files by area) · SettingsPage
├── Controls/     InkCanvasControl · PageCanvas · ColorPicker · WidthPicker · LiquidGlassBackdrop
├── Models/       Document · Stroke · ShapeGeometry · AppSettings
├── Services/     DocumentService (SQLite) · PDF import/export · SearchIndex · VersionHistory · NoteTags · UpdateService
├── Rendering/    Win2D page renderer · PDF vector writer
├── Input/        PalmRejection · PenButtonRouter · PressureCurve
├── Tools/        ITool + Pen · Highlighter · Eraser · Text · Image · Lasso · Rect · Shape · Laser
└── Themes/       Fluent colors & styles
```

<div align="center">

Made with ✒️ &amp; Win2D · Built for Windows 10 &amp; 11

</div>
