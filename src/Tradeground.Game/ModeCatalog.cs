using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradeground.Game;

/// <summary>Loads and validates the game modes shipped inside this assembly.</summary>
public sealed class ModeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, GameMode> _modes;

    public ModeCatalog(IEnumerable<GameMode> modes)
    {
        _modes = new Dictionary<string, GameMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in modes)
        {
            var errors = mode.Validate();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Mode '{mode.Id}' is invalid: {string.Join(" ", errors)}");
            }

            if (!_modes.TryAdd(mode.Id, mode))
            {
                throw new InvalidOperationException($"Duplicate mode id '{mode.Id}'.");
            }
        }
    }

    public IReadOnlyCollection<GameMode> All => _modes.Values;

    public bool TryGet(string id, [NotNullWhen(true)] out GameMode? mode) => _modes.TryGetValue(id, out mode);

    public static ModeCatalog LoadBuiltIn()
    {
        var assembly = typeof(ModeCatalog).Assembly;
        var modes = assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".mode.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Missing resource {name}.");
                return JsonSerializer.Deserialize<GameMode>(stream, JsonOptions)
                    ?? throw new InvalidOperationException($"Resource {name} is empty.");
            })
            .ToList();

        return new ModeCatalog(modes);
    }
}
