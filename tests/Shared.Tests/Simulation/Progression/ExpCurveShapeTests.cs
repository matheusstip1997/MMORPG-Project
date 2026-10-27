using Shared.Data.Tables;

namespace Shared.Tests.Simulation.Progression;

/// <summary>
/// The committed <c>ExpCurve</c> keeps the shape the user decided (2026-10-27, spec 3.6 "M2 placeholder curves"): about 8
/// even-level kills per level at level 1, 17 at 15, 26 at 30 (kills per level = 8 + 0.6 x level, about 520 from 1 to 30).
/// The values are placeholders generated from <c>monsterBaseExp = 10 + 10 x level</c> and
/// <c>expToNext = (1 + level) x (80 + 6 x level)</c>; a retune that keeps the shape changes this test, not the code.
/// </summary>
public class ExpCurveShapeTests
{
    private static List<ExpCurve> Rows()
    {
        var set = CommittedTables.Load();
        Assert.True(set.TryGet(typeof(ExpCurve), out var table));
        return table.Rows.Cast<ExpCurve>().ToList();
    }

    [Fact]
    public void Every_row_is_the_documented_formula()
    {
        foreach (var row in Rows())
        {
            Assert.Equal(10 + 10 * row.Level, row.MonsterBaseExp);
            Assert.Equal((1 + row.Level) * (80 + 6 * row.Level), row.ExpToNext);
        }
    }

    [Fact]
    public void Even_level_kills_per_level_follow_8_plus_0_6_per_level_up_to_30()
    {
        var rows = Rows();
        for (int level = 1; level <= 30; level++)
        {
            var row = rows[level - 1];
            double kills = row.ExpToNext / (double)row.MonsterBaseExp;
            Assert.InRange(kills, 8 + 0.6 * level - 0.5, 8 + 0.6 * level + 0.5);
        }
        Assert.InRange(rows[0].ExpToNext / (double)rows[0].MonsterBaseExp, 8, 9); // about 8 at level 1
        Assert.InRange(rows[14].ExpToNext / (double)rows[14].MonsterBaseExp, 16.5, 17.5); // about 17 at 15
        Assert.InRange(rows[29].ExpToNext / (double)rows[29].MonsterBaseExp, 25.5, 26.5); // about 26 at 30
        double total = rows.Take(30).Sum(r => r.ExpToNext / (double)r.MonsterBaseExp);
        Assert.InRange(total, 500, 540); // roughly 520 kills from 1 to 30
    }

    [Fact]
    public void The_curve_covers_every_level_below_the_cap_and_never_decreases()
    {
        var rows = Rows();
        var settings = ProgressionSettings.Find(CommittedTables.Load());
        Assert.NotNull(settings);
        Assert.Equal(settings!.MaxLevel - 1, rows.Count);
        Assert.True(rows.Count >= 129); // levels 1 to at least 130 (R-3.6-02 bands end at 130)
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i + 1, rows[i].Level);
            if (i > 0)
                Assert.True(rows[i].ExpToNext >= rows[i - 1].ExpToNext);
        }
    }
}
