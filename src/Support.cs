using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BtrLibrary
{
    /// <summary>Per-user settings stored in HKCU\Software\BtrLibrary.</summary>
    public static class Settings
    {
        private const string KeyPath = @"Software\BtrLibrary";

        public static string LibraryRoot
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    var value = key?.GetValue("LibraryRoot") as string;
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BTR Library");
            }
            set => Set("LibraryRoot", value ?? "");
        }

        /// <summary>The team parts list in the btr-library repo. Commit to it and everyone gets the change on their next Sync.</summary>
        public const string DefaultManifestUrl =
            "https://raw.githubusercontent.com/ButlerTechRobotics/btr-library/main/manifest/library.json";

        /// <summary>Custom parts list URL. Empty = <see cref="DefaultManifestUrl"/> (falling back to the copy next to the DLL when offline).</summary>
        public static string ManifestUrl
        {
            get => Get("ManifestUrl") ?? "";
            set => Set("ManifestUrl", value ?? "");
        }

        /// <summary>"auto" (follow Windows light/dark), "light" or "dark" — same choices as the BTR web apps.</summary>
        public static string Theme
        {
            get => Get("Theme") ?? "auto";
            set => Set("Theme", value ?? "auto");
        }

        /// <summary>Download missing parts automatically when SOLIDWORKS starts.</summary>
        public static bool AutoSync
        {
            get => Get("AutoSync") != "0";
            set => Set("AutoSync", value ? "1" : "0");
        }

        private static string Get(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key?.GetValue(name) as string;
        }

        private static void Set(string name, string value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, value);
        }
    }

    /// <summary>Preview images: a sidecar .png/.jpg if present, otherwise the Windows shell thumbnail
    /// (SOLIDWORKS installs a thumbnail handler for SLDPRT/SLDASM).</summary>
    public static class Thumbnails
    {
        public static Image Get(LibraryItem item, int size)
        {
            try
            {
                var sidecar = item.PreviewPath;
                if (sidecar != null)
                {
                    // Copy into memory so the file isn't locked.
                    using (var fs = File.OpenRead(sidecar))
                    using (var img = Image.FromStream(fs))
                        return new Bitmap(img);
                }
                return FromShell(item.Path, size);
            }
            catch
            {
                return null;
            }
        }

        private static Image FromShell(string path, int size)
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
            if (factory == null) return null;
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0, out IntPtr hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
                try { return Image.FromHbitmap(hbm); }
                finally { DeleteObject(hbm); }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }

    /// <summary>Checks GitHub Releases for a newer BTR Library installer (best effort, once per session).</summary>
    public static class Updates
    {
        public const string LatestReleaseApi = "https://api.github.com/repos/ButlerTechRobotics/btr-library/releases/latest";

        [DataContract]
        private sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "html_url")] public string Url { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        }

        public static Version Current => typeof(Updates).Assembly.GetName().Version;

        /// <summary>Returns (version, release page URL) if GitHub has a newer release, otherwise null. Never throws.</summary>
        public static async Task<Tuple<Version, string>> CheckAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("BtrLibrary-SolidWorks/" + Current);
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    var bytes = await http.GetByteArrayAsync(LatestReleaseApi).ConfigureAwait(false);
                    Release r;
                    using (var ms = new MemoryStream(bytes))
                        r = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(ms);
                    if (r == null || r.Draft || r.Prerelease || string.IsNullOrEmpty(r.Tag)) return null;
                    if (!Version.TryParse(r.Tag.TrimStart('v', 'V'), out var latest)) return null;
                    return IsNewer(latest, Current) ? Tuple.Create(latest, r.Url) : null;
                }
            }
            catch
            {
                return null; // offline, rate-limited, no releases yet: just don't show anything
            }
        }

        /// <summary>Compares major.minor.build, treating missing parts as 0 (so "0.3.0" equals assembly 0.3.0.0).</summary>
        public static bool IsNewer(Version a, Version b)
        {
            int P(int x) => x < 0 ? 0 : x;
            var x1 = new Version(a.Major, a.Minor, P(a.Build));
            var x2 = new Version(b.Major, b.Minor, P(b.Build));
            return x1 > x2;
        }
    }

    /// <summary>"Explorer-style" sort so 8T comes before 12T.</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string x, string y)
        {
            x = x ?? ""; y = y ?? "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x.Substring(si, i - si).TrimStart('0'), b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    int c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    /// <summary>Writes the BTR tab icons (embedded PNGs) to disk, because CreateTaskpaneView3 wants file paths.</summary>
    public static class Icons
    {
        public static string[] EnsureTaskpaneIcons()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtrLibrary", "icons");
            Directory.CreateDirectory(dir);

            var sizes = new[] { 20, 32, 40, 64, 96, 128 };
            var paths = new string[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                paths[i] = Path.Combine(dir, $"btrlib_{sizes[i]}.png");
                using (var src = Assets.Open($"icon_{sizes[i]}.png"))
                using (var dst = File.Create(paths[i]))
                    src.CopyTo(dst);
            }
            return paths;
        }
    }

    /// <summary>Access to files embedded from the Assets folder (see the .csproj).</summary>
    public static class Assets
    {
        public static Stream Open(string name) =>
            typeof(Assets).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Missing embedded asset " + name);

        public static string[] Names => typeof(Assets).Assembly.GetManifestResourceNames();

        public static Image LoadImage(string name)
        {
            using (var s = Open(name))
            using (var img = Image.FromStream(s))
                return new Bitmap(img);
        }
    }
}
