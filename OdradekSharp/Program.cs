using System.Diagnostics;
using OdradekSharp.Ds2;
using OdradekSharp.Export;
using OdradekSharp.Rtti;

namespace OdradekSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            if (Environment.GetEnvironmentVariable("ODRADEKSHARP_TRACE") == "1")
                Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                odradeksharp - Decima / Death Stranding 2 asset reader (C# port of odradek)

                usage:
                  odradeksharp info    <gameDir>                    game + graph summary
                  odradeksharp groups  <gameDir> [--limit N]        list streaming-graph groups
                  odradeksharp types   <gameDir> [--filter S]       type names present in the graph
                  odradeksharp find    <gameDir> <TypeName> [--limit N] [--all]
                  odradeksharp read    <gameDir> <group>:<index>    dump one object
                  odradeksharp dump    <gameDir> <TypeName> --out <dir> [--limit N]
                  odradeksharp rtti    [--types <types.json>]       load types.json and print statistics

                --all       include derived types when matching (default: true)
                --subgroups read child groups while reading a group (default: true)
                """);
            return 0;
        }

        var cmd = args[0];
        var rest = args.Skip(1).ToArray();
        switch (cmd)
        {
            case "rtti": return Rtti(rest);
            case "info": return Info(rest);
            case "groups": return Groups(rest);
            case "types": return Types(rest);
            case "find": return Find(rest);
            case "group": return GroupInfo(rest);
            case "hex": return Hex(rest);
            case "trace": return TraceGroup(rest);
            case "read": return Read(rest);
            case "dump": return Dump(rest);
            default:
                Console.Error.WriteLine($"unknown command: {cmd}");
                return 2;
        }
    }

    private static string? Opt(string[] a, string name)
    {
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i] == name) return a[i + 1];
        return null;
    }

    private static bool Flag(string[] a, string name) => a.Contains(name);

    private static int OptInt(string[] a, string name, int fallback) =>
        int.TryParse(Opt(a, name), out var v) ? v : fallback;

    private static int Rtti(string[] a)
    {
        var path = Opt(a, "--types") ?? Path.Combine(AppContext.BaseDirectory, "Data", "types.json");
        using var factory = TypeFactory.Load(path, Path.Combine(Path.GetDirectoryName(path)!, "extensions.json"));
        var errors = new List<string>();
        foreach (var name in factory.KnownNames)
        {
            try { factory.Resolve(name); }
            catch (Exception e) { errors.Add($"{name}: {e.Message}"); }
        }
        Console.WriteLine($"definitions: {factory.KnownNames.Count}  resolved: {factory.Types.Count}  errors: {errors.Count}");
        foreach (var g in factory.Types.Values.GroupBy(t => t.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-10} {g.Count()}");
        return errors.Count == 0 ? 0 : 1;
    }

    private static DecimaGame OpenGame(string[] a, out int consumed)
    {
        consumed = 1;
        if (a.Length < 1) throw new ArgumentException("missing <gameDir>");
        return DecimaGame.Open(a[0], Opt(a, "--types"), Opt(a, "--extensions"));
    }

    private static int Info(string[] a)
    {
        var sw = Stopwatch.StartNew();
        using var game = OpenGame(a, out _);
        Console.WriteLine($"game root      : {game.Root}");
        Console.WriteLine($"graph file     : {game.StreamingGraphPath}");
        Console.WriteLine($"load time      : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"types in graph : {game.Graph.TypeTable.Count}");
        Console.WriteLine($"groups         : {game.Graph.Groups.Count}");
        Console.WriteLine($"files          : {game.Graph.Files.Count}");
        Console.WriteLine($"link table     : {game.Graph.LinkTable.Length} bytes");
        var roots = game.Graph.Groups.Sum(g => g.Roots.Count);
        Console.WriteLine($"root objects   : {roots}");
        Console.WriteLine();
        Console.WriteLine("first groups:");
        foreach (var g in game.Graph.Groups.OrderBy(g => g.Id).Take(10))
            Console.WriteLine($"  {g.Id,8}  objects={g.Types.Count,7}  spans={g.Spans.Count,4}  subgroups={g.SubGroups.Count,3}  linkStart={g.LinkStart}");
        return 0;
    }

    private static int Groups(string[] a)
    {
        using var game = OpenGame(a, out _);
        var limit = OptInt(a, "--limit", int.MaxValue);
        var byId = Flag(a, "--by-id");
        var groups = byId ? game.Graph.Groups.OrderBy(g => g.Id) : game.Graph.Groups.AsEnumerable();
        var count = 0;
        foreach (var g in groups)
        {
            if (count++ >= limit) break;
            var typeNames = g.Types.Select(t => t.Name).Distinct().Take(3);
            Console.WriteLine($"{g.Id,8}  objects={g.Types.Count,7}  spans={g.Spans.Count,4}  " +
                              $"locators={g.Locators.Count,6}  subgroups={g.SubGroups.Count,3}  [{string.Join(", ", typeNames)}]");
        }
        Console.WriteLine($"total groups: {game.Graph.Groups.Count}");
        return 0;
    }

    private static int Types(string[] a)
    {
        using var game = OpenGame(a, out _);
        var filter = Opt(a, "--filter");
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in game.Graph.Groups)
            foreach (var type in group.Types)
                stats[type.Name] = stats.GetValueOrDefault(type.Name) + 1;
        foreach (var (name, count) in stats
                     .Where(kv => filter is null || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(kv => kv.Value))
            Console.WriteLine($"{count,9}  {name}");
        Console.WriteLine($"distinct types: {stats.Count}");
        return 0;
    }

    private static int Find(string[] a)
    {
        if (a.Length < 2) throw new ArgumentException("usage: find <gameDir> <TypeName>");
        using var game = OpenGame(a, out _);
        var typeName = a[1];
        var limit = OptInt(a, "--limit", 20);
        var includeDerived = !Flag(a, "--exact");
        var total = 0;
        foreach (var (id, type) in game.FindObjects(typeName, includeDerived))
        {
            total++;
            if (total <= limit) Console.WriteLine($"{id,-14} {type.Name}");
        }
        Console.WriteLine($"total {typeName}: {total}" + (total > limit ? $" (showing first {limit})" : ""));
        return 0;
    }

    /// <summary>trace &lt;gameDir&gt; &lt;groupId&gt; [index] — per-object byte ranges inside the group's spans.</summary>
    private static int TraceGroup(string[] a)
    {
        using var game = OpenGame(a, out _);
        var id = int.Parse(a[1]);
        game.Objects.TraceEnabled = true;
        try
        {
            game.ReadGroup(id, !Flag(a, "--no-subgroups"));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("read aborted: " + e.Message);
        }
        foreach (var (index, type, start, end, file, spanLength) in game.Objects.Trace)
            Console.WriteLine($"[{index,5}] {type,-42} {start,10}..{end,-10} size={end - start,-8} span={spanLength} {file}");
        return 0;
    }

    /// <summary>Dump raw bytes of a mounted game file: hex &lt;gameDir&gt; &lt;devicePath&gt; &lt;offset&gt; &lt;length&gt;</summary>
    private static int Hex(string[] a)
    {
        using var game = OpenGame(a, out _);
        var file = a[1];
        var offset = long.Parse(a[2]);
        var length = int.Parse(a[3]);
        using var data = Io.DataFile.Open(StreamingGraph.ResolveGamePath(game.Root, file));
        var bytes = data.Read(offset, length);
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var line = string.Join(' ', bytes.Skip(i).Take(16).Select(b => b.ToString("x2")));
            Console.WriteLine($"{offset + i:x8}  {line}");
        }
        return 0;
    }

    /// <summary>Metadata-only listing of one group: object index, type and span layout.</summary>
    private static int GroupInfo(string[] a)
    {
        if (a.Length < 2) throw new ArgumentException("usage: group <gameDir> <groupId>");
        using var game = OpenGame(a, out _);
        var id = int.Parse(a[1]);
        var group = game.Graph.GetGroup(id);
        Console.WriteLine($"group {id}: objects={group.Types.Count} spans={group.Spans.Count} " +
                          $"locators={group.Locators.Count} subgroups={group.SubGroups.Count} linkStart={group.LinkStart}");
        foreach (var span in group.Spans)
            Console.WriteLine($"  span file={game.Graph.Files[span.FileIndex]} offset={span.Offset} length={span.Length}");
        for (var i = 0; i < group.Types.Count; i++)
        {
            var t = group.Types[i];
            var msg = t.Messages.Count > 0 ? " messages=" + string.Join("|", t.Messages) : "";
            Console.WriteLine($"  [{i}] {t.Name}{msg}");
        }
        return 0;
    }

    private static int Read(string[] a)    {
        if (a.Length < 2) throw new ArgumentException("usage: read <gameDir> <group>:<index>");
        using var game = OpenGame(a, out _);
        var id = ObjectId.Parse(a[1]);
        var type = game.ObjectType(id);
        Console.WriteLine($"object  : {id}");
        Console.WriteLine($"type    : {type.Name} (version {type.Version})");
        var obj = game.ReadObject(id, !Flag(a, "--no-subgroups"));
        if (Flag(a, "--json"))
        {
            var json = JsonExporter.Export(obj);
            if (Opt(a, "--out") is { } outPath) { File.WriteAllText(outPath, json); Console.WriteLine($"wrote {outPath}"); }
            else Console.WriteLine(json);
        }
        else
        {
            foreach (var attr in type.SerializedAttrs)
            {
                if (!obj.Fields.TryGetValue(attr.Name, out var v)) continue;
                Console.WriteLine($"  {attr.Name,-32} {Describe(v)}");
            }
        }
        return 0;
    }

    private static int Dump(string[] a)
    {
        if (a.Length < 2) throw new ArgumentException("usage: dump <gameDir> <TypeName> --out <dir>");
        using var game = OpenGame(a, out _);
        var typeName = a[1];
        var outDir = Opt(a, "--out") ?? throw new ArgumentException("--out <dir> is required");
        var limit = OptInt(a, "--limit", int.MaxValue);
        var byGroup = Flag(a, "--by-group");
        Directory.CreateDirectory(outDir);

        var written = 0;
        foreach (var (id, _) in game.FindObjects(typeName, !Flag(a, "--exact")))
        {
            if (written >= limit) break;
            try
            {
                var obj = game.ReadObject(id, !Flag(a, "--no-subgroups"));
                var path = Path.Combine(outDir, $"{obj.Type.Name}_{id.GroupId}_{id.ObjectIndex}.json");
                File.WriteAllText(path, JsonExporter.Export(obj));
                written++;
                if (written % 100 == 0) Console.WriteLine($"  {written} written...");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  {id} failed: {e.Message}");
            }
        }
        Console.WriteLine($"exported {written} object(s) to {outDir}");
        return 0;
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        TypedObject t => t.Type.Name,
        ObjectRef r => r.ToString()!,
        UuidRef g => g.ToGuid().ToString("D"),
        EnumValue e => e.ToString(),
        string s => "\"" + (s.Length > 60 ? s[..60] + "..." : s) + "\"",
        IReadOnlyList<object?> list => $"[{list.Count}]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
    };
}
