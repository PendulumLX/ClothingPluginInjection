using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VamInjector.Services;

/// <summary>
/// Result of opening a .var archive. Holds metadata only — the underlying
/// ZipArchive is closed before this is returned. The .vaj entries' JSON
/// content is parsed lazily by the caller (e.g. during Inject).
/// </summary>
public sealed record VarOpenResult(string ArchivePath, IReadOnlyList<VajEntryInfo> VajEntries);

public sealed record VajEntryInfo(string EntryName, string Basename);

/// <summary>
/// Open and save-back .var packages (which are zip archives).
///
/// Save strategy is "temp file + atomic swap":
///   1. Open source .var in Read mode.
///   2. Create sibling .tmp in Create mode.
///   3. For each source entry: copy raw bytes (unmodified), or write the
///      patched JSON (modified .vaj).
///   4. Close both archives (source first), then File.Replace the .tmp
///      over the source — atomic on NTFS.
///
/// Note on byte-equivalence: ZipArchive doesn't expose original compressed
/// bytes per-entry, so unmodified entries are decompressed-then-recompressed
/// (Optimal). The decompressed content of binary siblings (.vab, .jpg, .vam)
/// is preserved byte-for-byte, but the entry's compressed representation in
/// the zip will differ. Verify integrity by hashing the *extracted* files,
/// not the raw zip entry bytes.
/// </summary>
public static class VarPackageService
{
    public static VarOpenResult Open(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var vajEntries = new List<VajEntryInfo>();

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(entry.Name))
                continue; // skip directory entries ending in .vaj (none expected, but safe)

            var basename = Path.GetFileNameWithoutExtension(entry.Name);
            vajEntries.Add(new VajEntryInfo(entry.FullName, basename));
        }

        return new VarOpenResult(archivePath, vajEntries);
    }

    /// <summary>
    /// Atomically rewrites the .var at <paramref name="archivePath"/>,
    /// substituting the patched JSON for each entry named in
    /// <paramref name="patchedVajs"/> (key = zip entry full name) and
    /// copying every other entry's decompressed content unchanged.
    /// </summary>
    public static void Save(string archivePath, IReadOnlyDictionary<string, JObject> patchedVajs)
    {
        var tempPath = archivePath + ".tmp";

        try
        {
            using (var source = ZipFile.OpenRead(archivePath))
            using (var temp = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var sourceEntry in source.Entries)
                {
                    if (string.IsNullOrEmpty(sourceEntry.Name))
                        continue; // directory entries — skip

                    var newEntry = temp.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);

                    using var sourceStream = sourceEntry.Open();
                    using var destStream = newEntry.Open();

                    if (patchedVajs.TryGetValue(sourceEntry.FullName, out var patched))
                    {
                        WritePatchedJson(destStream, patched);
                    }
                    else
                    {
                        sourceStream.CopyTo(destStream);
                    }
                }
            }

            // Atomic on NTFS. Fall back to plain overwrite if File.Replace
            // refuses (e.g. cross-volume move).
            try
            {
                File.Replace(tempPath, archivePath, destinationBackupFileName: null);
            }
            catch (IOException)
            {
                File.Move(tempPath, archivePath, overwrite: true);
            }
        }
        catch
        {
            // Clean up the temp file on any failure so we never leave a
            // half-written .tmp lying next to the user's .var.
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    private static void WritePatchedJson(Stream dest, JObject patched)
    {
        using var writer = new StreamWriter(dest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.Indented };
        patched.WriteTo(jsonWriter);
    }
}