using System.IO;
using System.Windows;
using VamInjector.ViewModels;

namespace VamInjector;

/// <summary>
/// Drag-drop is wired here in the view because it's pure UI plumbing —
/// the ViewModel just receives a path string. DragEnter/DragOver share
/// the same acceptance predicate so the cursor feedback is consistent.
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

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        // Take the first dropped file; multi-file drops are ignored to
        // avoid ambiguity (one .var at a time keeps the workflow simple).
        var first = paths[0];

        if (DataContext is MainViewModel vm)
            vm.OpenArchive(first);
    }

    private static void UpdateDragEffect(DragEventArgs e)
    {
        e.Effects = TryGetSingleVarPath(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool TryGetSingleVarPath(IDataObject data, out string path)
    {
        path = null!;
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1) return false;

        var candidate = paths[0];
        if (!candidate.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(candidate)) return false;

        path = candidate;
        return true;
    }
}