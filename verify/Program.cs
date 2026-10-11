using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VamInjector.Services;

// Headless verifier: exercises VarPackageService.Open / VajInjectionService.Patch /
// VarPackageService.Save against a real .var, then asserts archive integrity.
//
// Usage: dotnet run --project VamInjector.Verify.csproj -- <path-to-var>

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: verify <path-to-var>");
    return 2;
}

var archivePath = args[0];
if (!File.Exists(archivePath))
{
    Console.Error.WriteLine($"File not found: {archivePath}");
    return 2;
}

Console.WriteLine($"=== Verifying: {archivePath} ===");

// 1. Open: list .vaj entries.
var openResult = VarPackageService.Open(archivePath);
Console.WriteLine($"\n[Open] Found {openResult.VajEntries.Count} .vaj entries:");
foreach (var v in openResult.VajEntries)
    Console.WriteLine($"  - {v.EntryName}  (basename={v.Basename})");

if (openResult.VajEntries.Count == 0)
{
    Console.Error.WriteLine("FAIL: no .vaj entries.");
    return 1;
}

// 2. Hash a binary sibling (.vab or first non-.vaj non-meta entry) before patch.
//    We'll compare extracted content hash after Save.
var baselineSiblings = ListBinarySiblings(archivePath);
Console.WriteLine($"\n[Baseline] Hashing {baselineSiblings.Count} binary sibling(s) before patch:");
var baselineHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var sib in baselineSiblings)
{
    var bytes = ExtractEntryToBytes(archivePath, sib);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    baselineHashes[sib] = hash;
    Console.WriteLine($"  - {sib}  ({bytes.Length} bytes)  sha256={hash[..16]}…");
}

// 3. Read each .vaj, patch with VajInjectionService.Patch.
var patched = new Dictionary<string, JObject>(StringComparer.Ordinal);
Console.WriteLine("\n[Patch] Reading and patching .vaj entries:");
foreach (var info in openResult.VajEntries)
{
    using var archive = ZipFile.OpenRead(archivePath);
    var entry = archive.GetEntry(info.EntryName)!;
    using var stream = entry.Open();
    using var reader = new StreamReader(stream);
    using var jr = new JsonTextReader(reader);
    var root = JObject.Load(jr);

    var beforeComponents = (root["components"] as JArray)?.Count ?? 0;
    var beforeStorables = (root["storables"] as JArray)?.Count ?? 0;

    VajInjectionService.Patch(root, info.Basename);

    var afterComponents = (root["components"] as JArray)?.Count ?? 0;
    var afterStorables = (root["storables"] as JArray)?.Count ?? 0;

    Console.WriteLine($"  - {info.EntryName}: components {beforeComponents}→{afterComponents}, storables {beforeStorables}→{afterStorables}");

    patched[info.EntryName] = root;
}

// 4. Save back atomically.
Console.WriteLine("\n[Save] Writing patched archive atomically…");
VarPackageService.Save(archivePath, patched);

// 5. Re-open and verify.
var reopen = VarPackageService.Open(archivePath);
Console.WriteLine($"\n[Reopen] Archive lists {reopen.VajEntries.Count} .vaj entries after save.");
if (reopen.VajEntries.Count != openResult.VajEntries.Count)
{
    Console.Error.WriteLine($"FAIL: .vaj count changed ({openResult.VajEntries.Count} → {reopen.VajEntries.Count})");
    return 1;
}

// 6. Verify each .vaj contains the expected injected blocks.
Console.WriteLine("\n[Verify .vaj] Checking injected blocks in each .vaj:");
var allOk = true;
foreach (var info in reopen.VajEntries)
{
    using var archive = ZipFile.OpenRead(archivePath);
    var entry = archive.GetEntry(info.EntryName)!;
    using var stream = entry.Open();
    using var reader = new StreamReader(stream);
    using var jr = new JsonTextReader(reader);
    var root = JObject.Load(jr);

    var components = root["components"] as JArray;
    var storables = root["storables"] as JArray;

    var hasComponent = components?.Any(c =>
        c is JObject o &&
        o["type"] is JValue v &&
        v.Type == JTokenType.String &&
        (string?)v.Value == VajInjectionService.ComponentsBlockType) ?? false;

    var lastStorable = storables?.LastOrDefault() as JObject;
    JObject? plugs = null;
    var storableOk = lastStorable is not null
        && (string?)lastStorable["id"] == info.Basename
        && (plugs = lastStorable["plugins"] as JObject) is not null
        && (string?)plugs[VajInjectionService.PluginKey] == VajInjectionService.PluginReference;

    var mark = (hasComponent && storableOk) ? "OK" : "FAIL";
    Console.WriteLine($"  [{mark}] {info.EntryName}  components-has-MVRPluginManager={hasComponent}  last-storable-id={lastStorable?["id"]}  plugins-key={(plugs?[VajInjectionService.PluginKey] is JValue pv ? pv.Value : null)}");
    if (!(hasComponent && storableOk)) allOk = false;
}

// 7. Verify binary sibling integrity (extracted bytes identical to baseline).
Console.WriteLine("\n[Verify siblings] Comparing extracted bytes of binary entries after save:");
foreach (var sib in baselineSiblings)
{
    var bytes = ExtractEntryToBytes(archivePath, sib);
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var match = hash == baselineHashes[sib];
    var mark = match ? "OK" : "FAIL";
    Console.WriteLine($"  [{mark}] {sib}  baseline={baselineHashes[sib][..16]}…  after={hash[..16]}…");
    if (!match) allOk = false;
}

// 8. Verify all original entries still present.
var afterEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
using (var archive = ZipFile.OpenRead(archivePath))
    foreach (var e in archive.Entries)
        afterEntryNames.Add(e.FullName);

using (var origArchive = ZipFile.OpenRead(archivePath + ".bak"))
{
    Console.WriteLine("\n[Verify entry list] Comparing entry names against backup:");
    foreach (var origEntry in origArchive.Entries)
    {
        var present = afterEntryNames.Contains(origEntry.FullName);
        var mark = present ? "OK" : "FAIL (missing)";
        Console.WriteLine($"  [{mark}] {origEntry.FullName}");
        if (!present) allOk = false;
    }
}

Console.WriteLine();
Console.WriteLine(allOk ? "=== ALL CHECKS PASSED ===" : "=== ONE OR MORE CHECKS FAILED ===");
return allOk ? 0 : 1;

// ---- helpers ----

static List<string> ListBinarySiblings(string archivePath)
{
    var list = new List<string>();
    using var archive = ZipFile.OpenRead(archivePath);
    foreach (var entry in archive.Entries)
    {
        if (string.IsNullOrEmpty(entry.Name)) continue;
        // Skip .vaj and .json — we want binary asset files like .vab, .png, .jpg, .vam
        if (entry.Name.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase)) continue;
        if (entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
        list.Add(entry.FullName);
    }
    return list;
}

static byte[] ExtractEntryToBytes(string archivePath, string entryName)
{
    using var archive = ZipFile.OpenRead(archivePath);
    var e = archive.GetEntry(entryName)!;
    using var s = e.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
}