using System.IO;
using System.Windows;
using VamInjector.ViewModels;

namespace VamInjector;

/// <summary>
/// Drag-drop is wired here in the view because it's pure UI plumbing —
/// the ViewModel just receives paths. DragEnter/DragOver share the same
/// acceptance predicate so cursor feedback is consistent.
///
/// We accept the gesture as long as at least one dropped file ends in .var.
/// The VM handles per-file validation (non-.var files are reported in
/// the error banner rather than rejected at the gesture level, so the
/// user can drop a mixed selection without the window doing nothing).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
        => UpdateDragEffect(e);

    private void Window_DragOver(object sender, DragEventArgs e)
        => UpdateDragEffect(e);

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (DataContext is MainViewModel vm)
            vm.OpenArchives(paths);
    }

    private static void UpdateDragEffect(DragEventArgs e)
    {
        e.Effects = TryGetPaths(e.Data, out var paths) && paths.Any(IsVarFile)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool TryGetPaths(IDataObject data, out string[] paths)
    {
        paths = Array.Empty<string>();
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] raw || raw.Length == 0) return false;
        paths = raw;
        return true;
    }

    private static bool IsVarFile(string path)
        => path.EndsWith(".var", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}