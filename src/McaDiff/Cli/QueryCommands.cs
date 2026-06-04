using System.Text.Json;
using System.Text.Json.Serialization;
using McaDiff.Query;
using McaDiff.Repo;

namespace McaDiff.Cli;

/// <summary>World-state inspection commands (read-only): <c>inspect</c> a coordinate, <c>find</c>
/// entities / block entities. They never modify the world. Exit codes: 0 = found, 1 = no
/// matches / not present, 2 = error.</summary>
public static class QueryCommands
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Inspect(string? dashC, string[] a)
    {
        var (pos, opts) = ArgParser.Parse(a, ["--dim"], ["--json"]);
        if (pos.Count < 3) return Err("usage: inspect <x> <y> <z> [<world>] [--dim D] [--json]");
        if (!int.TryParse(pos[0], out int x) || !int.TryParse(pos[1], out int y) || !int.TryParse(pos[2], out int z))
            return Err("inspect: x y z must be integers");
        if (ResolveWorld(dashC, pos, 3) is not { } world) return NoWorld();

        BlockInspect? r = new WorldQuery(world, opts.GetValueOrDefault("--dim") ?? "").BlockAt(x, y, z);
        if (r is null) { Console.Error.WriteLine($"({x},{y},{z}): no chunk/section there (ungenerated?)"); return 1; }
        if (opts.ContainsKey("--json")) Console.WriteLine(JsonSerializer.Serialize(r, Json));
        else Console.WriteLine($"({r.X},{r.Y},{r.Z}) [{r.Dimension}]: {r.Block}" + (r.Biome is null ? "" : $"  (biome {r.Biome})"));
        return 0;
    }

    public static int Players(string? dashC, string[] a)
    {
        var (pos, opts) = ArgParser.Parse(a, [], ["--json"]);
        if (ResolveWorld(dashC, pos, 0) is not { } world) return NoWorld();
        var players = new WorldQuery(world).Players().ToList();
        if (opts.ContainsKey("--json")) Console.WriteLine(JsonSerializer.Serialize(players, Json));
        else foreach (PlayerHit p in players)
            Console.WriteLine($"{p.Source}  ({p.X:0.#},{p.Y:0.#},{p.Z:0.#}) [{p.Dimension}]"
                + (p.Health >= 0 ? $"  {p.Health:0.#} hp" : "") + $"  gamemode {p.GameMode}");
        return Report(players.Count, "player(s)");
    }

    public static int Poi(string? dashC, string[] a)
    {
        var (pos, opts) = ArgParser.Parse(a, ["--type", "--near", "--radius", "--dim"], ["--json"]);
        if (ResolveWorld(dashC, pos, 0) is not { } world) return NoWorld();
        (int, int, int)? near = ParseNear(opts.GetValueOrDefault("--near"), out string? e);
        if (e is not null) return Err(e);
        int radius = int.TryParse(opts.GetValueOrDefault("--radius"), out int rr) ? rr : 64;
        var hits = new WorldQuery(world, opts.GetValueOrDefault("--dim") ?? "")
            .Poi(opts.GetValueOrDefault("--type"), near, radius).ToList();
        if (opts.ContainsKey("--json")) Console.WriteLine(JsonSerializer.Serialize(hits, Json));
        else foreach (PoiHit h in hits) Console.WriteLine($"{h.Type}  ({h.X},{h.Y},{h.Z})  [{h.Region}]");
        return Report(hits.Count, "point(s) of interest");
    }

    public static int Find(string? dashC, string[] a)
    {
        var (pos, opts) = ArgParser.Parse(a, ["--near", "--radius", "--dim", "--text"], ["--json"]);
        if (pos.Count < 1) return Err("usage: find <entity|block-entity|sign> ... [<world>] [--near x,y,z] [--radius N] [--dim D] [--json]");
        string kind = pos[0];
        // sign matches by --text; entity/block-entity match by a positional <id>.
        bool isSign = kind is "sign" or "signs";
        string id = isSign ? "" : pos.Count > 1 ? pos[1] : "";
        if (!isSign && pos.Count < 2) return Err("usage: find <entity|block-entity> <id> [<world>] ...");
        if (ResolveWorld(dashC, pos, isSign ? 1 : 2) is not { } world) return NoWorld();

        (int, int, int)? near = ParseNear(opts.GetValueOrDefault("--near"), out string? nearErr);
        if (nearErr is not null) return Err(nearErr);
        int radius = int.TryParse(opts.GetValueOrDefault("--radius"), out int rr) ? rr : 64;
        var q = new WorldQuery(world, opts.GetValueOrDefault("--dim") ?? "");
        bool json = opts.ContainsKey("--json");

        switch (kind)
        {
            case "block-entity" or "block_entity" or "be":
                {
                    var hits = q.BlockEntities(id, near, radius).ToList();
                    if (json) Console.WriteLine(JsonSerializer.Serialize(hits, Json));
                    else foreach (BlockEntityHit h in hits)
                        Console.WriteLine($"{h.Id}  ({h.X},{h.Y},{h.Z})" + (h.ItemCount > 0 ? $"  {h.ItemCount} item(s)" : "") + $"  [{h.Region}]");
                    return Report(hits.Count, $"block entities matching '{id}'");
                }
            case "entity" or "e":
                {
                    var hits = q.Entities(id, near, radius).ToList();
                    if (json) Console.WriteLine(JsonSerializer.Serialize(hits, Json));
                    else foreach (EntityHit h in hits)
                        Console.WriteLine($"{h.Id}  ({h.X:0.#},{h.Y:0.#},{h.Z:0.#})" + (h.CustomName is null ? "" : $"  \"{h.CustomName}\"") + $"  [{h.Region}]");
                    return Report(hits.Count, $"entities matching '{id}'");
                }
            case "sign" or "signs":
                {
                    var hits = q.Signs(opts.GetValueOrDefault("--text"), near, radius).ToList();
                    if (json) Console.WriteLine(JsonSerializer.Serialize(hits, Json));
                    else foreach (SignHit h in hits)
                        Console.WriteLine($"({h.X},{h.Y},{h.Z})  \"{string.Join(" / ", h.Lines)}\"  [{h.Region}]");
                    return Report(hits.Count, opts.GetValueOrDefault("--text") is { } p ? $"signs containing '{p}'" : "signs");
                }
            default:
                return Err($"unknown find kind '{kind}' (use: entity | block-entity | sign)");
        }
    }

    // ---- helpers ----

    private static string? ResolveWorld(string? dashC, List<string> pos, int worldIndex)
    {
        if (pos.Count > worldIndex) return pos[worldIndex];
        return Repository.Discover(dashC)?.Worktree;
    }

    private static (int, int, int)? ParseNear(string? s, out string? error)
    {
        error = null;
        if (s is null) return null;
        string[] parts = s.Split(',');
        if (parts.Length == 3 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y) && int.TryParse(parts[2], out int z))
            return (x, y, z);
        error = $"--near must be x,y,z (got '{s}')";
        return null;
    }

    private static int Report(int count, string what)
    {
        Console.Error.WriteLine(count == 0 ? $"No {what}." : $"{count} {what}.");
        return count == 0 ? 1 : 0;
    }

    private static int NoWorld() => Err("no world given and no worktree bound (pass <world> or run inside a repo)");

    private static int Err(string message)
    {
        Console.Error.WriteLine($"mcadiff: {message}");
        return 2;
    }
}
