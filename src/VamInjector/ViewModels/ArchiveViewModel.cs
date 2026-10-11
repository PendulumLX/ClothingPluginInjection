using System.IO;
using VamInjector.Services;

namespace VamInjector.ViewModels;

/// <summary>
/// One loaded .var archive in the multi-file UI. Holds the path and the
/// list of .vaj entries discovered at load time. The actual JSON parse
/// of each .vaj happens lazily inside PatchOneAsync during inject.
/// </summary>
public sealed class ArchiveViewModel
{
    public string FilePath { get; }
    public string FileName { get; }
    public IReadOnlyList<VajEntryInfo> VajEntries { get; }

    public ArchiveViewModel(string filePath, IReadOnlyList<VajEntryInfo> vajEntries)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        VajEntries = vajEntries;
    }

    /// <summary>Display text in the archives ListBox.</summary>
    public string Display => $"{FileName}  —  {VajEntries.Count} .vaj";
}