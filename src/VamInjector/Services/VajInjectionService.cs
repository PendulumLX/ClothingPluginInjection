using Newtonsoft.Json.Linq;

namespace VamInjector.Services;

/// <summary>
/// Patches a parsed .vaj JSON document with the two fixed plugin blocks.
/// Mutates the JObject in place; returns the same instance for convenience.
/// </summary>
public static class VajInjectionService
{
    public const string ComponentsBlockType = "MVRPluginManager";
    public const string PluginReference =
        "Stopper.ClothingPluginManager.latest:/Custom/Scripts/Stopper/ClothingPluginManager/ClothingPluginManager.cs";
    public const string PluginKey = "plugin#0";

    /// <summary>
    /// Fully idempotent: running on an already-patched .vaj adds nothing.
    /// The components block is skipped if any entry with
    /// <c>type: "MVRPluginManager"</c> already exists; the storables block
    /// is skipped if a storable with the .vaj's basename already has
    /// <c>plugins["plugin#0"]</c> set to our exact plugin reference.
    /// </summary>
    /// <param name="vajRoot">Parsed .vaj JSON. Mutated in place.</param>
    /// <param name="vajBasename">.vaj file basename WITHOUT the .vaj extension.</param>
    public static JObject Patch(JObject vajRoot, string vajBasename)
    {
        InjectComponent(vajRoot);
        InjectStorable(vajRoot, vajBasename);
        return vajRoot;
    }

    private static void InjectComponent(JObject root)
    {
        var components = EnsureArray(root, "components");

        // Skip if MVRPluginManager already registered — prevents duplicates
        // on re-injection. (Plain `Equals` is fine here because we wrote the
        // value as a JValue ourselves; any other component with the same type
        // string should also be considered a duplicate we don't want.)
        foreach (var token in components)
        {
            if (token is JObject obj &&
                obj["type"] is JValue v &&
                v.Type == JTokenType.String &&
                string.Equals((string?)v.Value, ComponentsBlockType, StringComparison.Ordinal))
            {
                return;
            }
        }

        components.Add(new JObject(new JProperty("type", ComponentsBlockType)));
    }

    private static void InjectStorable(JObject root, string vajBasename)
    {
        var storables = EnsureArray(root, "storables");

        // Skip if this .vaj already has a storable matching its basename and
        // already pointing at our exact plugin reference. This makes the
        // operation fully idempotent — re-running the injector (or running
        // it on a .vaj someone else pre-edited) does not pile up duplicates.
        foreach (var token in storables)
        {
            if (token is JObject obj &&
                (string?)obj["id"] == vajBasename &&
                obj["plugins"] is JObject plugs &&
                (string?)plugs[PluginKey] == PluginReference)
            {
                return;
            }
        }

        var newStorable = new JObject
        {
            new JProperty("id", vajBasename),
            new JProperty("plugins", new JObject
            {
                new JProperty(PluginKey, PluginReference),
            }),
        };

        storables.Add(newStorable);
    }

    private static JArray EnsureArray(JObject root, string key)
    {
        if (root[key] is JArray existing)
            return existing;

        var created = new JArray();
        root[key] = created;
        return created;
    }
}