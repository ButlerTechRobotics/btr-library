# BTR Library — SOLIDWORKS 2026 add-in

FRCDesignLib-style COTS parts library for Butler Tech Robotics (FRC teams 144 and 325).
A SOLIDWORKS task pane where students search vendor parts and insert them into assemblies.
Part of the BTR app set (BTR Blockout, BTR Calc, BTR Lighten; ButlerTechRobotics GitHub org).
Naming standard: app "BTR <Name>", repo `btr-<name>` → this is **BTR Library** / `btr-library`.

## Layout

```
src/
  BtrLibrary.csproj   SDK-style, net48, x64. References SOLIDWORKS interop from SwApiDir.
  SwAddin.cs          ISwAddin entry point, COM registration (HKLM Addins + HKCU AddInsStartup), task pane setup
  LibraryPane.cs      Task pane UI (WinForms, owner-drawn): header, search, tree, details card, sync, options menu
  Theme.cs            BTR palette (light/dark), embedded fonts, BtrHeader/BtrButton/Card/FieldHost/SectionLabel controls
  LibraryIndex.cs     Scans library folder: Vendor\Category[\Sub]\Part.(SLDPRT|SLDASM|STEP) + optional .meta/.png sidecars
  LibrarySync.cs      Downloads parts listed in library.json from vendors into the library folder (skips existing)
  Inserter.cs         SOLIDWORKS API: STEP→native conversion (cached in _converted), insert into assembly / part
  Preview3D.cs        3D preview: Mesh, MeshCache (%LOCALAPPDATA%\BtrLibrary\Previews), ModelViewer (software z-buffer renderer)
  Support.cs          Settings (HKCU\Software\BtrLibrary), thumbnails, natural sort, icons, embedded assets
  Assets/             brand_10/15/20.png, icon_*.png, Fonts/*.ttf (Barlow, SIL OFL). Embedded with LogicalName = file name.
manifest/library.json Team parts list (copied next to the DLL at build). 355 parts: WCP gears/pulleys/sprockets/bearings/chain + motors, PDH/PDP 2.0, Limelights.
scripts/              install.bat / uninstall.bat (RegAsm /codebase, run as admin), New-FrcLibrary.ps1
```

## Build / install / test loop

1. **Close SOLIDWORKS first.** It locks `BtrLibrary.dll`; builds fail or silently don't take effect otherwise.
2. Build Release (Visual Studio 2026, or `dotnet build src\BtrLibrary.csproj -c Release`, or MSBuild via vswhere).
   Output: `src\bin\Release\net48\BtrLibrary.dll` (+ SolidWorks interop DLLs copied alongside).
3. Register once per folder location: run `scripts\install.bat` **as administrator** (needs HKLM).
   Re-run only if the build output path changes (folder moved). Rebuilding in place needs no re-register.
4. Start SOLIDWORKS 2026 → Task Pane → BTR tab. Crash logs: `%LOCALAPPDATA%\SolidWorks\CXPD\<session>\`
   (the `.log` lists `<AD2> LOAD_ADDIN` lines and an `<AD5>` recent-API-call trace on crashes).

Machine: Windows 11, SOLIDWORKS 2026 SP5.2 at `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`,
two monitors (144 and 96 DPI), Avast installed.

## Hard-won rules (don't regress these)

- **`Environment` is ambiguous** with `SolidWorks.Interop.sldworks.Environment`. Always write `System.Environment`
  in files that `using SolidWorks.Interop.sldworks;`.
- **Interop references must be `<Private>true</Private>`** (copy local). SOLIDWORKS 2026 doesn't GAC them, so RegAsm
  fails with "Could not load SolidWorks.Interop.swpublished" otherwise.
- **Never switch or close the user's active window during an insert.** Opening the part visibly and then closing it
  while active crashed SOLIDWORKS (null active doc in ClearSelectionsNotify). Open/convert files inside
  `using (Hidden(app))` (DocumentVisible false), re-activate the user's doc with `EnsureActive` (only if it changed),
  and close temp docs *after* the user's doc is active again.
- **Never use `IFace2.GetTessTriangles`/`GetTessNorms` on a hidden document.** They read the display tessellation, which a
  never-shown doc doesn't have → access violation in slduiu (`GetTessTriangleCount`), crashed SW 2026-11-18. Use the
  kernel tessellator `IBody2.GetTessellation(null)` → `ITessellation` (facets → fins → vertices), as in `Inserter.AddBody`.
- All SOLIDWORKS API calls happen on the UI thread (task pane event handlers). Background threads only for file IO / HTTP.
- `DisplayWindowFromHandlex64` hosts the WinForms control — no COM-visible UserControl needed.
- Theme must match the BTR web apps (CSS vars in btr-calc `index.html`): charcoal bar `#181818`, 325 blue `#0b3bbf`,
  144 orange `#f26a1b`, accent `#e85d12`; dark variants in `Palette.DarkMode`. Fonts: Barlow / Barlow Condensed;
  brand strip PNGs pre-rendered from the BTR logo SVG with Big Shoulders Display. Theme setting: auto/light/dark.

