using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BtrLibrary
{
    /// <summary>One part or assembly in the library.</summary>
    public sealed class LibraryItem
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public string[] Tags { get; set; } = new string[0];
        public string SearchText { get; set; }

        public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

        /// <summary>STEP files get converted to native SOLIDWORKS files before inserting.</summary>
        public bool IsNeutral => Extension == ".step" || Extension == ".stp";

        public string BaseName => System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path);

        /// <summary>Optional sidecar image: "Falcon 500.png" next to "Falcon 500.SLDPRT".</summary>
        public string PreviewPath
        {
            get
            {
                foreach (var ext in new[] { ".png", ".jpg" })
                {
                    var p = System.IO.Path.Combine(Folder, BaseName + ext);
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Scans a library folder laid out as  Root\Vendor\Category[\Subcategory...]\Part.SLDPRT
    /// Folders starting with "_" (e.g. _converted) and Office/SOLIDWORKS temp files are skipped.
    /// </summary>
    public static class LibraryIndex
    {
        private static readonly HashSet<string> Extensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".sldprt", ".sldasm", ".step", ".stp" };

        public static List<LibraryItem> Scan(string root)
        {
            var items = new List<LibraryItem>();
            if (!Directory.Exists(root)) return items;
            Walk(root, root, items);

            // If a STEP and a native file share a name in the same folder, keep only the native one.
            return items
                .GroupBy(i => System.IO.Path.Combine(i.Folder, i.BaseName), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(i => i.IsNeutral ? 1 : 0).First())
                .ToList();
        }

        private static void Walk(string root, string dir, List<LibraryItem> items)
        {
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileName(file);
                if (name.StartsWith("~$")) continue;
                if (!Extensions.Contains(System.IO.Path.GetExtension(file))) continue;
                items.Add(Build(root, file));
            }

            foreach (var sub in dirs)
            {
                var name = System.IO.Path.GetFileName(sub);
                if (name.StartsWith("_") || name.StartsWith(".")) continue;
                Walk(root, sub, items);
            }
        }

        private static LibraryItem Build(string root, string file)
        {
            var rel = file.Substring(root.Length).TrimStart('\\', '/');
            var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            string vendor = parts.Length > 1 ? parts[0] : "Misc";
            string category = parts.Length > 2
                ? string.Join(" / ", parts.Skip(1).Take(parts.Length - 2))
                : "General";

            var item = new LibraryItem
            {
                Path = file,
                Name = System.IO.Path.GetFileNameWithoutExtension(file).Replace('_', ' '),
                Vendor = vendor,
                Category = category,
            };

            ReadMeta(item);

            item.SearchText = string.Join(" ",
                new[] { item.Name, item.Vendor, item.Category, item.Description ?? "" }
                    .Concat(item.Tags)).ToLowerInvariant();
            return item;
        }

        /// <summary>
        /// Optional sidecar "Part Name.meta" with simple key=value lines:
        ///   name=Falcon 500 (Kraken-style mount)
        ///   description=Brushless motor, 1/2" hex output
        ///   tags=motor, brushless, falcon, ctre
        ///   url=https://store.ctr-electronics.com/...
        /// </summary>
        private static void ReadMeta(LibraryItem item)
        {
            var meta = System.IO.Path.Combine(item.Folder, item.BaseName + ".meta");
            if (!File.Exists(meta)) return;

            foreach (var raw in File.ReadAllLines(meta))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "name": item.Name = val; break;
                    case "description": item.Description = val; break;
                    case "url": item.Url = val; break;
                    case "tags":
                        item.Tags = val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(s => s.Trim()).ToArray();
                        break;
                }
            }
        }
    }
}
