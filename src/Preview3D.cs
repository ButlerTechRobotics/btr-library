using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace BtrLibrary
{
    /// <summary>Triangle soup for the 3D preview: 9 floats position + 9 floats normal + 1 ARGB colour per triangle.</summary>
    public sealed class Mesh
    {
        public const int DefaultColor = unchecked((int)0xFFB4B9C2); // brushed steel

        public float[] Pos;
        public float[] Nrm;
        public int[] Color;
        public float Cx, Cy, Cz, Radius = 1;

        public int Count => Color?.Length ?? 0;

        public void ComputeBounds()
        {
            if (Count == 0) return;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < Pos.Length; i += 3)
            {
                float x = Pos[i], y = Pos[i + 1], z = Pos[i + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            Cx = (minX + maxX) / 2; Cy = (minY + maxY) / 2; Cz = (minZ + maxZ) / 2;
            float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            Radius = Math.Max(1e-6f, (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2);
        }
    }

    /// <summary>Collects tessellated faces (optionally transformed by a SOLIDWORKS MathTransform array).</summary>
    public sealed class MeshBuilder
    {
        private readonly List<float> _pos = new List<float>();
        private readonly List<float> _nrm = new List<float>();
        private readonly List<int> _col = new List<int>();

        public MeshBuilder(int defaultColor) { DefaultColor = defaultColor; }
        public int DefaultColor { get; }

        /// <param name="tri">9 floats per triangle (x,y,z of 3 vertices).</param>
        /// <param name="nrm">9 floats per triangle (vertex normals), or null for flat shading.</param>
        /// <param name="xf">SOLIDWORKS transform ArrayData (rotation 0-8, translation 9-11, scale 12), or null.</param>
        public void Add(float[] tri, float[] nrm, double[] xf, int color) => Add(tri, nrm, xf, null, color);

        /// <param name="colors">One ARGB colour per triangle.</param>
        public void Add(float[] tri, float[] nrm, double[] xf, int[] colors) => Add(tri, nrm, xf, colors, DefaultColor);

        private void Add(float[] tri, float[] nrm, double[] xf, int[] colors, int color)
        {
            int n = tri.Length / 9;
            bool hasN = nrm != null && nrm.Length >= n * 9;
            bool hasX = xf != null && xf.Length >= 12;
            double s = hasX && xf.Length >= 13 && xf[12] != 0 ? xf[12] : 1;

            for (int t = 0; t < n; t++)
            {
                int b = t * 9;
                float fnx, fny, fnz;
                {
                    float ax = tri[b + 3] - tri[b], ay = tri[b + 4] - tri[b + 1], az = tri[b + 5] - tri[b + 2];
                    float bx = tri[b + 6] - tri[b], by = tri[b + 7] - tri[b + 1], bz = tri[b + 8] - tri[b + 2];
                    fnx = ay * bz - az * by; fny = az * bx - ax * bz; fnz = ax * by - ay * bx;
                }
                for (int v = 0; v < 3; v++)
                {
                    int i = b + v * 3;
                    double x = tri[i], y = tri[i + 1], z = tri[i + 2];
                    double nx = hasN ? nrm[i] : fnx, ny = hasN ? nrm[i + 1] : fny, nz = hasN ? nrm[i + 2] : fnz;
                    if (nx * nx + ny * ny + nz * nz < 1e-18) { nx = fnx; ny = fny; nz = fnz; } // missing vertex normal
                    if (hasX)
                    {
                        // SOLIDWORKS uses row vectors: p' = s * p·R + T
                        double px = s * (x * xf[0] + y * xf[3] + z * xf[6]) + xf[9];
                        double py = s * (x * xf[1] + y * xf[4] + z * xf[7]) + xf[10];
                        double pz = s * (x * xf[2] + y * xf[5] + z * xf[8]) + xf[11];
                        double qx = nx * xf[0] + ny * xf[3] + nz * xf[6];
                        double qy = nx * xf[1] + ny * xf[4] + nz * xf[7];
                        double qz = nx * xf[2] + ny * xf[5] + nz * xf[8];
                        x = px; y = py; z = pz; nx = qx; ny = qy; nz = qz;
                    }
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 0) { nx /= len; ny /= len; nz /= len; }
                    _pos.Add((float)x); _pos.Add((float)y); _pos.Add((float)z);
                    _nrm.Add((float)nx); _nrm.Add((float)ny); _nrm.Add((float)nz);
                }
                _col.Add(colors != null && t < colors.Length ? colors[t] : color);
            }
        }

        public Mesh Build()
        {
            var m = new Mesh { Pos = _pos.ToArray(), Nrm = _nrm.ToArray(), Color = _col.ToArray() };
            m.ComputeBounds();
            return m;
        }

        /// <summary>SOLIDWORKS MaterialPropertyValues (r, g, b, ...) in 0..1 → ARGB.</summary>
        public static int ColorOf(object values, int fallback)
        {
            if (!(values is double[] d) || d.Length < 3 || d[0] < 0 || d[1] < 0 || d[2] < 0) return fallback;
            int C(double v) => Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
            return unchecked((int)0xFF000000) | (C(d[0]) << 16) | (C(d[1]) << 8) | C(d[2]);
        }
    }

    /// <summary>
    /// Tessellated previews cached in %LOCALAPPDATA%\BtrLibrary\Meshes. Files are named after the SHA-1 of the
    /// part file's *contents* (plus configuration), so the same vendor STEP gets the same name on every PC — that's
    /// what lets a team share pre-built previews (Export previews → previews\ next to library.json → Sync).
    /// Compact format (gzip): positions as 16-bit fractions of the bounding box, normals as signed bytes,
    /// colours as a small palette — roughly 5x smaller than raw floats.
    /// </summary>
    public static class MeshCache
    {
        private const int Magic = 0x4D525442; // "BTRM"
        private const int Version = 2;

        private static bool _cleaned;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Hashes =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Dir
        {
            get
            {
                var root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "BtrLibrary");
                if (!_cleaned)
                {
                    _cleaned = true;
                    // Previews keyed by path+date (before content keys); they can't be shared, so drop them.
                    try { var old = Path.Combine(root, "Previews"); if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
                }
                return Path.Combine(root, "Meshes");
            }
        }

        /// <summary>Portable preview file name, e.g. "3f2a…c1.mesh" or "3f2a…c1-9b0e4d12.mesh" for a named configuration.</summary>
        public static string FileNameFor(LibraryItem item, string config) => FileNameFor(item.Path, config);

        public static string FileNameFor(string partPath, string config)
        {
            var name = ContentHash(partPath);
            if (!string.IsNullOrEmpty(config)) name += "-" + Hex(Encoding.UTF8.GetBytes(config)).Substring(0, 8);
            return name + ".mesh";
        }

        public static string PathFor(LibraryItem item, string config) => Path.Combine(Dir, FileNameFor(item, config));

        public static bool Has(LibraryItem item, string config)
        {
            try { return File.Exists(PathFor(item, config)); } catch { return false; }
        }

        /// <summary>SHA-1 of the file bytes, memoised per path + size + date so it's only computed once.</summary>
        private static string ContentHash(string path)
        {
            var fi = new FileInfo(path);
            string memo = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            return Hashes.GetOrAdd(memo, _ =>
            {
                using (var fs = File.OpenRead(path)) return Hex(fs);
            });
        }

        private static string Hex(byte[] data) { using (var ms = new MemoryStream(data)) return Hex(ms); }

        private static string Hex(Stream s)
        {
            using (var sha = SHA1.Create())
            {
                var sb = new StringBuilder(40);
                foreach (var b in sha.ComputeHash(s)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>True if the bytes look like a preview file this version can read (guards against HTML error pages).</summary>
        public static bool IsValid(byte[] data) =>
            data != null && data.Length > 8 && BitConverter.ToInt32(data, 0) == Magic && BitConverter.ToInt32(data, 4) == Version;

        /// <summary>Stores a downloaded/shared preview file in the cache.</summary>
        public static bool Import(string fileName, byte[] data)
        {
            if (!IsValid(data)) return false;
            try
            {
                Directory.CreateDirectory(Dir);
                var file = Path.Combine(Dir, Path.GetFileName(fileName));
                File.WriteAllBytes(file + ".tmp", data);
                if (File.Exists(file)) File.Delete(file);
                File.Move(file + ".tmp", file);
                return true;
            }
            catch { return false; }
        }

        public static Mesh TryLoad(LibraryItem item, string config)
        {
            string file = null;
            try
            {
                file = PathFor(item, config);
                if (!File.Exists(file)) return null;
                using (var fs = File.OpenRead(file))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadInt32() != Magic) return null;
                    if (r.ReadInt32() != Version) { r.Close(); File.Delete(file); return null; } // old format: rebuild
                    using (var z = new BinaryReader(new GZipStream(fs, CompressionMode.Decompress)))
                    {
                        int n = z.ReadInt32();
                        float minX = z.ReadSingle(), minY = z.ReadSingle(), minZ = z.ReadSingle();
                        float sx = z.ReadSingle(), sy = z.ReadSingle(), sz = z.ReadSingle();
                        var palette = new int[z.ReadInt32()];
                        for (int i = 0; i < palette.Length; i++) palette[i] = z.ReadInt32();

                        var m = new Mesh { Pos = new float[n * 9], Nrm = new float[n * 9], Color = new int[n] };
                        var pb = ReadExactly(z, n * 9 * 2);
                        for (int i = 0; i < n * 9; i++)
                        {
                            int q = pb[i * 2] | (pb[i * 2 + 1] << 8);
                            switch (i % 3)
                            {
                                case 0: m.Pos[i] = minX + q * sx; break;
                                case 1: m.Pos[i] = minY + q * sy; break;
                                default: m.Pos[i] = minZ + q * sz; break;
                            }
                        }
                        var nb = ReadExactly(z, n * 9);
                        for (int i = 0; i < n * 9; i++) m.Nrm[i] = (sbyte)nb[i] / 127f;
                        var cb = ReadExactly(z, n);
                        for (int i = 0; i < n; i++) m.Color[i] = palette[cb[i]];

                        m.ComputeBounds();
                        return m.Count > 0 ? m : null;
                    }
                }
            }
            catch { return null; }
        }

        public static void Save(LibraryItem item, string config, Mesh m)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var file = PathFor(item, config);
                var tmp = file + ".tmp";
                int n = m.Count;

                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i < m.Pos.Length; i += 3)
                {
                    minX = Math.Min(minX, m.Pos[i]); maxX = Math.Max(maxX, m.Pos[i]);
                    minY = Math.Min(minY, m.Pos[i + 1]); maxY = Math.Max(maxY, m.Pos[i + 1]);
                    minZ = Math.Min(minZ, m.Pos[i + 2]); maxZ = Math.Max(maxZ, m.Pos[i + 2]);
                }
                float sx = Math.Max(1e-12f, (maxX - minX) / 65535f);
                float sy = Math.Max(1e-12f, (maxY - minY) / 65535f);
                float sz = Math.Max(1e-12f, (maxZ - minZ) / 65535f);

                var palette = new List<int>();
                var index = new Dictionary<int, byte>();
                var cb = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    if (!index.TryGetValue(m.Color[i], out byte k))
                    {
                        if (palette.Count == 256) k = 0; // absurdly colourful part: reuse the first colour
                        else { k = (byte)palette.Count; palette.Add(m.Color[i]); index[m.Color[i]] = k; }
                    }
                    cb[i] = k;
                }

                using (var fs = File.Create(tmp))
                {
                    using (var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true)) { w.Write(Magic); w.Write(Version); }
                    using (var z = new BinaryWriter(new GZipStream(fs, CompressionLevel.Optimal)))
                    {
                        z.Write(n);
                        z.Write(minX); z.Write(minY); z.Write(minZ);
                        z.Write(sx); z.Write(sy); z.Write(sz);
                        z.Write(palette.Count);
                        foreach (var c in palette) z.Write(c);

                        var pb = new byte[n * 9 * 2];
                        for (int i = 0; i < n * 9; i++)
                        {
                            float v = m.Pos[i], lo = i % 3 == 0 ? minX : i % 3 == 1 ? minY : minZ, st = i % 3 == 0 ? sx : i % 3 == 1 ? sy : sz;
                            int q = Math.Max(0, Math.Min(65535, (int)Math.Round((v - lo) / st)));
                            pb[i * 2] = (byte)q; pb[i * 2 + 1] = (byte)(q >> 8);
                        }
                        z.Write(pb);

                        var nb = new byte[n * 9];
                        for (int i = 0; i < n * 9; i++)
                            nb[i] = (byte)(sbyte)Math.Max(-127, Math.Min(127, (int)Math.Round(m.Nrm[i] * 127)));
                        z.Write(nb);
                        z.Write(cb);
                    }
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch { /* cache is optional */ }
        }

        private static byte[] ReadExactly(BinaryReader r, int count)
        {
            var b = r.ReadBytes(count);
            if (b.Length != count) throw new EndOfStreamException();
            return b;
        }
    }

    /// <summary>
    /// Interactive 3D preview with a small software z-buffer renderer (no GPU / ActiveX dependencies).
    /// Drag to rotate, right/middle-drag to pan, wheel to zoom, double-click to reset.
    /// Falls back to a 2D thumbnail while the mesh is loading or when none is available.
    /// </summary>
    public sealed class ModelViewer : Control
    {
        private const float DefaultYaw = -0.785f, DefaultPitch = 0.55f;

        private Mesh _mesh;
        private Image _fallback;
        private string _message, _action;
        private Rectangle _actionRect;
        private bool _actionHot;
        private float _yaw = DefaultYaw, _pitch = DefaultPitch, _zoom = 1, _panX, _panY;
        private bool _dirty = true, _hover;
        private MouseButtons _dragButton = MouseButtons.None;
        private Point _last;

        private Bitmap _buf;
        private int[] _pix;
        private float[] _z;
        private int _bufScale;

        public Color Plate { get; set; } = Color.White;
        public Color Border { get; set; } = Color.Gainsboro;
        public Color Ink2 { get; set; } = Color.Gray;
        public Font MessageFont { get; set; }
        public Color Accent { get; set; } = Color.DarkOrange;
        public float UiScale { get; set; } = 1;

        /// <summary>Raised when the user clicks the <see cref="ActionText"/> button.</summary>
        public event EventHandler ActionClicked;

        public ModelViewer()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.SizeAll;
        }

        public Mesh Mesh
        {
            get => _mesh;
            set { _mesh = value; _zoom = 1; _panX = _panY = 0; Redraw(); }
        }

        /// <summary>2D image shown when there is no mesh (not owned by the viewer).</summary>
        public Image Fallback
        {
            get => _fallback;
            set { _fallback = value; Invalidate(); }
        }

        /// <summary>Small caption such as "LOADING 3D…" or "NO PREVIEW"; null for none.</summary>
        public string Message
        {
            get => _message;
            set { _message = value; Invalidate(); }
        }

        /// <summary>Text of a button shown over the thumbnail when there is no mesh (e.g. "LOAD 3D"); null hides it.</summary>
        public string ActionText
        {
            get => _action;
            set { _action = value; _actionHot = false; Invalidate(); }
        }

        private bool ShowsAction => _mesh == null && !string.IsNullOrEmpty(_action);

        public void Redraw() { _dirty = true; Invalidate(); }

        public void ResetView()
        {
            _yaw = DefaultYaw; _pitch = DefaultPitch; _zoom = 1; _panX = _panY = 0;
            Redraw();
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (ShowsAction && e.Button == MouseButtons.Left && _actionRect.Contains(e.Location))
            {
                ActionClicked?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (_mesh == null) return;
            _dragButton = e.Button;
            _last = e.Location;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (ShowsAction)
            {
                bool hot = _actionRect.Contains(e.Location);
                if (hot != _actionHot) { _actionHot = hot; Invalidate(); }
                Cursor = hot ? Cursors.Hand : Cursors.Default;
            }
            else if (Cursor != Cursors.SizeAll) Cursor = Cursors.SizeAll;
            if (_dragButton == MouseButtons.None || _mesh == null) return;
            int dx = e.X - _last.X, dy = e.Y - _last.Y;
            _last = e.Location;
            if (_dragButton == MouseButtons.Left)
            {
                _yaw += dx * 0.012f;
                _pitch = Math.Max(-1.55f, Math.Min(1.55f, _pitch + dy * 0.012f));
            }
            else
            {
                _panX += dx; _panY += dy;
            }
            Redraw();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragButton == MouseButtons.None) return;
            _dragButton = MouseButtons.None;
            Capture = false;
            Redraw(); // re-render at full quality
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_mesh == null) return;
            _zoom = Math.Max(0.2f, Math.Min(12f, _zoom * (float)Math.Pow(1.15, e.Delta / 120.0)));
            Redraw();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_mesh != null) ResetView();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _actionHot = false; Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); _dirty = true; }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Plate)) g.FillRectangle(b, ClientRectangle);

            if (_mesh != null && _mesh.Count > 0 && Width > 2 && Height > 2)
            {
                // Low resolution while dragging for responsiveness, 2x supersampled otherwise.
                int ss = _dragButton != MouseButtons.None ? 1 : 2;
                if (_dirty || _buf == null || _bufScale != ss ||
                    _buf.Width != ClientSize.Width * ss || _buf.Height != ClientSize.Height * ss)
                {
                    Render(ss);
                    _dirty = false;
                }
                g.InterpolationMode = ss > 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_buf, ClientRectangle, 0, 0, _buf.Width, _buf.Height, GraphicsUnit.Pixel);
            }
            else if (_fallback != null)
            {
                float k = Math.Min((float)Width / _fallback.Width, (float)Height / _fallback.Height);
                float w = _fallback.Width * k, h = _fallback.Height * k;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_fallback, (Width - w) / 2, (Height - h) / 2, w, h);
            }

            var font = MessageFont ?? Font;
            bool hasPicture = _mesh != null || _fallback != null;
            int pad = Math.Max(4, font.Height / 3);
            var bottom = new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            if (!string.IsNullOrEmpty(_message))
                TextRenderer.DrawText(g, _message, font, hasPicture ? bottom : ClientRectangle, Ink2,
                    flags | (hasPicture ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter));
            else if (_hover && _mesh != null && _dragButton == MouseButtons.None)
                TextRenderer.DrawText(g, "DRAG TO ROTATE · WHEEL TO ZOOM", font, bottom, Ink2, flags | TextFormatFlags.Bottom);

            if (ShowsAction) DrawAction(g, font);

            using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        private void DrawAction(Graphics g, Font font)
        {
            var size = TextRenderer.MeasureText(g, _action, font);
            int w = size.Width + (int)(22 * UiScale), h = size.Height + (int)(10 * UiScale);
            _actionRect = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(_actionRect, h / 2f))
            {
                using (var b = new SolidBrush(_actionHot ? Theme.Mix(Accent, Color.Black, 0.12f) : Accent)) g.FillPath(b, path);
            }
            g.SmoothingMode = SmoothingMode.Default;
            TextRenderer.DrawText(g, _action, font, _actionRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private void Render(int ss)
        {
            int W = Math.Max(1, ClientSize.Width * ss), H = Math.Max(1, ClientSize.Height * ss);
            if (_buf == null || _buf.Width != W || _buf.Height != H)
            {
                _buf?.Dispose();
                _buf = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                _pix = new int[W * H];
                _z = new float[W * H];
            }
            _bufScale = ss;

            // Background: the plate colour with a subtle vertical gradient.
            Color top = Plate, bot = Theme.Mix(Plate, Color.Black, Plate.GetBrightness() > 0.5f ? 0.07f : 0.25f);
            for (int y = 0; y < H; y++)
            {
                float t = (float)y / H;
                int c = unchecked((int)0xFF000000) |
                        ((int)(top.R + (bot.R - top.R) * t) << 16) |
                        ((int)(top.G + (bot.G - top.G) * t) << 8) |
                        (int)(top.B + (bot.B - top.B) * t);
                int row = y * W;
                for (int x = 0; x < W; x++) _pix[row + x] = c;
            }
            for (int i = 0; i < _z.Length; i++) _z[i] = float.MinValue;

            var m = _mesh;
            float cy = (float)Math.Cos(_yaw), sy = (float)Math.Sin(_yaw);
            float cp = (float)Math.Cos(_pitch), sp = (float)Math.Sin(_pitch);
            float r00 = cy, r01 = 0, r02 = sy;
            float r10 = sy * sp, r11 = cp, r12 = -cy * sp;
            float r20 = -sy * cp, r21 = sp, r22 = cy * cp;

            float k = Math.Min(W, H) * 0.46f / m.Radius * _zoom;
            float ox = W / 2f + _panX * ss, oy = H / 2f + _panY * ss;

            // Headlight from upper-left, Blinn-Phong highlight.
            float lx = -0.35f, ly = 0.55f, lz = 0.76f;
            float ll = (float)Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;
            float hx = lx, hy = ly, hz = lz + 1;
            float hl = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz); hx /= hl; hy /= hl; hz /= hl;

            var sx = new float[3]; var syv = new float[3]; var sz = new float[3];
            var cr = new float[3]; var cg = new float[3]; var cb = new float[3];

            for (int t = 0; t < m.Count; t++)
            {
                int col = m.Color[t];
                float br = (col >> 16) & 255, bg = (col >> 8) & 255, bb = col & 255;
                int b = t * 9;
                for (int v = 0; v < 3; v++)
                {
                    int i = b + v * 3;
                    float x = m.Pos[i] - m.Cx, y = m.Pos[i + 1] - m.Cy, z = m.Pos[i + 2] - m.Cz;
                    float X = r00 * x + r01 * y + r02 * z;
                    float Y = r10 * x + r11 * y + r12 * z;
                    float Z = r20 * x + r21 * y + r22 * z;
                    sx[v] = ox + X * k; syv[v] = oy - Y * k; sz[v] = Z;

                    float nx0 = m.Nrm[i], ny0 = m.Nrm[i + 1], nz0 = m.Nrm[i + 2];
                    float nx = r00 * nx0 + r01 * ny0 + r02 * nz0;
                    float ny = r10 * nx0 + r11 * ny0 + r12 * nz0;
                    float nz = r20 * nx0 + r21 * ny0 + r22 * nz0;
                    if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; } // two-sided

                    float diff = Math.Max(0, nx * lx + ny * ly + nz * lz);
                    float nh = Math.Max(0, nx * hx + ny * hy + nz * hz);
                    float spec = nh * nh; spec *= spec; spec *= spec; spec *= spec; spec *= spec; // ^32
                    float shade = 0.32f + 0.68f * diff;
                    float hi = 110f * spec;
                    cr[v] = br * shade + hi; cg[v] = bg * shade + hi; cb[v] = bb * shade + hi;
                }
                Raster(W, H, sx, syv, sz, cr, cg, cb);
            }

            var data = _buf.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (data.Stride == W * 4) Marshal.Copy(_pix, 0, data.Scan0, _pix.Length);
                else for (int y = 0; y < H; y++) Marshal.Copy(_pix, y * W, data.Scan0 + y * data.Stride, W);
            }
            finally { _buf.UnlockBits(data); }
        }

        private void Raster(int W, int H, float[] x, float[] y, float[] z, float[] r, float[] g, float[] b)
        {
            float area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0]);
            if (Math.Abs(area) < 1e-8f) return;
            float inv = 1f / area;

            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x[0], Math.Min(x[1], x[2]))));
            int maxX = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(x[0], Math.Max(x[1], x[2]))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(y[0], Math.Min(y[1], y[2]))));
            int maxY = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(y[0], Math.Max(y[1], y[2]))));
            if (minX > maxX || minY > maxY) return;

            for (int py = minY; py <= maxY; py++)
            {
                float fy = py + 0.5f;
                int row = py * W;
                for (int px = minX; px <= maxX; px++)
                {
                    float fx = px + 0.5f;
                    float w0 = ((x[1] - fx) * (y[2] - fy) - (x[2] - fx) * (y[1] - fy)) * inv;
                    if (w0 < 0) continue;
                    float w1 = ((x[2] - fx) * (y[0] - fy) - (x[0] - fx) * (y[2] - fy)) * inv;
                    if (w1 < 0) continue;
                    float w2 = 1 - w0 - w1;
                    if (w2 < 0) continue;

                    int idx = row + px;
                    float d = w0 * z[0] + w1 * z[1] + w2 * z[2];
                    if (d <= _z[idx]) continue;
                    _z[idx] = d;

                    int R = (int)(w0 * r[0] + w1 * r[1] + w2 * r[2]);
                    int G = (int)(w0 * g[0] + w1 * g[1] + w2 * g[2]);
                    int B = (int)(w0 * b[0] + w1 * b[1] + w2 * b[2]);
                    if (R > 255) R = 255; if (G > 255) G = 255; if (B > 255) B = 255;
                    _pix[idx] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _buf?.Dispose();
            base.Dispose(disposing);
        }
    }
}
