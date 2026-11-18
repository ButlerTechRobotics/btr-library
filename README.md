# BTR Library for SOLIDWORKS 2026

Part of the Butler Tech Robotics app set (BTR Blockout, BTR Calc, BTR Lighten). Repo name: `btr-library`.

A SOLIDWORKS add-in that works like FRCDesignLib / FRCDesignApp in Onshape. It puts a
searchable library of FRC COTS parts in the Task Pane and lets you insert them with one click.

## What it does

- **BTR Library tab in the Task Pane**, with a Vendor › Category › Part tree.
- **Instant search** across names, vendors, categories and tags. You can search for things like `kraken`, `24T`, `2x1` or `hex bearing`.
- **3D preview** of each part. Drag to rotate, right-drag to pan, scroll to zoom, double-click to reset. Parts without a preview yet show their thumbnail (or a `.png` you put next to the part) and a **Load 3D** button.
- **Configuration picker**, so one file can hold every size (for example a gear file with 12T–60T configurations).
- **Insert** into the active assembly. The part lands at the origin and stays selected so you can drag or mate it.
  - **Drag** a part from the tree into the graphics area to place it interactively. Dragging always uses the default configuration.
  - **Double-click** a part, or select it and press **Enter**, to insert it.
- **Insert into a part** gives you a derived body (Insert › Part), like deriving a part in Onshape.
- **STEP support**: vendor STEP files are converted to SOLIDWORKS files the first time you use them, then cached.
- **Open**, **Show file** and **Vendor page** buttons.

## Look and feel

The task pane uses the same theme as the other BTR apps:
- The charcoal brand bar with the BTR logo, plus the 325-blue and 144-orange stripes.
- Barlow / Barlow Condensed type, which is embedded so it looks the same on every PC.
- Orange primary buttons, rounded cards, and the orange-square section labels.

Like the web apps, it follows the Windows light/dark setting automatically. To force Light or Dark, click **⋯ › Theme**.

## Install (students)

