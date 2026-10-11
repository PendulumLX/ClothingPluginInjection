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
    /// <summary>All archives currently loaded (each drop replaces this list).</summary>
    public ObservableCollection<ArchiveViewModel> Archives { get; } = new();

    private ArchiveViewModel? _selectedArchive;
    public ArchiveViewModel? SelectedArchive
    {
        get => _selectedArchive;
        set
        {
            if (SetField(ref _selectedArchive, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedVajEntries));
            }
        }
    }

    public bool HasSelection => _selectedArchive is not null;

    /// <summary>
    /// Bound by the right ListBox. Returns the selected archive's entries,
    /// or an empty list when nothing is selected so the binding never nulls.
    /// </summary>
    public IReadOnlyList<VajEntryInfo> SelectedVajEntries =>
        _selectedArchive?.VajEntries ?? Array.Empty<VajEntryInfo>();

    private string _statusMessage = "Drop one or more .var files onto this window to begin.";
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
            execute: _ => _ = InjectAllAsync(),
            canExecute: _ => !IsBusy && Archives.Count > 0);
    }

    /// <summary>
    /// Loads the given paths. Replaces any previously loaded archives.
    /// Each path is opened independently — failures are collected into
    /// LastError and the survivors still load.
    /// </summary>
    public void OpenArchives(IEnumerable<string> paths)
    {
        LastError = null;
        Archives.Clear();
        SelectedArchive = null;

        var loadedNames = new List<string>();
        var failures = new List<string>();

        foreach (var path in paths)
        {
            if (!path.EndsWith(".var", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{Path.GetFileName(path)} (not a .var file)");
                continue;
            }
            if (!File.Exists(path))
            {
                failures.Add($"{Path.GetFileName(path)} (not found)");
                continue;
            }

            try
            {
                var result = VarPackageService.Open(path);
                Archives.Add(new ArchiveViewModel(path, result.VajEntries));
                loadedNames.Add(Path.GetFileName(path));
            }
            catch (InvalidDataException)
            {
                failures.Add($"{Path.GetFileName(path)} (not a valid zip)");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        // Auto-select the first archive so the .vaj preview is populated
        // immediately on a fresh drop.
        if (Archives.Count > 0)
            SelectedArchive = Archives[0];

        StatusMessage = BuildOpenStatusMessage(loadedNames.Count, failures);
        if (failures.Count > 0)
            LastError = "Some files were skipped:\n  • " + string.Join("\n  • ", failures);

        InjectCommand.RaiseCanExecuteChanged();
    }

    private string BuildOpenStatusMessage(int loadedCount, IReadOnlyList<string> failures)
    {
        if (loadedCount == 0)
            return "No archives loaded.";

        var totalVaj = 0;
        foreach (var a in Archives) totalVaj += a.VajEntries.Count;

        var msg = $"Loaded {loadedCount} archive(s) — {totalVaj} .vaj file(s) total.";
        if (failures.Count > 0) msg += $" ({failures.Count} skipped)";
        return msg;
    }

    private async Task InjectAllAsync()
    {
        if (Archives.Count == 0) return;

        IsBusy = true;
        LastError = null;

        // Snapshot so the iteration isn't disturbed if the collection changes.
        var snapshot = Archives.ToList();
        var failures = new List<string>();
        var totalPatched = 0;

        try
        {
            for (var i = 0; i < snapshot.Count; i++)
            {
                var archive = snapshot[i];
                StatusMessage = $"Patching {i + 1}/{snapshot.Count}: {archive.FileName}…";

                try
                {
                    var count = await Task.Run(() => PatchOneArchive(archive));
                    totalPatched += count;
                }
                catch (Exception ex)
                {
                    failures.Add($"{archive.FileName}: {ex.Message}");
                }
            }

            var okArchives = snapshot.Count - failures.Count;
            StatusMessage = failures.Count == 0
                ? $"Injected plugin blocks into {totalPatched} .vaj file(s) across {okArchives} archive(s)."
                : $"Injected into {totalPatched} .vaj file(s) across {okArchives} archive(s). {failures.Count} failed — see error below.";
            if (failures.Count > 0)
                LastError = "Some archives failed:\n  • " + string.Join("\n  • ", failures);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens one archive, reads every .vaj stream, patches with the
    /// injection service, and saves atomically. Returns the number of
    /// .vaj files patched.
    /// </summary>
    private static int PatchOneArchive(ArchiveViewModel archive)
    {
        var patched = new Dictionary<string, JObject>(StringComparer.Ordinal);

        using (var zip = ZipFile.OpenRead(archive.FilePath))
        {
            foreach (var info in archive.VajEntries)
            {
                var entry = zip.GetEntry(info.EntryName)
                    ?? throw new InvalidDataException($"Entry disappeared: {info.EntryName}");

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(reader);

                var root = JObject.Load(jsonReader);
                VajInjectionService.Patch(root, info.Basename);

                patched[info.EntryName] = root;
            }
        }

        VarPackageService.Save(archive.FilePath, patched);
        return patched.Count;
    }
}