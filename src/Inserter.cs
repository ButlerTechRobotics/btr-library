using System;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BtrLibrary
{
    /// <summary>All the SOLIDWORKS API work: converting STEP, inserting into assemblies and parts.</summary>
    public static class Inserter
    {
        /// <summary>
        /// Inserts the item into the active document.
        ///  - Assembly: adds it as a component at the origin and selects it so you can move/mate it.
        ///  - Part: inserts it as a derived body (Insert > Part), like "derive" in Onshape.
        /// </summary>
        public static string Insert(ISldWorks app, LibraryItem item, string config)
        {
            var target = app.ActiveDoc as IModelDoc2
                ?? throw new InvalidOperationException("Open an assembly (or a part) first, then insert.");

            int targetType = target.GetType();
            if (targetType == (int)swDocumentTypes_e.swDocDRAWING)
                throw new InvalidOperationException("Parts can't be inserted into a drawing. Switch to an assembly or part.");

            string path = EnsureNative(app, item);
            int docType = DocTypeFromPath(path);
            config = config ?? "";

            if (targetType == (int)swDocumentTypes_e.swDocASSEMBLY)
                return InsertIntoAssembly(app, target, path, docType, config, item.Name);

            if (docType != (int)swDocumentTypes_e.swDocPART)
                throw new InvalidOperationException("Assemblies can only be inserted into an assembly.");

            return InsertIntoPart(target, path, config, item.Name);
        }

        private static string InsertIntoAssembly(ISldWorks app, IModelDoc2 target, string path,
                                                 int docType, string config, string name)
        {
            string targetTitle = target.GetTitle();
            var assy = (IAssemblyDoc)target;

            // AddComponent5 needs the file loaded in memory first. Load it hidden so the
            // active window never changes (switching/closing windows mid-insert crashed SOLIDWORKS).
            bool wasOpen = app.GetOpenDocumentByName(path) != null;
            int err = 0, warn = 0;
            IModelDoc2 doc;
            using (Hidden(app))
            {
                doc = app.OpenDoc6(path, docType,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                    config, ref err, ref warn) as IModelDoc2;
            }

            if (doc == null)
                throw new InvalidOperationException($"SOLIDWORKS couldn't open {Path.GetFileName(path)} (error {err}).");

            EnsureActive(app, targetTitle);

            var comp = assy.AddComponent5(path,
                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "", false, config, 0, 0, 0);

            if (!wasOpen) app.CloseDoc(doc.GetTitle());

            if (comp == null)
                throw new InvalidOperationException($"SOLIDWORKS couldn't add {name} to the assembly.");

            target.ClearSelection2(true);
            comp.Select4(false, null, false);
            target.GraphicsRedraw2();

            return string.IsNullOrEmpty(config)
                ? $"Inserted {name} at the origin. Drag or mate it into place."
                : $"Inserted {name} [{config}] at the origin. Drag or mate it into place.";
        }

        private static string InsertIntoPart(IModelDoc2 target, string path, string config, string name)
        {
            var part = (IPartDoc)target;
            int options = (int)(swInsertPartOptions_e.swInsertPartImportSolids
                              | swInsertPartOptions_e.swInsertPartImportPlanes
                              | swInsertPartOptions_e.swInsertPartImportAxes);

            var feat = part.InsertPart3(path, options, config);
            if (feat == null)
                throw new InvalidOperationException($"SOLIDWORKS couldn't insert {name} into this part.");

            target.GraphicsRedraw2();
            return $"Inserted {name} into the part as a derived body.";
        }

        /// <summary>
        /// Returns a native SOLIDWORKS path for the item. STEP files are imported once and cached in a
        /// "_converted" folder next to them (or in %LOCALAPPDATA% if the library is read-only).
        /// </summary>
        public static string EnsureNative(ISldWorks app, LibraryItem item)
        {
            if (!item.IsNeutral) return item.Path;

            var cached = FindConverted(item);
            if (cached != null && File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(item.Path))
                return cached;

            string outDir = ConvertedDir(item);
            string activeTitle = (app.ActiveDoc as IModelDoc2)?.GetTitle();

            int err = 0;
            IModelDoc2 doc;
            using (Hidden(app))
            {
                object importData = app.GetImportFileData(item.Path);
                doc = app.LoadFile4(item.Path, "r", importData, ref err) as IModelDoc2;
            }
            if (doc == null)
                throw new InvalidOperationException($"Couldn't import {Path.GetFileName(item.Path)} (error {err}).");

            string outPath;
            try
            {
                string ext = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? ".SLDASM" : ".SLDPRT";
                outPath = Path.Combine(outDir, item.BaseName + ext);

                int e = 0, w = 0;
                bool ok = doc.Extension.SaveAs(outPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                    null, ref e, ref w);

                if (!ok)
                    throw new InvalidOperationException($"Couldn't save the converted file to {outDir} (error {e}).");
            }
            finally
            {
                // Make sure the user's document is active *before* closing the converted one,
                // so SOLIDWORKS is never left without an active document.
                if (activeTitle != null) EnsureActive(app, activeTitle);
                app.CloseDoc(doc.GetTitle());
            }
            return outPath;
        }

        /// <summary>True if a STEP item already has a converted native copy (no import needed).</summary>
        public static bool IsConverted(LibraryItem item) => !item.IsNeutral || FindConverted(item) != null;

        private static string FindConverted(LibraryItem item)
        {
            foreach (var dir in new[] { SharedConvertedDir(item), LocalConvertedDir(item) })
                foreach (var ext in new[] { ".SLDPRT", ".SLDASM" })
                {
                    var p = Path.Combine(dir, item.BaseName + ext);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        private static string SharedConvertedDir(LibraryItem item) => Path.Combine(item.Folder, "_converted");

        private static string LocalConvertedDir(LibraryItem item)
        {
            var root = Settings.LibraryRoot.TrimEnd('\\');
            var rel = item.Folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? item.Folder.Substring(root.Length).TrimStart('\\')
                : item.Vendor;
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                                "BtrLibrary", "Converted", rel);
        }

        private static string ConvertedDir(LibraryItem item)
        {
            var shared = SharedConvertedDir(item);
            try
            {
                Directory.CreateDirectory(shared);
                var probe = Path.Combine(shared, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return shared;
            }
            catch (Exception)
            {
                var local = LocalConvertedDir(item);
                Directory.CreateDirectory(local);
                return local;
            }
        }

        public static void Open(ISldWorks app, LibraryItem item, string config)
        {
            string path = EnsureNative(app, item);
            int err = 0, warn = 0;
            var doc = app.OpenDoc6(path, DocTypeFromPath(path),
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, config ?? "", ref err, ref warn);
            if (doc == null)
                throw new InvalidOperationException($"Couldn't open {Path.GetFileName(path)} (error {err}).");
        }

        public static string[] GetConfigurations(ISldWorks app, LibraryItem item)
        {
            if (item.IsNeutral) return new string[0];
            object names = app.GetConfigurationNames(item.Path);
            if (names is string[] s) return s;
            if (names is object[] o) return Array.ConvertAll(o, x => x?.ToString() ?? "");
            return new string[0];
        }

        /// <summary>
        /// Triangle mesh for the task pane's 3D preview. Cached on disk per file + configuration, so
        /// SOLIDWORKS only has to open (and, for STEP, convert) each part once. Same hidden-doc rules as Insert.
        /// </summary>
        public static Mesh GetPreviewMesh(ISldWorks app, LibraryItem item, string config)
        {
            config = config ?? "";
            var cached = MeshCache.TryLoad(item, config);
            if (cached != null) return cached;

            string path = EnsureNative(app, item);
            string activeTitle = (app.ActiveDoc as IModelDoc2)?.GetTitle();

            var doc = app.GetOpenDocumentByName(path) as IModelDoc2;
            bool wasOpen = doc != null;
            if (!wasOpen)
            {
                int err = 0, warn = 0;
                using (Hidden(app))
                {
                    doc = app.OpenDoc6(path, DocTypeFromPath(path),
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                        config, ref err, ref warn) as IModelDoc2;
                }
                if (doc == null)
                    throw new InvalidOperationException($"Couldn't open {Path.GetFileName(path)} for preview (error {err}).");
            }

            Mesh mesh;
            try
            {
                mesh = Tessellate(doc);
            }
            finally
            {
                // User's document active again *before* closing ours.
                if (activeTitle != null) EnsureActive(app, activeTitle);
                if (!wasOpen) app.CloseDoc(doc.GetTitle());
            }

            if (mesh.Count > 0) MeshCache.Save(item, config, mesh);
            return mesh;
        }

        private static Mesh Tessellate(IModelDoc2 doc)
        {
            var b = new MeshBuilder(MeshBuilder.ColorOf(doc.MaterialPropertyValues, Mesh.DefaultColor));

            if (doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var assy = (IAssemblyDoc)doc;
                try { assy.ResolveAllLightWeightComponents(false); } catch { /* best effort */ }
                if (assy.GetComponents(false) is object[] comps)
                {
                    foreach (var o in comps)
                    {
                        if (!(o is IComponent2 c) || c.IsSuppressed() || c.IsHidden(true)) continue;
                        if (!(c.GetBodies3((int)swBodyType_e.swSolidBody, out object _) is object[] bodies)) continue;
                        var xf = (c.Transform2 as MathTransform)?.ArrayData as double[];
                        int color = MeshBuilder.ColorOf(c.MaterialPropertyValues, b.DefaultColor);
                        foreach (var body in bodies) AddBody(b, body as IBody2, xf, color);
                    }
                }
            }
            else if (((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) is object[] bodies)
            {
                foreach (var body in bodies) AddBody(b, body as IBody2, null, b.DefaultColor);
            }

            return b.Build();
        }

        /// <summary>
        /// Tessellates a body with the geometry kernel (IBody2.GetTessellation).
        /// NOT IFace2.GetTessTriangles: that reads the *display* tessellation, which a hidden document never builds —
        /// calling it there crashed SOLIDWORKS (access violation in slduiu, GetTessTriangleCount).
        /// </summary>
        private static void AddBody(MeshBuilder b, IBody2 body, double[] xf, int color)
        {
            if (body == null) return;
            try { color = MeshBuilder.ColorOf(body.MaterialPropertyValues2, color); } catch { }

            if (!(body.GetTessellation(null) is ITessellation tess)) return;
            tess.NeedFaceFacetMap = true;
            tess.NeedVertexNormal = true;
            tess.ImprovedQuality = true;
            tess.CurveChordAngleTolerance = 0.2;   // ~11°: round bores/teeth look smooth
            tess.SurfacePlaneAngleTolerance = 0.2;
            if (!tess.Tessellate()) return;

            int facets = tess.GetFacetCount(), verts = tess.GetVertexCount();
            if (facets <= 0 || verts <= 0) return;

            // Per-facet colour from face appearances (if any).
            var colors = new int[facets];
            for (int i = 0; i < facets; i++) colors[i] = color;
            if (body.GetFaces() is object[] faces)
            {
                foreach (var o in faces)
                {
                    if (!(o is IFace2 face)) continue;
                    int c;
                    try { c = MeshBuilder.ColorOf(face.MaterialPropertyValues, color); } catch { continue; }
                    if (c == color) continue;
                    if (tess.GetFaceFacets(face) is int[] ids)
                        foreach (var id in ids) if (id >= 0 && id < facets) colors[id] = c;
                }
            }

            var pts = new double[verts][];
            var nrms = new double[verts][];
            for (int v = 0; v < verts; v++)
            {
                pts[v] = tess.GetVertexPoint(v) as double[];
                nrms[v] = tess.GetVertexNormal(v) as double[];
            }

            var tri = new float[facets * 9];
            var nrm = new float[facets * 9];
            int kept = 0;
            for (int f = 0; f < facets; f++)
            {
                if (!(tess.GetFacetFins(f) is int[] fins) || fins.Length != 3) continue;
                bool ok = true;
                for (int k = 0; k < 3 && ok; k++)
                {
                    if (!(tess.GetFinVertices(fins[k]) is int[] fv) || fv.Length < 1 || fv[0] < 0 || fv[0] >= verts) { ok = false; break; }
                    var p = pts[fv[0]]; var n = nrms[fv[0]];
                    if (p == null || p.Length < 3) { ok = false; break; }
                    int i = kept * 9 + k * 3;
                    tri[i] = (float)p[0]; tri[i + 1] = (float)p[1]; tri[i + 2] = (float)p[2];
                    if (n != null && n.Length >= 3) { nrm[i] = (float)n[0]; nrm[i + 1] = (float)n[1]; nrm[i + 2] = (float)n[2]; }
                }
                if (!ok) continue;
                colors[kept] = colors[f];
                kept++;
            }
            if (kept == 0) return;

            Array.Resize(ref tri, kept * 9);
            Array.Resize(ref nrm, kept * 9);
            Array.Resize(ref colors, kept);
            b.Add(tri, nrm, xf, colors);
        }

        /// <summary>Re-activates the user's document only if something else became active.</summary>
        private static void EnsureActive(ISldWorks app, string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            var current = (app.ActiveDoc as IModelDoc2)?.GetTitle();
            if (string.Equals(current, title, StringComparison.OrdinalIgnoreCase)) return;
            int err = 0;
            app.ActivateDoc3(title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
        }

        /// <summary>While in scope, newly opened parts/assemblies load without a window.</summary>
        private static IDisposable Hidden(ISldWorks app) => new HiddenDocs(app);

        private sealed class HiddenDocs : IDisposable
        {
            private readonly ISldWorks _app;
            public HiddenDocs(ISldWorks app)
            {
                _app = app;
                _app.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                _app.DocumentVisible(false, (int)swDocumentTypes_e.swDocASSEMBLY);
            }
            public void Dispose()
            {
                _app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART);
                _app.DocumentVisible(true, (int)swDocumentTypes_e.swDocASSEMBLY);
            }
        }

        private static int DocTypeFromPath(string path) =>
            Path.GetExtension(path).Equals(".sldasm", StringComparison.OrdinalIgnoreCase)
                ? (int)swDocumentTypes_e.swDocASSEMBLY
                : (int)swDocumentTypes_e.swDocPART;
    }
}