## Parts data

- WCP CAD links follow `https://wcproducts.info/files/frc/cad/<SKU>.step` (verified on the steel hex bore gear page).
- `ButlerTechRobotics/btr-calc` has `wcp-parts.json` with ~500 WCP SKUs — likely source for generating many more
  library.json entries (spot-check the URL pattern on other product types first).
- Display names: `<N>T [Rounded Hex|Pocketed|Pocketed MotionX] Steel Gear (<SKU>)` — category already says 20DP / hex bore.
- Vendor CAD is linked, not redistributed (sync downloads straight from the vendor).
- Repo: github.com/ButlerTechRobotics/btr-library (public, `main`). Sync's default list is the raw `main` URL
  (`Settings.DefaultManifestUrl`); offline it falls back to the library.json next to the DLL. Push with the AydenLo17
  account (BTR org admin); the default gh account AydenL-3S only has read access to the org.
- WCP entries were generated from btr-calc `wcp-parts.json` (2026-09-30 data) on 2026-11-18, keeping only SKUs whose
  `wcproducts.info/files/frc/cad/<SKU>.step` answered HTTP 200. That pattern does NOT work for belts (no CAD) or the
  Kraken motors at lowercase .step (they're at uppercase `WCP-0940.STEP`; URLs are case-sensitive). No official SystemCore CAD yet (2026-11-18). In this Git Bash, curl can't do HTTPS (returns 000); use PowerShell Invoke-WebRequest.
- Names with `"` or `/` (e.g. `1/2" Hex`): Sync writes the file as `1-2in Hex` and puts `name=` in the .meta so the
  tree shows the real name.

## Status (2026-11-18)

- Builds and loads in SOLIDWORKS 2026 (log shows `BtrLibrary.DLL ... 0.2.0.0 NET`).
- First insert attempt crashed → fixed with the hidden-doc change in Inserter.cs; **needs re-test**:
  new assembly → close the Begin Assembly panel → search `24t` → Insert.
- 3D preview (Inserter.GetPreviewMesh: hidden open → IBody2.GetTessellation → cached mesh). Kernel-tessellation path verified working in SW (2026-11-18). Not yet tested: assemblies, Build/Export/Sync-previews flow in SW.
  **Never build a preview on selection**: converting a STEP steals focus and an unpinned task pane auto-hides. Selection
  shows a cached mesh or the thumbnail + "LOAD 3D" button; ⋯ → "Build 3D previews" does all missing ones; pane calls
  TaskpaneView.ShowView afterwards (ReShow). Cache format v2 = gzip + 16-bit positions (~11x smaller, ~20 KB/gear).
- Preview cache `%LOCALAPPDATA%\BtrLibrary\Meshes`, file name = SHA-1 of part file bytes (+ config hash) → portable.
  WCP STEP downloads are byte-identical across PCs (verified). ⋯ → "Export previews for sharing" copies them to
  `manifest/previews` (shipped next to DLL via csproj); Sync fetches `<manifest url>/previews/<hash>.mesh` or the local
  folder (optional `"previews"` in library.json). Sync can only key previews for config "" (no SW API on the sync thread).
- Not yet verified on real hardware: STEP sync download, STEP conversion, drag-and-drop into graphics area,
  insert-into-part, dark mode in the real task pane.

## Ideas queued

FRC generators (box tube with 0.5" hole pattern, hex shaft, belt/chain C-C calculator), favorites/recents,
more vendors (REV, Thrifty Bot, Swyft, AndyMark, hardware).
