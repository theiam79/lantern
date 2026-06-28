using System.Text.Json;
using Lantern.Contracts;
using Lantern.Engine;

namespace Lantern.Server.Content;

// JSON shapes (camelCase). Unknown keys are ignored (forward-compat).
file sealed record PackFile(int SchemaVersion, string PackId, WeaponJson[]? Weapons, MonsterJson[]? Monsters, FormulaJson? WoundFormula);
file sealed record WeaponJson(string Id, string Name, int Speed, int Accuracy, int Strength);
file sealed record MonsterJson(string Id, string Name, LevelJson[]? Levels);
file sealed record LevelJson(string Id, string Name, StatsJson Stats, int WoundThreshold, string HitLocationDeckRef);
file sealed record StatsJson(int Movement, int Toughness, int Speed, int Accuracy, int Damage, int Luck, int Evasion);
file sealed record FormulaJson(string? Kind, int? Min, int? Max);
file sealed record HostDeckFile(int SchemaVersion, string DeckId, HostCardJson[]? Cards);
file sealed record HostCardJson(string Id, string Label, bool HasCriticalSlot, bool IsTrap, int Copies);

/// <summary>
/// Loads mechanical content packs (committed, content/packs/) and host-provided hit-location
/// decks (gitignored, content/local/) into engine <see cref="IContentPack"/>s at startup.
/// Never loads card art or effect prose.
/// </summary>
public sealed class ContentService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IContentPack> _packs = new(StringComparer.Ordinal);
    private readonly ILogger<ContentService> _log;

    public ContentService(IHostEnvironment env, IConfiguration config, ILogger<ContentService> log)
    {
        _log = log;
        var root = ResolveContentRoot(env, config);
        if (root is null)
        {
            _log.LogWarning("No content directory found; no packs loaded.");
            return;
        }

        var hostDecks = LoadHostDecks(Path.Combine(root, "local"));
        LoadPacks(Path.Combine(root, "packs"), hostDecks);
        _log.LogInformation("Loaded {PackCount} content pack(s), {DeckCount} host deck(s) from {Root}.",
            _packs.Count, hostDecks.Count, root);
    }

    public IContentPack? GetPack(string packId) => _packs.GetValueOrDefault(packId);
    public IReadOnlyCollection<string> PackIds => _packs.Keys;

    private Dictionary<string, IReadOnlyList<HitLocCardDef>> LoadHostDecks(string dir)
    {
        var decks = new Dictionary<string, IReadOnlyList<HitLocCardDef>>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return decks;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var d = JsonSerializer.Deserialize<HostDeckFile>(File.ReadAllText(file), Json);
                if (d?.DeckId is null || d.Cards is null) continue;
                var cards = d.Cards.Select(c => new HitLocCardDef(c.Id, c.Label, c.HasCriticalSlot, c.IsTrap, Math.Max(1, c.Copies))).ToList();
                decks[$"host:{d.DeckId}"] = cards;
            }
            catch (Exception ex) { _log.LogError(ex, "Failed to load host deck {File}", file); }
        }
        return decks;
    }

    private void LoadPacks(string dir, Dictionary<string, IReadOnlyList<HitLocCardDef>> hostDecks)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<PackFile>(File.ReadAllText(file), Json);
                if (p?.PackId is null) continue;

                var gear = (p.Weapons ?? []).ToDictionary(
                    w => w.Id, w => new GearDef(w.Id, w.Name, w.Speed, w.Accuracy, w.Strength));

                var monsters = (p.Monsters ?? []).ToDictionary(
                    m => m.Id,
                    m => new MonsterDef(m.Id, m.Name, (m.Levels ?? []).ToDictionary(
                        l => l.Id,
                        l => new LevelDef(
                            l.Id,
                            new MonsterStats(l.Stats.Movement, l.Stats.Toughness, l.Stats.Speed, l.Stats.Accuracy, l.Stats.Damage, l.Stats.Luck, l.Stats.Evasion),
                            l.WoundThreshold,
                            l.HitLocationDeckRef))));

                var formula = new FormulaConfig(WoundMin: p.WoundFormula?.Min ?? 2, WoundMax: p.WoundFormula?.Max ?? 10);
                _packs[p.PackId] = new ContentPack(p.PackId, formula, monsters, gear, hostDecks);
            }
            catch (Exception ex) { _log.LogError(ex, "Failed to load content pack {File}", file); }
        }
    }

    // Search the config path, then walk up from the content root for a "content" dir (robust to cwd).
    private static string? ResolveContentRoot(IHostEnvironment env, IConfiguration config)
    {
        var configured = config["Content:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;

        var dir = new DirectoryInfo(env.ContentRootPath);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "content");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
