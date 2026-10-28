using Dapper;
using GameServer.Host.Config;
using Npgsql;
using Shared.Data;
using Shared.Data.Tables;

/// <summary>
/// Dev only (WP-M2-5 play tests): sets a character's level in the local dev realm database. The level's skill points come
/// from <c>ProgressionSettings.skillPointsPerLevel</c> (R-3.5-04); exp starts the level at 0. Never for a shared or real
/// database: <c>DbMigrate</c> allows it only with <c>--dev</c>.
/// </summary>
internal static class DevLevel
{
    public static async Task<int> SetAsync(string realmConnection, string characterName, int level)
    {
        var tables = ConfigLoader.LoadAndValidate(Path.Combine("data", "tables"), ConfigTables.All);
        if (!tables.Ok || ProgressionSettings.Find(tables.Set) is not { } progression)
        {
            Console.Error.WriteLine("--set-level needs the config tables (run it from the repository root).");
            return 2;
        }
        if (level > progression.MaxLevel)
        {
            Console.Error.WriteLine($"--set-level: level {level} is above ProgressionSettings.maxLevel {progression.MaxLevel}.");
            return 2;
        }
        await using var source = NpgsqlDataSource.Create(realmConnection);
        await using var connection = await source.OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, int Level)?>(
            "SELECT character_id, level FROM characters WHERE name = @characterName AND deleted_at IS NULL", new { characterName });
        if (row is not { } character)
        {
            Console.Error.WriteLine($"--set-level: no character named '{characterName}'.");
            return 1;
        }
        int gained = Math.Max(0, level - character.Level) * progression.SkillPointsPerLevel;
        await connection.ExecuteAsync("""
            UPDATE characters SET level = @level, exp = 0, skill_points = skill_points + @gained, version = version + 1, updated_at = now()
            WHERE character_id = @id
            """, new { level, gained, id = character.Id });
        Console.WriteLine($"{characterName}: level {character.Level} -> {level}, +{gained} skill points");
        return 0;
    }
}
