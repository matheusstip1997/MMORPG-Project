using System.Text.Json.Nodes;
using GameServer.Host.Config;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using SkillTreeGen;

namespace GameServer.Tests.Tools;

/// <summary>
/// The skill-tree generator (spec 3.5 Implementation, WP-M2-5): <c>docs/design/skill-trees.json</c> to <c>SkillTree</c>,
/// <c>SkillEvolution</c>, the tier-1 class rows and the names. The committed tables are what it writes (the round trip,
/// also <c>SkillTreeGen --check</c>); a design change shows up; appending never touches hand-written rows.
/// </summary>
public class SkillTreeGenTests
{
    private static string DesignPath() => Path.Combine(RepositoryPaths.Root(), "docs", "design", "skill-trees.json");

    [Fact]
    public void The_committed_tables_are_what_the_design_file_generates()
    {
        var files = new TableFiles(RepositoryPaths.Tables());
        var result = SkillTreeGenerator.Generate(File.ReadAllText(DesignPath()), files, maxTier: 1);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Differences(files));
        Assert.Equal((49, 12), (result.NodeCount, result.ClassCount));
    }

    [Fact]
    public void Generated_rows_load_back_as_the_design_file_says()
    {
        var set = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All).Set;
        var design = JsonNode.Parse(File.ReadAllText(DesignPath()))!;
        var shipped = design["classes"]!.AsArray().Where(c => (int)c!["tier"]! <= 1).Select(c => (string)c!["id"]!).ToHashSet();
        var skills = design["skills"]!.AsArray().Where(s => shipped.Contains((string)s!["classId"]!)).ToList();
        Assert.Equal(skills.Count, SkillTree.Rows(set).Count);
        foreach (var skill in skills)
        {
            var node = SkillTree.Find(set, (string)skill!["id"]!)!;
            Assert.Equal((string)skill["classId"]!, node.ClassId);
            Assert.Equal((string)skill["evolvesFrom"]!, node.EvolvesFrom);
            Assert.Equal((int)skill["row"]!, node.Row);
            Assert.Equal(skill["requires"]!.AsArray().Select(r => (string)r!["skill"]!), node.Requires);
            Assert.Equal(skill["requires"]!.AsArray().Select(r => (int)r!["level"]!), node.RequiresLevels);
        }
        foreach (var cls in design["classes"]!.AsArray().Where(c => (int)c!["tier"]! == 1))
        {
            var row = ClassDef.Find(set, (string)cls!["id"]!)!;
            Assert.Equal(((string)cls["parent"]!, 1, (int)cls["unlockLevel"]!), (row.Parent, row.Tier, row.UnlockLevel));
        }
    }

    [Fact]
    public void A_changed_design_file_is_a_difference_and_writing_it_round_trips()
    {
        using var temp = CopyTables();
        var files = new TableFiles(temp.Path);
        var design = JsonNode.Parse(File.ReadAllText(DesignPath()))!;
        var slash = design["skills"]!.AsArray().First(s => (string)s!["id"]! == "warrior.slash")!;
        slash["row"] = 3;
        var result = SkillTreeGenerator.Generate(design.ToJsonString(), files, maxTier: 1);
        Assert.Contains(result.Differences(files), d => d.Contains("SkillTree.json", StringComparison.Ordinal));

        result.Write(files);
        Assert.Empty(SkillTreeGenerator.Generate(design.ToJsonString(), files, maxTier: 1).Differences(files));
        var set = ConfigLoader.LoadAndValidate(temp.Path, ConfigTables.All);
        Assert.True(set.Ok, string.Join("\n", set.Errors));
        Assert.Equal(13, SkillTree.Find(set.Set, "warrior.slash")!.MinCharLevel); // 1 + 4 x 3
    }

    [Fact]
    public void A_missing_class_row_is_appended_and_the_hand_written_rows_and_comments_stay()
    {
        using var temp = CopyTables();
        var path = Path.Combine(temp.Path, "ClassDef.json");
        var original = File.ReadAllText(path);
        int knight = original.IndexOf("{ \"id\": \"knight\"", StringComparison.Ordinal);
        int end = original.IndexOf('\n', knight);
        File.WriteAllText(path, original.Remove(knight, end - knight + 1));

        var files = new TableFiles(temp.Path);
        var result = SkillTreeGenerator.Generate(File.ReadAllText(DesignPath()), files, maxTier: 1);
        Assert.Single(result.ClassRows);
        result.Write(files);
        var written = File.ReadAllText(path);
        Assert.Contains("// Spec 3.5 root classes, v0.", written); // the hand-written header comment
        Assert.Contains("\"id\": \"knight\"", written);
        var set = ConfigLoader.LoadAndValidate(temp.Path, ConfigTables.All);
        Assert.True(set.Ok, string.Join("\n", set.Errors));
        Assert.Equal("warrior", ClassDef.Find(set.Set, "knight")!.Parent);
    }

    [Fact]
    public void A_hand_row_that_disagrees_with_the_design_file_is_a_problem_not_an_edit()
    {
        var files = new TableFiles(RepositoryPaths.Tables());
        var design = JsonNode.Parse(File.ReadAllText(DesignPath()))!;
        design["classes"]!.AsArray().First(c => (string)c!["id"]! == "knight")!["armor"] = "medium";
        var result = SkillTreeGenerator.Generate(design.ToJsonString(), files, maxTier: 1);
        Assert.Contains(result.Problems, p => p.Contains("ClassDef 'knight' armorClass", StringComparison.Ordinal));
    }

    private static TempDirectory CopyTables()
    {
        var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        foreach (var file in Directory.GetFiles(RepositoryPaths.Tables(), "*.json"))
            File.Copy(file, Path.Combine(temp.Path, Path.GetFileName(file)));
        return temp;
    }
}
