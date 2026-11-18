using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BtrLibrary
{
    [DataContract]
    public sealed class Manifest
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        /// <summary>Optional: where shared 3D previews live. Default: "previews/" next to library.json.</summary>
        [DataMember(Name = "previews")] public string Previews { get; set; }
        [DataMember(Name = "parts")] public List<ManifestPart> Parts { get; set; } = new List<ManifestPart>();
    }

    [DataContract]
    public sealed class ManifestPart
    {
        [DataMember(Name = "vendor")] public string Vendor { get; set; }
        [DataMember(Name = "category")] public string Category { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "sku")] public string Sku { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "productUrl")] public string ProductUrl { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "tags")] public string[] Tags { get; set; }
    }

    public sealed class SyncResult
    {
        public int Total, Downloaded, AlreadyHad, Previews;
        public List<string> Failures = new List<string>();

        public override string ToString()
        {
            var s = Downloaded > 0
                ? $"Synced: {Downloaded} new part(s), {AlreadyHad} already in library"
                : $"Library up to date ({AlreadyHad} parts)";
            if (Previews > 0) s += $", {Previews} new 3D preview(s)";
            if (Failures.Count > 0) s += $", {Failures.Count} failed";
            return s;
        }
    }

    /// <summary>
    /// Downloads the parts listed in library.json straight from the vendors into the library folder,
    /// so teammates never have to fetch CAD by hand. Files that already exist are skipped.
    /// The list comes from Settings.ManifestUrl if set, otherwise the team list in the btr-library GitHub repo
    /// (Settings.DefaultManifestUrl), falling back to the library.json shipped next to the DLL when offline.
    /// </summary>
    public static class LibrarySync
    {
        private const int Parallel = 4;

        public static async Task<SyncResult> RunAsync(string root, IProgress<string> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BtrLibrary-SolidWorks/0.2");

                progress?.Report("Reading parts list…");
                var (manifest, manifestUrl) = await LoadManifestAsync(http, ct).ConfigureAwait(false);
                var result = new SyncResult { Total = manifest.Parts.Count };

                Directory.CreateDirectory(root);
                var todo = new List<(ManifestPart part, string target)>();
                var partFiles = new List<string>();

                foreach (var part in manifest.Parts.Where(p => !string.IsNullOrWhiteSpace(p.Url)))
                {
                    string dir = FolderFor(root, part);
                    Directory.CreateDirectory(dir);
                    string baseName = Sanitize(part.Name ?? part.Sku ?? Path.GetFileNameWithoutExtension(part.Url));
                    WriteMeta(Path.Combine(dir, baseName + ".meta"), part);

                    string ext = Path.GetExtension(new Uri(part.Url).AbsolutePath).ToLowerInvariant();
                    string target = Path.Combine(dir, baseName + (ext == ".zip" ? "" : ext));
                    if (ext != ".zip" && !NativeExists(dir, baseName)) partFiles.Add(target);

                    if (ext == ".zip" ? Directory.Exists(target) : File.Exists(target) || NativeExists(dir, baseName))
                        result.AlreadyHad++;
                    else
                        todo.Add((part, target));
                }

                int done = 0;
                using (var gate = new SemaphoreSlim(Parallel))
                {
                    var tasks = todo.Select(async item =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await DownloadAsync(http, item.part.Url, item.target, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref result.Downloaded);
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            lock (result.Failures) result.Failures.Add($"{item.part.Name}: {ex.Message}");
                        }
                        finally
                        {
                            gate.Release();
                            int n = Interlocked.Increment(ref done);
                            progress?.Report($"Downloading parts {n}/{todo.Count}…");
                        }
                    });
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }

                await FetchPreviewsAsync(http, manifest, manifestUrl, partFiles, result, progress, ct).ConfigureAwait(false);
                return result;
            }
        }

        /// <summary>
        /// Pulls pre-built 3D previews (made with "Export previews for sharing") so nobody has to convert parts
        /// just to see them. Previews are named by file contents, so a missing or stale one is simply not found.
        /// Best effort: any failure here is ignored.
        /// </summary>
        private static async Task FetchPreviewsAsync(HttpClient http, Manifest manifest, string manifestUrl,
            List<string> partFiles, SyncResult result, IProgress<string> progress, CancellationToken ct)
        {
            Func<string, Task<byte[]>> fetch;
            try
            {
                string spec = string.IsNullOrWhiteSpace(manifest.Previews) ? "previews/" : manifest.Previews.Trim();
                if (!spec.EndsWith("/")) spec += "/";

                if (manifestUrl != null || Uri.IsWellFormedUriString(spec, UriKind.Absolute))
                {
                    var baseUri = manifestUrl != null ? new Uri(new Uri(manifestUrl), spec) : new Uri(spec);
                    fetch = async name =>
                    {
                        using (var resp = await http.GetAsync(new Uri(baseUri, name), ct).ConfigureAwait(false))
                            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false) : null;
                    };
                }
                else
                {
                    // Parts list shipped with the add-in: previews\ folder next to the DLL.
                    string dir = Path.Combine(Path.GetDirectoryName(typeof(LibrarySync).Assembly.Location), spec.TrimEnd('/'));
                    fetch = name =>
                    {
                        var f = Path.Combine(dir, name);
                        return Task.FromResult(File.Exists(f) ? File.ReadAllBytes(f) : null);
                    };
                }
            }
            catch { return; }

            var wanted = new List<string>();
            foreach (var file in partFiles)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    string name = MeshCache.FileNameFor(file, "");
                    if (!File.Exists(Path.Combine(MeshCache.Dir, name))) wanted.Add(name);
                }
                catch { /* unreadable file: skip */ }
            }
            if (wanted.Count == 0) return;

            progress?.Report("Fetching 3D previews…");
            using (var gate = new SemaphoreSlim(Parallel))
            {
                await Task.WhenAll(wanted.Select(async name =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (MeshCache.Import(name, await fetch(name).ConfigureAwait(false)))
                            Interlocked.Increment(ref result.Previews);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { /* previews are optional */ }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }
        }

        private static async Task<(Manifest, string)> LoadManifestAsync(HttpClient http, CancellationToken ct)
        {
            string custom = Settings.ManifestUrl?.Trim();
            string url = string.IsNullOrEmpty(custom) ? Settings.DefaultManifestUrl : custom;
            string json;
            try
            {
                json = await http.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception) when (string.IsNullOrEmpty(custom))
            {
                // Offline or GitHub unreachable: use the list that shipped with the add-in.
                string local = Path.Combine(Path.GetDirectoryName(typeof(LibrarySync).Assembly.Location), "library.json");
                if (!File.Exists(local))
                    throw new FileNotFoundException("Couldn't reach the team parts list on GitHub and there's no library.json next to BtrLibrary.dll.");
                json = File.ReadAllText(local);
                url = null; // previews then come from the previews folder next to the DLL
            }

            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return ((Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(ms), url);
        }

        private static async Task DownloadAsync(HttpClient http, string url, string target, CancellationToken ct)
        {
            string temp = target + ".part";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");

                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(temp))
                    await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
            }

            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(temp, target);
                File.Delete(temp);
            }
            else
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }
        }

        private static string FolderFor(string root, ManifestPart part)
        {
            var segments = new List<string> { Sanitize(part.Vendor ?? "Misc") };
            segments.AddRange((part.Category ?? "General")
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(Sanitize));
            return Path.Combine(root, Path.Combine(segments.ToArray()));
        }

        /// <summary>Someone may already have converted/replaced the STEP with a native file of the same name.</summary>
        private static bool NativeExists(string dir, string baseName) =>
            File.Exists(Path.Combine(dir, baseName + ".SLDPRT")) || File.Exists(Path.Combine(dir, baseName + ".SLDASM"));

        private static void WriteMeta(string path, ManifestPart part)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Written by BTR Library sync. Edit library.json instead.");
            // Names like 1/2" Hex can't be file names; keep the real name for the tree.
            if (!string.IsNullOrEmpty(part.Name) && Sanitize(part.Name) != part.Name) sb.AppendLine("name=" + part.Name);
            if (!string.IsNullOrEmpty(part.Description)) sb.AppendLine("description=" + part.Description);
            var tags = (part.Tags ?? new string[0]).ToList();
            if (!string.IsNullOrEmpty(part.Sku)) tags.Add(part.Sku);
            if (tags.Count > 0) sb.AppendLine("tags=" + string.Join(", ", tags.Distinct()));
            if (!string.IsNullOrEmpty(part.ProductUrl)) sb.AppendLine("url=" + part.ProductUrl);

            string text = sb.ToString();
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        }

        private static string Sanitize(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            name = name.Replace("\"", "in");   // 1/2" Hex -> 1-2in Hex
            var s = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
            return s.Length == 0 ? "_" : s;
        }
    }
}
