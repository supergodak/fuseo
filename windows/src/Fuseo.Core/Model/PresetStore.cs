using System.Reflection;
using System.Text.Json;

namespace Fuseo.Core.Model;

/// <summary>
/// 埋め込みリソース（Mac/iOS と共有のプリセット JSON）を読み、スキーマ検証して返す。
/// 検証規約は Mac 版と同じ: 全ルールに basis 必須・fixed は region 必須・dynamic は detector 必須・
/// fallbackRegion は dynamic 専用・id はプリセット内で一意。
/// </summary>
public static class PresetStore
{
    private const string Prefix = "Fuseo.Presets.";

    public static IReadOnlyList<DocumentPreset> LoadAll()
    {
        var asm = typeof(PresetStore).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0) throw new PresetException("プリセットが埋め込まれていない");
        return names.Select(n => Load(asm, n)).ToList();
    }

    private static DocumentPreset Load(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new PresetException($"リソースを開けない: {name}");
        DocumentPreset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<DocumentPreset>(stream);
        }
        catch (JsonException e)
        {
            throw new PresetException($"{name}: JSON を読めない（{e.Path}）: {e.Message}");
        }
        if (preset is null) throw new PresetException($"空のプリセット: {name}");
        Validate(preset, name);
        return preset;
    }

    public static void Validate(DocumentPreset preset, string source)
    {
        if (preset.SchemaVersion != 1) throw new PresetException($"{source}: schemaVersion が 1 ではない");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in preset.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) throw new PresetException($"{source}: id が空のルール");
            if (!ids.Add(rule.Id)) throw new PresetException($"{source}: id が重複 {rule.Id}");
            if (string.IsNullOrWhiteSpace(rule.Basis)) throw new PresetException($"{source}: {rule.Id} に basis が無い");
            switch (rule.Kind)
            {
                case RuleKind.Fixed when rule.Region is null:
                    throw new PresetException($"{source}: fixed ルール {rule.Id} に region が無い");
                case RuleKind.Fixed when rule.FallbackRegion is not null:
                    throw new PresetException($"{source}: fallbackRegion は dynamic 専用 ({rule.Id})");
                case RuleKind.Dynamic when rule.Detector is null:
                    throw new PresetException($"{source}: dynamic ルール {rule.Id} に detector が無い");
            }
        }
    }
}

public sealed class PresetException(string message) : Exception(message);