1. Go to [Releases](https://github.com/ButlerTechRobotics/btr-library/releases/latest) and download
   **BTR-Library-Setup-&lt;version&gt;.exe**.
2. Close SOLIDWORKS, run the installer, and approve the admin prompt.
3. Start SOLIDWORKS and open the **BTR** tab in the Task Pane on the right.
   If it isn't there, turn on **BTR Library** under **Tools › Add-Ins**.

When a new version comes out, the pane shows an orange **Update available** link: download it and run it the same way.
To remove the add-in, uninstall **BTR Library for SOLIDWORKS** from Windows Settings › Apps.

## Building and releasing (maintainers)

You need a PC with SOLIDWORKS 2026 (the build uses its API files), the .NET SDK and [Inno Setup 6](https://jrsoftware.org/isdl.php).
Visual Studio is optional.

- **Release:** bump `<Version>` in `src/BtrLibrary.csproj`, commit and push, then run
  `.\scripts\release.ps1 -Publish`. It builds the add-in and the installer, and attaches the installer to a new GitHub release.
  Without `-Publish` it only builds `dist\BTR-Library-Setup-<version>.exe`.
- **Parts:** don't need a release. Edit `manifest/library.json` and push (see Sync below).
- **Developing:** build with `dotnet build src\BtrLibrary.csproj -c Release` (close SOLIDWORKS first, it locks the DLL)
  and register the build folder once with `scripts\install.bat` (run as administrator). If SOLIDWORKS isn't in
  `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`, change `SwApiDir` in the `.csproj`.
  Installing a release afterwards re-points SOLIDWORKS at the installed copy, and the other way around.

## Parts come pre-loaded (Sync)

Teammates don't download CAD by hand. The team **parts list** is
[`manifest/library.json`](manifest/library.json) in this repo. When SOLIDWORKS starts, or when you click **Sync**, the add-in reads it from GitHub and
downloads every listed part it doesn't already have, straight from the vendor, into
`Documents\BTR Library` (or whatever library folder you've picked). Files that already exist are skipped, so syncing again is fast.

- **Currently included:** 355 parts (about 800 MB of STEP files). Motors and electronics: Kraken X60/X44, NEO Vortex with SPARK Flex, REV PDH, CTRE PDP 2.0, Limelight 4/3G/3. WCP power transmission: 236 gears (aluminum and steel; 1/2" hex, 3/8" hex, SplineXL, SplineXS and motor bores), 38 HTD5/GT2 pulleys, 50 #25/#35 sprockets, 7 flanged bearings and 14 chain parts.
- **Adding parts = a commit.** Add entries to `manifest/library.json` and push to `main`. Everyone picks them up on their
  next Sync without reinstalling (GitHub's raw file cache can take a few minutes). Shared 3D previews work the same way via `manifest/previews/`.
- **Offline**, Sync falls back to the copy of `library.json` that was built into the add-in.
- Use **⋯** to turn auto-sync off, or **⋯ › Set parts list URL…** to point at a different list (empty = this repo's list).
- If the library is on a shared team drive, only one person has to sync.

Each entry in `library.json` looks like this:

```json
{ "vendor": "WCP", "category": "Gears/20DP Steel Hex Bore",
  "name": "24T Steel Gear 20DP (WCP-0736)", "sku": "WCP-0736",
  "url": "https://wcproducts.info/files/frc/cad/WCP-0736.step",
  "productUrl": "https://wcproducts.com/products/steel-hex-bore-gears",
  "description": "24t steel spur gear, 20 DP, 1/2\" hex bore, 4140 steel",
  "tags": ["gear", "20dp", "24t", "hex"] }
```

`url` can point at a `.step`, `.sldprt` or `.zip` file. Zip files are unpacked into a folder.

### Sharing 3D previews

Building a 3D preview means SOLIDWORKS has to open (and for STEP, convert) the part, which is slow. Do it once for the whole team:

1. On one PC, Sync, then click **⋯ › Build 3D previews**.
2. Click **⋯ › Export previews for sharing…** and pick the `previews` folder next to `library.json`
   (`manifest\previews` in this repo). Commit it.
3. Everyone else gets the previews on their next Sync, so every part opens in 3D right away.

Previews are named after the part file's contents, so they match on any PC, and a preview goes stale on its own when a vendor updates the CAD.
Sync looks for them at `previews/` next to the parts list URL. For the built-in list, it uses the `previews` folder shipped next to the DLL.
To keep them somewhere else, add `"previews": "<url>"` to `library.json`.

## Adding your own parts by hand

```powershell
powershell -ExecutionPolicy Bypass -File scripts\New-FrcLibrary.ps1 -Root "D:\BTR Library"
```

This step is optional. It builds the folder tree (WCP, REV, CTRE, AndyMark, Thrifty Bot, Swyft, Hardware, Team Parts…).
Download CAD from the vendors' sites and drop it into the matching folders. Then click the
**Library:** path at the bottom of the task pane and pick the folder.

```
BTR Library\
  WCP\Gears\20DP Spur Gear.SLDPRT        <- configurations: 12T, 14T, ... 60T
  WCP\Gears\20DP Spur Gear.meta          <- optional: description, tags, url
  CTRE\Motors\Kraken X60.STEP            <- converted on first insert
  REV\Structure\MAXTube 1x2.SLDPRT
```

**For teams:** put the library on a shared drive that has the **same drive path on every
computer**. SOLIDWORKS assemblies store absolute paths to their components. Library files open
read-only when inserted, so nobody edits the shared copy by accident.

## Project layout

| File | What it does |
|---|---|
| `SwAddin.cs` | SOLIDWORKS entry point, COM registration and task pane setup |
| `LibraryPane.cs` | Task pane UI: search, tree, preview, buttons, drag-and-drop |
| `LibraryIndex.cs` | Scans the library folder and reads `.meta` files |
| `Inserter.cs` | SOLIDWORKS API calls: insert into an assembly or part, convert STEP, configurations |
| `Support.cs` | Settings (registry), thumbnails, natural sort, generated tab icons |

## Ideas for v2

- FRC generators like the popular FeatureScripts: box tube with a 0.5" hole pattern, hex shaft, belt/chain center-to-center calculator.
- A favorites and recently-used list.
- A shared vendor-library download so teams don't each have to build their own.
