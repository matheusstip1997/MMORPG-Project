using Shared.Protocol;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation;

/// <summary>
/// The pure rules of a death loot bag (spec 4.3 R-4.3-03, 4.4 R-4.4-01, M4 decision 8; WP-M4-9): the owner window and the
/// expiry from the death's time, who may do what at the window's edge, the stolen mark keeping its first owner, the mail split,
/// and the stack keys of marked stacks (a stolen stack never merges with a clean one nor with another take's).
/// </summary>
public class LootBagRulesTests
{
    private const long Died = 1_000_000, Owner = 7, Other = 8;

    [Fact]
    public void The_window_and_the_expiry_count_from_the_death()
    {
        Assert.Equal(Died + 60_000, LootBagRules.WindowUntil(Died, 60_000));
        Assert.Equal(Died + 900_000, LootBagRules.ExpiresAt(Died, 900_000));
        Assert.False(LootBagRules.IsExpired(Died + 899_999, Died + 900_000));
        Assert.True(LootBagRules.IsExpired(Died + 900_000, Died + 900_000));
    }

    [Theory]
    [InlineData(Owner, false, 0, LootBagAccess.Own)]
    [InlineData(Owner, false, 120_000, LootBagAccess.Own)]
    [InlineData(Other, true, 59_999, LootBagAccess.CollectForOwner)]
    [InlineData(Other, false, 59_999, LootBagAccess.Locked)]
    [InlineData(Other, true, 60_000, LootBagAccess.TakeStolen)] // a party member after the window takes with the mark too
    [InlineData(Other, false, 60_000, LootBagAccess.TakeStolen)]
    public void Who_may_do_what_at_the_window_edge(long viewer, bool sameParty, long sinceDeath, LootBagAccess expected)
    {
        Assert.Equal(expected, LootBagRules.Access(viewer, Owner, sameParty, Died + sinceDeath, LootBagRules.WindowUntil(Died, 60_000)));
    }

    [Fact]
    public void A_take_marks_a_clean_stack_and_keeps_the_first_mark_of_a_stolen_one()
    {
        var clean = new ItemStack(1, 0, false, 5);
        var marked = LootBagRules.Mark(clean, Owner, 42);
        Assert.Equal((true, Owner, 42L, 5), (marked.Stolen, marked.StolenFrom, marked.StolenAtMs, marked.Count));

        var again = LootBagRules.Mark(marked, Other, 99);
        Assert.Equal((Owner, 42L), (again.StolenFrom, again.StolenAtMs));

        var flaggedBefore = new ItemStack(1, 0, true, 5); // a stolen flag set before WP-M4-9: no owner known
        Assert.Equal(Other, LootBagRules.Mark(flaggedBefore, Other, 99).StolenFrom);
    }

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(1, 8, 1)]
    [InlineData(8, 8, 1)]
    [InlineData(9, 8, 2)]
    [InlineData(30, 8, 4)]
    public void Rows_split_into_mails_of_at_most_the_attachments_per_mail(int rows, int perMail, int mails)
    {
        Assert.Equal(mails, LootBagRules.MailCount(rows, perMail));
        int covered = 0;
        for (int m = 0; m < mails; m++)
        {
            var (start, count) = LootBagRules.MailSlice(rows, perMail, m);
            Assert.Equal(covered, start);
            Assert.InRange(count, 1, perMail);
            covered += count;
        }
        Assert.Equal(rows, covered);
    }

    [Fact]
    public void Time_left_never_goes_below_zero()
    {
        Assert.Equal(0u, LootBagRules.MsLeft(10, 5));
        Assert.Equal(5u, LootBagRules.MsLeft(5, 10));
    }

    [Fact]
    public void A_stolen_stack_never_merges_with_a_clean_one_nor_with_another_takes()
    {
        var bag = new Bag(4);
        Assert.Equal(5, bag.Add(new ItemStack(1, 0, false, 5), 99, null));
        Assert.Equal(3, bag.Add(new ItemStack(1, 0, true, 3, 0, Owner, 42), 99, null));
        Assert.Equal(2, bag.Add(new ItemStack(1, 0, true, 2, 0, Owner, 43), 99, null)); // another take
        Assert.Equal(4, bag.Add(new ItemStack(1, 0, true, 4, 0, Owner, 42), 99, null)); // the same take: merges

        Assert.Equal((5, false), (bag[0].Count, bag[0].Stolen));
        Assert.Equal((7, Owner, 42L), (bag[1].Count, bag[1].StolenFrom, bag[1].StolenAtMs));
        Assert.Equal((2, 43L), (bag[2].Count, bag[2].StolenAtMs));
        Assert.True(bag[3].IsEmpty);
        Assert.Equal(Shared.Simulation.Items.BagMoveResult.Swapped, bag.TryMove(1, 2, 99, null)); // two marks swap, never merge
    }

    [Fact]
    public void A_clean_stack_carries_no_mark_and_WithCount_keeps_it()
    {
        Assert.Equal((0L, 0L), (new ItemStack(1, 0, false, 5, 0, Owner, 42).StolenFrom, new ItemStack(1, 0, false, 5, 0, Owner, 42).StolenAtMs));
        var marked = new ItemStack(1, 0, true, 5, 0, Owner, 42).WithCount(2);
        Assert.Equal((2, Owner, 42L), (marked.Count, marked.StolenFrom, marked.StolenAtMs));
    }
}
