using System.Reflection;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// The upper enum bounds the message readers check (R-2.5-04): every <c>Last*</c> constant of an enum type in the Shared
/// assembly must be the highest value its enum declares, so a value appended to an enum is not dropped as malformed by a
/// bound left behind (review of PR #266: <see cref="OutpostMessages.LastInteraction"/> was left at an older kind and the
/// server refused a valid contract board request). The two-sided ranges of the hot messages are checked one by one below.
/// </summary>
public class EnumBoundsTests
{
    private static IReadOnlyList<FieldInfo> LastConstants() =>
        typeof(ReliableMessages).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType.IsEnum && field.Name.StartsWith("Last", StringComparison.Ordinal))
            .ToList();

    private static decimal Number(object enumValue) => Convert.ToDecimal(enumValue);

    private static object HighestDeclared(Type enumType) => Enum.GetValues(enumType).Cast<object>().MaxBy(Number)!;

    [Fact]
    public void Every_last_constant_is_the_highest_value_of_its_enum()
    {
        var fields = LastConstants();
        Assert.True(fields.Count > 50, $"Found only {fields.Count} Last* enum constants; the scan is broken.");

        var stale = fields
            .Where(field => Number(field.GetRawConstantValue()!) != Number(HighestDeclared(field.FieldType)))
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name} = {Enum.ToObject(field.FieldType, field.GetRawConstantValue()!)}"
                             + $" (highest {field.FieldType.Name} is {HighestDeclared(field.FieldType)})")
            .ToList();
        Assert.True(stale.Count == 0, "Stale enum bounds: " + string.Join("; ", stale));
    }

    [Fact]
    public void The_hit_message_takes_kinds_up_to_the_highest_hit_kind()
    {
        var highest = Enum.GetValues<HitKind>().Max();
        var buffer = new byte[HitMessage.SizeFor(1)];
        HitEntry Hit(HitKind kind) => new() { AttackerId = 1, TargetId = 2, Amount = 3, Kind = kind };

        Assert.Equal(buffer.Length, HitMessage.Write(buffer, 1, new[] { Hit(highest) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitMessage.Write(buffer, 1, new[] { Hit(highest + 1) }));
    }

    [Fact]
    public void The_projectile_despawn_takes_reasons_up_to_the_highest_reason()
    {
        var highest = Enum.GetValues<ProjectileDespawnReason>().Max();
        Assert.True(ProjectileMessages.IsKnown(highest));
        Assert.False(ProjectileMessages.IsKnown(highest + 1));
    }

    [Fact]
    public void The_ground_area_despawn_takes_reasons_up_to_the_highest_reason()
    {
        var highest = Enum.GetValues<GroundAreaDespawnReason>().Max();
        Assert.True(GroundAreaMessages.IsKnown(highest));
        Assert.False(GroundAreaMessages.IsKnown(highest + 1));
    }
}
