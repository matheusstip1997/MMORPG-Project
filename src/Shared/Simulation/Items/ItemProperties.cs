using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Items
{
    /// <summary>
    /// The typed versioned properties of an item instance (spec 4.1 implementation: "item data uses a versioned properties
    /// field so new attributes do not need risky migrations"; <c>item_instances.properties</c>, JSONB). v1 (M2) held the
    /// enchant level, the gem list, the original owner and the owner history, all inert. v2 (M3 item model, WP-M3-2) adds a
    /// crafted item's <see cref="CraftedStats"/> snapshot (it replaces the item's <c>ItemStatBonusDef</c> list for that
    /// instance), its <see cref="Quality"/> and <see cref="CrafterId"/>, and <see cref="Tainted"/> (R-4.4-03).
    /// <para>
    /// <see cref="TryParse"/> reads v1 and v2 and keeps members it does not know; <see cref="ToJson"/> always writes v2 with
    /// the known fields in a fixed order, then the unknown ones sorted by name, so the same properties always give the same
    /// text (architecture rule 6). The server parses once per change (login, creation), never per tick. Portable (C# 9): it
    /// reads with the shared <see cref="ConfigJson"/> parser.
    /// </para>
    /// </summary>
    public sealed class ItemProperties
    {
        public const int CurrentVersion = 2;

        /// <summary>Most gems an instance holds (<see cref="ItemDef.MaxGemSlots"/>).</summary>
        public const int MaxGems = ItemDef.MaxGemSlots;

        /// <summary>Most lines in a crafted stats snapshot (a bound for the wire and for hostile data).</summary>
        public const int MaxCraftedStats = 16;

        /// <summary>The largest quality and enchant level the wire carries (16 and 8 bits).</summary>
        public const int MaxQuality = ushort.MaxValue;

        public const int MaxEnchant = byte.MaxValue;

        private static readonly string[] KnownFields =
            { "v", "enchant", "gems", "craftedStats", "quality", "crafterId", "originalOwner", "ownerHistory", "tainted" };

        private readonly List<KeyValuePair<string, ConfigJsonValue>> _unknown = new List<KeyValuePair<string, ConfigJsonValue>>();

        /// <summary>The version the properties were read as (1 or 2); <see cref="ToJson"/> writes <see cref="CurrentVersion"/>.</summary>
        public int ReadVersion { get; private set; } = CurrentVersion;

        /// <summary>Enchant level (R-4.1-01 +0 to +9; the bounds are WP-M3-8's table).</summary>
        public int Enchant { get; set; }

        /// <summary>Socketed gems, as <c>ItemDef</c> ids, in socket order (WP-M3-9).</summary>
        public List<string> Gems { get; } = new List<string>();

        /// <summary>
        /// A crafted instance's stats (spec 5.3 "output is a stats snapshot": materials plus quality, rarity included), used
        /// as stored instead of the item's <c>ItemStatBonusDef</c> list. Empty for dropped and vendor gear.
        /// </summary>
        public List<ItemStatBonus> CraftedStats { get; } = new List<ItemStatBonus>();

        /// <summary>The forge's quality (R-5.3-02); 0 when not crafted.</summary>
        public int Quality { get; set; }

        /// <summary>The crafter's character id; 0 when not crafted. Never shown as a name (rule 8).</summary>
        public long CrafterId { get; set; }

        /// <summary>The character the instance was created for (M4 owner history).</summary>
        public long OriginalOwner { get; set; }

        /// <summary>Previous owners' character ids (M4; inert).</summary>
        public List<long> OwnerHistory { get; } = new List<long>();

        /// <summary>Made with a stolen input (R-4.4-03 "any" rule).</summary>
        public bool Tainted { get; set; }

        public bool HasCraftedStats => CraftedStats.Count > 0;

        /// <summary>Members of a later version this build does not know; kept and written back unchanged.</summary>
        public int UnknownCount => _unknown.Count;

        /// <summary>The properties of a new instance: nothing set but its original owner.</summary>
        public static ItemProperties New(long originalOwner) => new ItemProperties { OriginalOwner = originalOwner };

        public ItemProperties Clone()
        {
            var copy = new ItemProperties
            {
                ReadVersion = ReadVersion, Enchant = Enchant, Quality = Quality, CrafterId = CrafterId,
                OriginalOwner = OriginalOwner, Tainted = Tainted,
            };
            copy.Gems.AddRange(Gems);
            copy.CraftedStats.AddRange(CraftedStats);
            copy.OwnerHistory.AddRange(OwnerHistory);
            copy._unknown.AddRange(_unknown); // parsed values are never changed, so sharing them is safe
            return copy;
        }

        /// <summary>
        /// Reads stored properties: an object with <c>v</c> 1 or 2. Missing known fields take their defaults; a known field
        /// of the wrong type or out of range, too many gems or stat lines, or a later version refuse (false, with a reason).
        /// </summary>
        public static bool TryParse(string json, out ItemProperties properties, out string error)
        {
            properties = null!;
            if (!ConfigJson.TryParse(json ?? string.Empty, out var root, out error))
                return false;
            if (root.Kind != ConfigJsonKind.Object)
                return Fail("properties must be an object", out error);
            if (!root.TryGetMember("v", out var version) || !version.TryGetInt64(out long v) || v < 1 || v > CurrentVersion)
                return Fail($"\"v\" must be 1 to {CurrentVersion}", out error);
            var result = new ItemProperties { ReadVersion = (int)v };
            foreach (var member in root.Members)
            {
                var value = member.Value;
                switch (member.Key)
                {
                    case "v":
                        break;
                    case "enchant":
                        if (!TryInt(value, 0, MaxEnchant, out int enchant))
                            return Fail("\"enchant\" must be an integer from 0 to " + MaxEnchant, out error);
                        result.Enchant = enchant;
                        break;
                    case "gems":
                        if (value.Kind != ConfigJsonKind.Array || value.Items.Count > MaxGems)
                            return Fail($"\"gems\" must be an array of at most {MaxGems} item ids", out error);
                        foreach (var gem in value.Items)
                        {
                            if (gem.Kind != ConfigJsonKind.String || gem.Text.Length == 0)
                                return Fail("a gem must be an item id", out error);
                            result.Gems.Add(gem.Text);
                        }
                        break;
                    case "craftedStats":
                        if (value.Kind != ConfigJsonKind.Array || value.Items.Count > MaxCraftedStats)
                            return Fail($"\"craftedStats\" must be an array of at most {MaxCraftedStats} lines", out error);
                        foreach (var line in value.Items)
                        {
                            if (line.Kind != ConfigJsonKind.Object || !line.TryGetMember("stat", out var stat)
                                || stat.Kind != ConfigJsonKind.String || stat.Text.Length == 0
                                || !TryOptionalInt(line, "flat", out int flat) || !TryOptionalInt(line, "percent", out int percent))
                                return Fail("a crafted stat must be {\"stat\": id, \"flat\": int, \"percent\": int}", out error);
                            result.CraftedStats.Add(new ItemStatBonus(stat.Text, flat, percent));
                        }
                        break;
                    case "quality":
                        if (!TryInt(value, 0, MaxQuality, out int quality))
                            return Fail("\"quality\" must be an integer from 0 to " + MaxQuality, out error);
                        result.Quality = quality;
                        break;
                    case "crafterId":
                        if (!value.TryGetInt64(out long crafter) || crafter < 0)
                            return Fail("\"crafterId\" must be a character id", out error);
                        result.CrafterId = crafter;
                        break;
                    case "originalOwner":
                        if (!value.TryGetInt64(out long owner) || owner < 0)
                            return Fail("\"originalOwner\" must be a character id", out error);
                        result.OriginalOwner = owner;
                        break;
                    case "ownerHistory":
                        if (value.Kind != ConfigJsonKind.Array)
                            return Fail("\"ownerHistory\" must be an array of character ids", out error);
                        foreach (var item in value.Items)
                        {
                            if (!item.TryGetInt64(out long previous) || previous < 0)
                                return Fail("\"ownerHistory\" must be an array of character ids", out error);
                            result.OwnerHistory.Add(previous);
                        }
                        break;
                    case "tainted":
                        if (value.Kind != ConfigJsonKind.True && value.Kind != ConfigJsonKind.False)
                            return Fail("\"tainted\" must be true or false", out error);
                        result.Tainted = value.Kind == ConfigJsonKind.True;
                        break;
                    default:
                        result._unknown.Add(member);
                        break;
                }
            }
            result._unknown.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            properties = result;
            error = string.Empty;
            return true;
        }

        /// <summary>The v2 text: the known fields in a fixed order, then the unknown members sorted by name.</summary>
        public string ToJson()
        {
            var text = new StringBuilder(128);
            text.Append("{\"v\":").Append(CurrentVersion);
            text.Append(",\"enchant\":").Append(Enchant.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"gems\":[");
            for (int i = 0; i < Gems.Count; i++)
            {
                if (i > 0)
                    text.Append(',');
                WriteString(text, Gems[i]);
            }
            text.Append("],\"craftedStats\":[");
            for (int i = 0; i < CraftedStats.Count; i++)
            {
                var line = CraftedStats[i];
                if (i > 0)
                    text.Append(',');
                text.Append("{\"stat\":");
                WriteString(text, line.StatId);
                text.Append(",\"flat\":").Append(line.Flat.ToString(CultureInfo.InvariantCulture));
                text.Append(",\"percent\":").Append(line.Percent.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            text.Append("],\"quality\":").Append(Quality.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"crafterId\":").Append(CrafterId.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"originalOwner\":").Append(OriginalOwner.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"ownerHistory\":[");
            for (int i = 0; i < OwnerHistory.Count; i++)
            {
                if (i > 0)
                    text.Append(',');
                text.Append(OwnerHistory[i].ToString(CultureInfo.InvariantCulture));
            }
            text.Append("],\"tainted\":").Append(Tainted ? "true" : "false");
            foreach (var member in _unknown)
            {
                text.Append(',');
                WriteString(text, member.Key);
                text.Append(':');
                WriteValue(text, member.Value);
            }
            return text.Append('}').ToString();
        }

        /// <summary>A field name this version writes itself (an unknown member never shadows one).</summary>
        public static bool IsKnownField(string name) => Array.IndexOf(KnownFields, name) >= 0;

        private static bool TryInt(ConfigJsonValue value, int min, int max, out int result)
        {
            result = 0;
            if (!value.TryGetInt64(out long number) || number < min || number > max)
                return false;
            result = (int)number;
            return true;
        }

        private static bool TryOptionalInt(ConfigJsonValue line, string name, out int result)
        {
            result = 0;
            return !line.TryGetMember(name, out var value) || TryInt(value, int.MinValue, int.MaxValue, out result);
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }

        private static void WriteValue(StringBuilder text, ConfigJsonValue value)
        {
            switch (value.Kind)
            {
                case ConfigJsonKind.Null:
                    text.Append("null");
                    break;
                case ConfigJsonKind.True:
                    text.Append("true");
                    break;
                case ConfigJsonKind.False:
                    text.Append("false");
                    break;
                case ConfigJsonKind.Number:
                    text.Append(value.Text);
                    break;
                case ConfigJsonKind.String:
                    WriteString(text, value.Text);
                    break;
                case ConfigJsonKind.Array:
                    text.Append('[');
                    for (int i = 0; i < value.Items.Count; i++)
                    {
                        if (i > 0)
                            text.Append(',');
                        WriteValue(text, value.Items[i]);
                    }
                    text.Append(']');
                    break;
                default:
                    text.Append('{');
                    for (int i = 0; i < value.Members.Count; i++)
                    {
                        if (i > 0)
                            text.Append(',');
                        WriteString(text, value.Members[i].Key);
                        text.Append(':');
                        WriteValue(text, value.Members[i].Value);
                    }
                    text.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        text.Append("\\\"");
                        break;
                    case '\\':
                        text.Append("\\\\");
                        break;
                    case '\n':
                        text.Append("\\n");
                        break;
                    case '\r':
                        text.Append("\\r");
                        break;
                    case '\t':
                        text.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
    }
}
