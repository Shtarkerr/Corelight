using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Catalog;

public sealed class TweakCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, TweakDefinition> _byId;

    public TweakCatalog(IReadOnlyList<TweakDefinition> tweaks, IReadOnlyList<string> rejected)
    {
        Tweaks = tweaks;
        Rejected = rejected;
        _byId = tweaks.ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<TweakDefinition> Tweaks { get; }

    /// <summary>Твики, отброшенные валидатором, с причинами. В нормальной сборке список пуст.</summary>
    public IReadOnlyList<string> Rejected { get; }

    public TweakDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Загружает каталог, вшитый в сборку (Catalog/Data/*.json).</summary>
    public static TweakCatalog LoadEmbedded()
    {
        var assembly = typeof(TweakCatalog).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Corelight.Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => (n, ReadResource(assembly, n)));
        return Load(files);
    }

    public static TweakCatalog Load(IEnumerable<(string Name, string Json)> files)
    {
        var tweaks = new List<TweakDefinition>();
        var rejected = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, json) in files)
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions)
                ?? throw new InvalidDataException($"Пустой файл каталога {name}.");

            foreach (var tweak in file.Tweaks)
            {
                tweak.Category = file.Category;
                var errors = GuardrailValidator.Validate(tweak).ToList();
                if (!ids.Add(tweak.Id))
                    errors.Add("повторяющийся id");

                if (errors.Count == 0)
                    tweaks.Add(tweak);
                else
                    rejected.Add($"{tweak.Id}: {string.Join("; ", errors)}");
            }
        }
        return new TweakCatalog(tweaks, rejected);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class CatalogFile
    {
        public TweakCategory Category { get; init; }

        public List<TweakDefinition> Tweaks { get; init; } = [];
    }
}
