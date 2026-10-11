using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VamInjector.Models;
using VamInjector.Services;

namespace VamInjector.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private string? _currentArchivePath;
    public string? CurrentArchivePath
    {
        get => _currentArchivePath;
        private set { if (SetField(ref _currentArchivePath, value)) OnPropertyChanged(nameof(ArchiveDisplayName)); }
    }

    public string ArchiveDisplayName =>
        _currentArchivePath is null ? "(no .var loaded)" : Path.GetFileName(_currentArchivePath);

    public ObservableCollection<VajEntryInfo> VajEntries { get; } = new();

    private string _statusMessage = "Drop a .var file onto this window to begin.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetField(ref _lastError, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _lastError is not null;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                InjectCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand InjectCommand { get; }

    public MainViewModel()
    {
        InjectCommand = new RelayCommand(
            execute: _ => _ = InjectAsync(),
            canExecute: _ => !IsBusy && _currentArchivePath is not null && VajEntries.Count > 0);
    }

    public void OpenArchive(string archivePath)
    {
        LastError = null;
        try
        {
            var result = VarPackageService.Open(archivePath);

            CurrentArchivePath = result.ArchivePath;
            VajEntries.Clear();
            foreach (var entry in result.VajEntries)
                VajEntries.Add(entry);

            StatusMessage = result.VajEntries.Count == 0
                ? $"Loaded {Path.GetFileName(archivePath)} — no .vaj files found."
                : $"Loaded {Path.GetFileName(archivePath)} — {result.VajEntries.Count} .vaj file(s). Click Inject to apply plugin blocks.";

            InjectCommand.RaiseCanExecuteChanged();
        }
        catch (InvalidDataException)
        {
            LastError = $"Not a valid .var (zip) file: {Path.GetFileName(archivePath)}";
            ResetArchive("Invalid .var file.");
        }
        catch (Exception ex)
        {
            LastError = $"Failed to open {Path.GetFileName(archivePath)}: {ex.Message}";
            ResetArchive("Open failed.");
        }
    }

    private async Task InjectAsync()
    {
        if (_currentArchivePath is null) return;

        IsBusy = true;
        LastError = null;
        StatusMessage = "Reading .vaj entries…";

        var archivePath = _currentArchivePath;
        var entries = VajEntries.ToList(); // snapshot

        try
        {
            // Read + patch every .vaj on a background thread.
            var patched = await Task.Run(() => ReadAndPatchAll(archivePath, entries));

            StatusMessage = $"Saving patched archive ({patched.Count} .vaj file(s))…";

            // Save-back is synchronous IO; run on background to keep UI responsive.
            await Task.Run(() => VarPackageService.Save(archivePath, patched));

            StatusMessage = $"Injected plugin blocks into {patched.Count} .vaj file(s) — saved {Path.GetFileName(archivePath)}.";
        }
        catch (InvalidDataException)
        {
            LastError = "A .vaj entry contained invalid JSON and could not be parsed.";
            StatusMessage = "Inject failed — see error above.";
        }
        catch (JsonReaderException ex)
        {
            LastError = $"JSON parse error: {ex.Message}";
            StatusMessage = "Inject failed — see error above.";
        }
        catch (Exception ex)
        {
            LastError = $"Inject failed: {ex.Message}";
            StatusMessage = "Inject failed — see error above.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens the archive once, reads each .vaj stream, parses to JObject,
    /// runs the patch, and returns the dictionary keyed by zip entry name.
    /// Malformed JSON in one .vaj aborts the whole batch (reported via
    /// InvalidDataException); we don't want to half-inject.
    /// </summary>
    private static IReadOnlyDictionary<string, JObject> ReadAndPatchAll(
        string archivePath, IReadOnlyList<VajEntryInfo> entries)
    {
        var result = new Dictionary<string, JObject>(StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var info in entries)
        {
            var entry = archive.GetEntry(info.EntryName)
                ?? throw new InvalidDataException($"Entry disappeared mid-read: {info.EntryName}");

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(reader);

            var root = JObject.Load(jsonReader);
            VajInjectionService.Patch(root, info.Basename);

            result[info.EntryName] = root;
        }

        return result;
    }

    private void ResetArchive(string status)
    {
        CurrentArchivePath = null;
        VajEntries.Clear();
        StatusMessage = status;
        InjectCommand.RaiseCanExecuteChanged();
    }
}