using System.Text.RegularExpressions;

namespace THOBOTTO.Tests;

// The shape of src: a folder is a namespace; the surfaces (the root, the panel's pages, the Activity) use anything;
// platform folders use only the platform (three catalogues name the features); a feature never uses a surface;
// features form no cycle.
public partial class StructureTests
{
    private static readonly string[] Surfaces = ["root", "Commands", "Panel/Pages", "Activity"];
    private static readonly string[] Platform =
        ["Access", "Certificates", "Data", "Debugging", "Discord", "Helpers", "Integrations", "Members", "Modules", "Notifications", "Panel", "Voice/Relay"];
    private static readonly string[] Catalogues = ["Data/BotDbContext.cs", "Modules/ModuleRegistry.cs", "Modules/SettingsPages.cs"];
    private static readonly string[] Shells = ["SetupCommands", "MeCommands", "ModCommands"];

    private static readonly Lazy<IReadOnlyList<SourceFile>> Files = new(Load);

    [Fact]
    public void Every_file_is_in_its_folders_namespace()
    {
        var offenders = new List<string>();
        foreach (var file in Files.Value.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var expected = file.Folder.Length == 0 ? "THOBOTTO" : "THOBOTTO." + file.Folder.Replace('/', '.');
            if (file.Namespace is null ? file.Folder.Length == 0 : file.Namespace == expected)
                continue;
            // The command shells' pieces sit in the folder whose commands they hold, as parts of the shell's class.
            if (IsShellPiece(file.Namespace, file.Types))
                continue;
            offenders.Add($"{file.Path}:{file.NamespaceLine} → {file.Namespace ?? "(none)"}");
        }
        Assert.True(offenders.Count == 0, "Files outside their folder's namespace:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Platform_folders_use_only_the_platform()
    {
        var offenders = Files.Value
            .Where(f => Kind(f.Node) == "platform" && !Catalogues.Contains(f.Path))
            .SelectMany(f => f.References)
            .Where(r => Kind(r.Target) != "platform")
            .Select(r => r.ToString())
            .ToList();
        Assert.True(offenders.Count == 0, "Platform folders naming a feature or a surface:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Features_never_use_a_surface()
    {
        var offenders = Files.Value
            .Where(f => Kind(f.Node) == "feature")
            .SelectMany(f => f.References)
            .Where(r => Kind(r.Target) == "surface")
            .Select(r => r.ToString())
            .ToList();
        Assert.True(offenders.Count == 0, "Features naming a surface:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Features_form_no_cycle()
    {
        var features = Files.Value.Select(f => f.Node).Where(n => Kind(n) == "feature").Distinct().ToList();
        var edges = Files.Value
            .Where(f => Kind(f.Node) == "feature")
            .SelectMany(f => f.References.Where(r => Kind(r.Target) == "feature").Select(r => (From: f.Node, r)))
            .ToList();
        var graph = features.ToDictionary(n => n, n => edges.Where(e => e.From == n).Select(e => e.r.Target).ToHashSet());

        var cycles = new List<string>();
        foreach (var component in StronglyConnected(features, graph).Where(c => c.Count > 1))
        {
            var inside = edges.Where(e => component.Contains(e.From) && component.Contains(e.r.Target)).Select(e => "  " + e.r);
            cycles.Add($"{string.Join(", ", component.Order())}:\n{string.Join('\n', inside)}");
        }
        Assert.True(cycles.Count == 0, "Cycles among the features:\n" + string.Join('\n', cycles));
    }

    private static string Kind(string node) => Surfaces.Contains(node) ? "surface" : Platform.Contains(node) ? "platform" : "feature";

    // The namespace's folder: THOBOTTO.Voice.Hearing is Voice/Hearing, THOBOTTO.Panel.Layout is Panel's, THOBOTTO the root.
    private static string NodeOf(string ns)
    {
        var rest = ns == "THOBOTTO" ? "" : ns["THOBOTTO.".Length..];
        return rest.Length == 0 ? "root" : rest == "Panel.Layout" ? "Panel" : rest.Replace('.', '/');
    }

    private static string NodeOfFolder(string folder) => folder.Length == 0 ? "root" : folder == "Panel/Layout" ? "Panel" : folder;

    private static IReadOnlyList<SourceFile> Load()
    {
        var src = Source();
        var paths = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".razor", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(src, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("Data/Migrations/", StringComparison.Ordinal) && !p.StartsWith("wwwroot/", StringComparison.Ordinal) && !p.Split('/').Any(part => part is "bin" or "obj"))
            .Order()
            .ToList();
        var declared = new Dictionary<string, List<(string Name, string Path)>>();
        var parsed = new List<(string Path, string Folder, string? Namespace, int NamespaceLine, List<TypeDeclaration> Types, List<Reference> References, string Code)>();
        foreach (var path in paths)
        {
            var code = Literal().Replace(File.ReadAllText(Path.Combine(src, path)), m => new string('\n', m.Value.Count(c => c == '\n')) + " ");
            var folder = Path.GetDirectoryName(path)!.Replace('\\', '/');
            var ns = NamespaceOf(code);
            var types = path.EndsWith(".cs", StringComparison.Ordinal) ? TypesOf(code) : [];
            var references = ReferencesOf(path, code, NodeOfFolder(folder));
            parsed.Add((path, folder, ns?.Name, ns?.Line ?? 1, types, references, code));
            var names = declared.GetValueOrDefault(folder) ?? (declared[folder] = []);
            names.AddRange(types.Select(t => (t.Name, path)));
            if (path.EndsWith(".razor", StringComparison.Ordinal))
                names.Add((Path.GetFileNameWithoutExtension(path), path));
        }

        // A nested folder sees its parent namespace's types without a using, and every folder the root's: a reference
        // where one is used, a shell's pieces being the shell's own. What _Imports.razor brings in reaches every page.
        var rootTypes = declared.GetValueOrDefault("", []).Where(t => t.Path.EndsWith(".cs", StringComparison.Ordinal)).ToList();
        var imports = parsed.Where(f => f.Path == "_Imports.razor").SelectMany(f => f.References).ToList();
        var files = new List<SourceFile>();
        foreach (var (path, folder, ns, nsLine, types, references, code) in parsed)
        {
            var node = NodeOfFolder(folder);
            if (node.Contains('/') && node != "Panel/Pages" && declared.TryGetValue(node[..node.IndexOf('/')], out var parentTypes)
                && Use(path, code, "THOBOTTO." + node[..node.IndexOf('/')], parentTypes) is { } parent)
                references.Add(parent);
            if (node != "root" && !IsShellPiece(ns, types) && Use(path, code, "THOBOTTO", rootTypes) is { } root)
                references.Add(root);
            if (path.EndsWith(".razor", StringComparison.Ordinal) && path != "_Imports.razor")
                references.AddRange(imports.Where(i => i.Target != node).Select(i => new Reference($"{path} via {i.File}", i.Line, i.Namespace)));
            files.Add(new(path, folder, node, ns, nsLine, types, references));
        }
        return files;
    }

    // A command shell's piece: a file in the shells' namespace holding parts of the shells alone.
    private static bool IsShellPiece(string? ns, IReadOnlyList<TypeDeclaration> types)
        => ns == "THOBOTTO.Commands" && types.Count > 0 && types.All(t => t.Partial && t.Kind == "class" && Shells.Contains(t.Name));

    // The first place a file names one of a folder's types, as a reference to the folder's namespace.
    private static Reference? Use(string path, string code, string ns, IEnumerable<(string Name, string Path)> types)
        => types.Select(t => Regex.Match(code, $@"\b{Regex.Escape(t.Name)}\b")).FirstOrDefault(m => m.Success) is { } used
            ? new(path, LineOf(code, used.Index), ns)
            : null;

    // The bot's folder, from the solution the test assembly was built under.
    private static string Source()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "THOBOTTO.slnx")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder!.FullName, "src", "THOBOTTO");
    }

    private static (string Name, int Line)? NamespaceOf(string code)
        => Namespace().Match(code) is { Success: true } m ? (m.Groups[1].Value, LineOf(code, m.Index)) : null;

    // Type declarations outside any braces (the file-scoped namespace's own).
    private static List<TypeDeclaration> TypesOf(string code)
    {
        var types = new List<TypeDeclaration>();
        var depth = 0;
        var at = 0;
        foreach (var m in Type().Matches(code).Cast<Match>())
        {
            for (; at < m.Index; at++)
                depth += code[at] == '{' ? 1 : code[at] == '}' ? -1 : 0;
            // A constraint's "class" or "struct" is followed by a keyword, never a type's name.
            if (depth == 0 && m.Groups[3].Value is not ("where" or "new" or "in" or "is" or "as" or "or" or "and" or "not"))
                types.Add(new(m.Groups[1].Success, m.Groups[2].Value.Split(' ')[^1], m.Groups[3].Value));
        }
        return types;
    }

    private static List<Reference> ReferencesOf(string path, string code, string own)
    {
        var references = new List<Reference>();
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Using().Match(line) is { Success: true } u)
            {
                references.Add(new(path, i + 1, u.Groups[1].Value));
                continue;
            }
            if (line.TrimStart().StartsWith("namespace ", StringComparison.Ordinal))
                continue;
            foreach (var m in Qualified().Matches(line).Cast<Match>())
            {
                var parts = m.Groups[1].Value.Split('.');
                references.Add(new(path, i + 1, parts.Length > 1 ? "THOBOTTO." + string.Join('.', parts[..^1]) : "THOBOTTO"));
            }
        }
        return references.Where(r => r.Target != own).DistinctBy(r => (r.Line, r.Namespace)).ToList();
    }

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    // Tarjan's components, each a set of nodes reaching one another.
    private static List<HashSet<string>> StronglyConnected(List<string> nodes, Dictionary<string, HashSet<string>> graph)
    {
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var components = new List<HashSet<string>>();
        foreach (var node in nodes.Order())
        {
            if (!index.ContainsKey(node))
                Visit(node);
        }
        return components;

        void Visit(string node)
        {
            index[node] = low[node] = index.Count;
            stack.Push(node);
            onStack.Add(node);
            foreach (var next in graph[node].Order())
            {
                if (!index.TryGetValue(next, out var seen))
                {
                    Visit(next);
                    low[node] = Math.Min(low[node], low[next]);
                }
                else if (onStack.Contains(next))
                    low[node] = Math.Min(low[node], seen);
            }
            if (low[node] != index[node])
                return;
            var component = new HashSet<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            }
            while (popped != node);
            components.Add(component);
        }
    }

