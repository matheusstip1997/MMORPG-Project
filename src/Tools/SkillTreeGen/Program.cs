// Generates the skill-tree tables from the design file (spec 3.5 Implementation, WP-M2-5):
//   data/tables/SkillTree.json and SkillEvolution.json are written whole from docs/design/skill-trees.json;
//   missing class rows (ClassDef, ClassGearDef) and UiStrings names (class.<id>, skillNode.<id>) are appended, existing
//   rows are never edited, so hand-written rows and comments stay.
// Usage: dotnet run --project src/Tools/SkillTreeGen -- [--design <file>] [--data <dir>] [--max-tier <n>] [--check]
// --check writes nothing and fails when the tables differ from what the design file gives (CI and the round-trip test).
// --max-tier is the highest class tier generated (1 in M2: the roots and the eight tier-1 classes, user decision 2026-10-27).
// Exit codes: 0 up to date (or written), 1 differences or problems, 2 bad arguments or missing files.

using SkillTreeGen;

var designPath = Path.Combine("docs", "design", "skill-trees.json");
var dataDirectory = Path.Combine("data", "tables");
var maxTier = 1;
var check = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--design" when i + 1 < args.Length:
            designPath = args[++i];
            break;
        case "--data" when i + 1 < args.Length:
            dataDirectory = args[++i];
            break;
        case "--max-tier" when i + 1 < args.Length && int.TryParse(args[i + 1], out var tier):
            maxTier = tier;
            i++;
            break;
        case "--check":
            check = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: SkillTreeGen [--design <file>] [--data <dir>] [--max-tier <n>] [--check]");
            return 2;
    }
}

if (!File.Exists(designPath) || !Directory.Exists(dataDirectory))
{
    Console.Error.WriteLine($"Design file or data directory not found: {Path.GetFullPath(designPath)}, {Path.GetFullPath(dataDirectory)}");
    return 2;
}

var files = new TableFiles(dataDirectory);
var result = SkillTreeGenerator.Generate(File.ReadAllText(designPath), files, maxTier);
foreach (var problem in result.Problems)
    Console.WriteLine($"ERROR {problem}");

var differences = result.Differences(files);
if (check)
{
    foreach (var difference in differences)
        Console.WriteLine($"DIFF {difference}");
    Console.WriteLine(differences.Count == 0 && result.Problems.Count == 0
        ? "OK: the skill-tree tables match the design file"
        : $"FAILED: {differences.Count} difference(s), {result.Problems.Count} problem(s); run SkillTreeGen without --check");
    return differences.Count == 0 && result.Problems.Count == 0 ? 0 : 1;
}

result.Write(files);
foreach (var difference in differences)
    Console.WriteLine($"WROTE {difference}");
Console.WriteLine(result.Problems.Count == 0
    ? $"OK: {result.NodeCount} node(s) of {result.ClassCount} class(es) written to {Path.GetFullPath(dataDirectory)}"
    : $"FAILED: {result.Problems.Count} problem(s) need a hand edit");
return result.Problems.Count == 0 ? 0 : 1;
