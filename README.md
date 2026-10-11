# ClothingPluginInjection

Small Windows desktop tool for Virt-A-Mate that injects plugin blocks into `.vaj`
scene files inside `.var` packages.

A `.var` file is a `.zip` archive containing binary assets, a `meta.json`, and one
or more `.vaj` JSON scene files. This tool:

1. Opens a `.var` (via drag-and-drop or via the verifier CLI).
2. Lists every `.vaj` it finds.
3. On **Inject Plugin Blocks**, appends two blocks to each `.vaj`:
   - `components`: `{ "type": "MVRPluginManager" }` *(skipped if already present)*
   - `storables`: `{ "id": "<vaj filename without extension>", "plugins": { "plugin#0": "Stopper.ClothingPluginManager.latest:/Custom/Scripts/Stopper/ClothingPluginManager/ClothingPluginManager.cs" } }`
4. Saves the modified `.vaj`(s) back into the original `.var`, byte-preserving
   every other entry (binary siblings like `.vab`, `.jpg`, `.vam` survive the
   round-trip exactly).

## Build

Requires the .NET 8 SDK on Windows:

```bash
dotnet build VamInjector.slnx
```

## Run the GUI

```bash
dotnet run --project src/VamInjector
```

Drag a `.var` onto the window, click **Inject Plugin Blocks**.

## Headless verifier (optional)

`verify/` is a console project that links the same Services source files and
exercises Open / Patch / Save, asserting archive integrity (binary siblings
byte-identical, all entries present, JSON blocks correct). Useful as a
regression test:

```bash
dotnet run --project verify -- "<path-to-var>"
```

## Safety

The save-back path writes a sibling `.tmp` and atomically swaps it onto the
original (`File.Replace`). On any failure the `.tmp` is deleted and the
original is untouched. Still — **back up your `.var` files before running
this on anything you can't re-download**.

## Project layout

```
src/VamInjector/   WPF MVVM app (MainWindow, MainViewModel, Services)
verify/            Console verifier (links the same Services sources)
```

## How the save flow preserves binary siblings

`VarPackageService.Save` opens the source archive in read mode, creates a
sibling temp archive in create mode, copies each entry's *decompressed*
bytes into the temp archive (or writes the patched JSON for the `.vaj`s the
user edited), then `File.Replace`s the temp onto the source. The
decompressed content of binary siblings like `.vab` is preserved
byte-for-byte; their compressed representation in the zip differs (re-encoded
at `CompressionLevel.Optimal`) but the file content is identical.