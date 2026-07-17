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
- 📦 **[GitHub Releases](../../releases)** — portable build: unzip and run `YuNotes.exe`, no installer required (signed with a developer certificate, so Windows asks you to trust it once).

> 💻 Requires **Windows 10 1809 (build 17763)** or later. Windows 11 recommended.

---

![Home screen](https://i.imgur.com/Z27quaE.png)

## ✨ Features

**✍️ Inking**
- Pen, highlighter, eraser — stroke erase or pixel erase
- Pressure-sensitive rendering via Win2D bezier smoothing
- Pen button bindings — map barrel / eraser tip / top button to any tool

**🎯 Selection & editing**
- Lasso and rectangle select
- Type text or paste images directly onto the canvas
- Screenshot tool — region capture straight into the current page

**📄 Documents**
- Import PDFs as note backgrounds, export back to PDF or PNG
- Page templates — blank, grid, dots, lined, Cornell
- `.yunote` files are SQLite — every stroke stays editable after close and reopen

**🧭 Navigation**
- Pinch-to-zoom and bottom-bar zoom slider
- Home screen with recent documents sorted by last modified

**🖐️ Input**
- Configurable palm rejection with adjustable grace window
- Touch and pen are handled independently — your hand never draws

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

```powershell
dotnet restore
dotnet build -c Debug
dotnet run --project src/YuNotes/YuNotes.csproj
```

Or open `YuNotes.sln` in Visual Studio and press **F5**.

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
├── Views/        HomePage · EditorPage · SettingsPage
├── Controls/     InkCanvasControl · PageCanvas · ColorPicker · WidthPicker
├── Models/       Document · Page · Stroke · TextElement · ImageElement
├── Services/     DocumentService (SQLite) · PDF/PNG/Screenshot services
├── Rendering/    Win2D renderers — template backgrounds, stroke drawing
├── Input/        PalmRejection · PenButtonRouter
├── Tools/        ITool + Pen · Highlighter · Eraser · Text · Image · Lasso · Rect
└── Themes/       Fluent colors & styles
```

<div align="center">

Made with ✒️ &amp; Win2D · Built for Windows 10 &amp; 11

</div>