    private sealed record Reference(string File, int Line, string Namespace)
    {
        public string Target => NodeOf(Namespace);

        public override string ToString() => $"{File}:{Line} → {Namespace}";
    }

    private sealed record TypeDeclaration(bool Partial, string Kind, string Name);

    private sealed record SourceFile(string Path, string Folder, string Node, string? Namespace, int NamespaceLine, IReadOnlyList<TypeDeclaration> Types, IReadOnlyList<Reference> References);

    // String and character literals and comments, C# and Razor, so that nothing in them counts as code.
    [GeneratedRegex(""""
        """[\s\S]*?"""|@\$?"(?:[^"]|"")*"|\$@"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])'|/\*[\s\S]*?\*/|//[^\n]*|@\*[\s\S]*?\*@
        """", RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Literal();

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)\s*[;{]", RegexOptions.Multiline)]
    private static partial Regex Namespace();

    [GeneratedRegex(@"\b(partial\s+)?(record\s+class|record\s+struct|class|record|interface|enum|struct)\s+(\w+)")]
    private static partial Regex Type();

    [GeneratedRegex(@"^\s*@?using\s+(THOBOTTO(?:\.\w+)*)\s*;?\s*$")]
    private static partial Regex Using();

    [GeneratedRegex(@"\bTHOBOTTO((?:\.\w+)+)")]
    private static partial Regex Qualified();
}
