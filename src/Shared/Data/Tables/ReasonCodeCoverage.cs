using System;
using System.Collections.Generic;
using Shared.Protocol;
using Shared.Protocol.Auth;

namespace Shared.Data.Tables
{
    /// <summary>
    /// DataValidator rule for <see cref="ReasonCodeStrings"/> (spec 9.1: every failure has a reason the UI can show):
    /// every member of every reason code enum, except <c>None</c>, has a row. Add a new reason enum to <see cref="Enums"/>.
    /// </summary>
    public static class ReasonCodeCoverage
    {
        public static readonly IReadOnlyList<Type> Enums = new[]
        {
            typeof(AuthReasonCode),
            typeof(ConnectRejectReason),
            typeof(ClientReasonCode),
            typeof(ActionReason),
            typeof(PickupReason),
            typeof(ChatReason),
            typeof(InventoryMoveReason),
            typeof(OutpostReason),
            typeof(SkillTreeReason),
            typeof(BookReason),
            typeof(AttributeReason),
            typeof(JobReason),
            typeof(UseItemReason),
            typeof(ForgeReason),
        };

        /// <summary>One error per code with no row. Empty when the table is not loaded (the loader reports that).</summary>
        public static List<ValidationError> Check(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (!set.TryGet(typeof(ReasonCodeStrings), out var table))
                return errors;

            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in table.Rows)
                codes.Add(((ReasonCodeStrings)row).Code);
            var tableName = TableInfo.For(typeof(ReasonCodeStrings)).Name;

            if (!codes.Contains(ReasonCodeStrings.UnknownCode))
                errors.Add(new ValidationError(ValidationCodes.RuleBroken, tableName, null, nameof(ReasonCodeStrings.Code),
                    $"no row for '{ReasonCodeStrings.UnknownCode}' (shown for codes without a row)"));
            foreach (var type in Enums)
            {
                foreach (var name in Enum.GetNames(type))
                {
                    if (name != "None" && !codes.Contains(name))
                        errors.Add(new ValidationError(ValidationCodes.RuleBroken, tableName, null, nameof(ReasonCodeStrings.Code),
                            $"no row for {type.Name}.{name}"));
                }
            }
            return errors;
        }
    }
}
