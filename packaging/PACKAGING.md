# YuNotes packaging & distribution

YuNotes is a **packaged (MSIX) WinUI 3 app**. That means it installs like any
normal Windows app: it gets a Start-menu entry, shows up in **Settings → Apps**,
and installs / updates / uninstalls the standard way. MSIX is also the format the
Microsoft Store requires, so the same project builds both.

The app used to be *unpackaged* (a loose `YuNotes.exe`). The switch is in
`src/YuNotes/YuNotes.csproj` (removed `WindowsPackageType=None`) plus
`src/YuNotes/Package.appxmanifest` and the logo assets under `src/YuNotes/Assets`.

---

## Set the version (one global place)

The app version lives in **one** spot: `<YuNotesVersion>` in the repo-root
[`Directory.Build.props`](../Directory.Build.props). Change it there and every
build picks it up automatically:

- the assembly / file version compiled into `YuNotes.exe`,
- the MSIX package version in `Package.appxmanifest` (stamped in at build time —
  don't hand-edit it there; a build overwrites it to match),
- the version in the `.msix` / `.msixupload` / standalone-exe output.

```xml
<!-- Directory.Build.props -->
<YuNotesVersion>1.0.0.0</YuNotesVersion>
```

Use the four-part `MAJOR.MINOR.BUILD.REVISION` form — the MSIX identity requires
all four parts. For a Store update, bump this (Partner Center rejects a resubmit
with the same or a lower version).

---

## Install it on this PC (sideload)

Everything is scripted in this folder.

### 1. Build a signed package

```powershell
packaging\build.ps1
```

- On first run this creates a **self-signed dev certificate** (`CN=YuNotes`) and
  exports `YuNotes.pfx` + `YuNotes.cer` here. The cert subject must match the
  manifest's `Identity/@Publisher`.
- Produces `packaging\output\YuNotes_<version>_x64_Test\YuNotes_<version>_x64.msix`.
- Close the running app first if it's the same Configuration you're building.

### 2. Install (trusts the dev cert, then installs)

Run from an **elevated** PowerShell (Run as administrator):

```powershell
packaging\install.ps1
```

This imports `YuNotes.cer` into `LocalMachine\TrustedPeople` (so Windows accepts
the self-signed package) and runs `Add-AppxPackage`. YuNotes then appears in the
Start menu and in Settings → Apps.

> The one-time cert trust is only needed for **self-signed** sideloading. Packages
> installed from the Microsoft Store are signed by Microsoft and need none of this.

### 3. Uninstall

```powershell
packaging\uninstall.ps1
```

…or just right-click YuNotes in the Start menu → Uninstall, or remove it from
Settings → Apps. All three do the same thing.

---

## Build a standalone single-file `.exe` (no install, no folder)

If you want a plain portable app instead of an MSIX — one `YuNotes.exe` you can
copy anywhere and double-click, with **no certificate, no install step, and no
supporting folder** — build it with:

```powershell
packaging\publish-exe.ps1
```

…or double-click **`Build Standalone EXE.bat`**.

- Output: `packaging\exe-output\YuNotes.exe` (~104 MB). A `YuNotes.pdb` is emitted
  beside it — that's just debug symbols; you can ignore/delete it, only the `.exe`
  is needed to run.
- The exe is **unpackaged** (`WindowsPackageType=None`) and **self-contained**: it
  bundles the .NET 8 runtime, the Windows App SDK runtime, and every managed/native
  dependency plus `resources.pri` **inside the single file** (`PublishSingleFile` +
  `IncludeAllContentForSelfExtract`). Nothing needs to be preinstalled on the
  target PC.
- On first launch it self-extracts its native components to a per-user temp folder,
  so the very first start is a little slower; later starts are normal.
- This is independent of the MSIX build — the project still builds a packaged app
  by default (`build.ps1` / `build-store.ps1`). The exe build uses the publish
  profile `src/YuNotes/Properties/PublishProfiles/win-x64-exe.pubxml` and touches
  none of the packaged settings.
- Trade-offs vs. MSIX: no Start-menu entry, no auto-update, and **file
  associations (.pdf / .yunote) don't register** (those come from the MSIX
  manifest on install). It's meant for portable / "just run it" distribution.

---

## Publish to the Microsoft Store (later)

The project is already Store-shaped. Remaining steps are account/identity work
that can only be done once you have a Partner Center account:

1. **Enroll** in [Partner Center](https://partner.microsoft.com/dashboard) as an
   app developer (one-time fee).
2. **Reserve the app name** ("YuNotes" or your chosen name) under Apps and games.
   That gives you the real identity values:
   - Package/Identity/**Name** (e.g. `12345Publisher.YuNotes`)
   - Package/Identity/**Publisher** (e.g. `CN=ABCD1234-...`)
   - Properties/**PublisherDisplayName**
3. **Put those values into** `src/YuNotes/Package.appxmanifest` (replacing the
   `YuNotes.YuNotes` / `CN=YuNotes` placeholders). In Visual Studio you can instead
   right-click the project → Publish → **Associate App with the Store**, which
   fills them in for you.
4. **Build a Store package** — VS: Project → Publish → *Create App Packages…* →
   *Microsoft Store*, which produces a `.msixupload`. (CLI equivalent:
   `msbuild /p:Configuration=Release /p:AppxPackageSigningEnabled=false
   /p:UapAppxPackageBuildMode=StoreUpload`.) Store packages are **not**
   self-signed — Microsoft signs them, so leave signing off.
5. **Upload** the `.msixupload` in the Partner Center submission, fill in the
   store listing (description, screenshots, the 300×300 Store logo is already in
   Assets), set pricing/markets, and submit for certification.

### Notes / options for Store

- **Architectures**: currently x64 only (`<Platforms>x64</Platforms>`). To reach
  Arm64 devices natively, add `arm64` there and to `<RuntimeIdentifiers>`, and
  build a bundle. Not required to submit.
- **Package size**: the sideload package is ~167 MB because the Windows App SDK
  runtime is bundled (`WindowsAppSDKSelfContained=true`) so it installs with no
  prerequisites. For the Store you can drop that to ship a much smaller package
  that takes the Windows App Runtime as a framework dependency (the Store handles
  the dependency automatically). Trade-off: smaller download vs. self-sufficient.
- **Version**: bump `Package.appxmanifest` `Identity/@Version` for each Store
  submission. The 4th digit (revision) must stay `0` for the Store.
