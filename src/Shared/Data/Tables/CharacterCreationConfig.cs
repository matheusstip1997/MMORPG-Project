using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Character creation rules (spec 9.10): name rules, allowed initial classes, appearance, and the state a new
    /// character starts with. The slot count lives in <see cref="CharacterSlots"/>.
    /// TODO(open): allowed name characters, the name filter, the initial class list and the appearance options are
    /// undecided (spec 9.10); the values in data/tables/CharacterCreationConfig.json are placeholders.
    /// </summary>
    [Table("CharacterCreationConfig", 2)]
    [Singleton]
    public sealed class CharacterCreationConfig : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Name length in UTF-16 code units (the placeholder character set is ASCII, so one per character).</summary>
        [Range(1, 64)] public int NameMinLength { get; set; }
        [Range(1, 64)] public int NameMaxLength { get; set; }

        /// <summary>Every character a name may contain, compared ordinally. Case-insensitive uniqueness is the database's job.</summary>
        public string NameAllowedCharacters { get; set; } = "";

        /// <summary>Lowercase words a name may not contain anywhere, ignoring case.</summary>
        public string[] NameBlockedWords { get; set; } = Array.Empty<string>();

        /// <summary>Class IDs a new character may pick; each names a <see cref="ClassDef"/> row (the game server builds the player from it).</summary>
        [Ref(typeof(ClassDef))] public string[] AllowedInitialClasses { get; set; } = Array.Empty<string>();

        /// <summary>The appearance blob version the server accepts; the blob stays opaque until the screen is designed.</summary>
        [Range(0, 65_535)] public int AppearanceVersion { get; set; }

        /// <summary>
        /// Largest appearance blob in bytes. 0 while characters do not store an appearance: raising it needs a realm
        /// migration that adds the column first.
        /// </summary>
        [Range(0, 65_536)] public int AppearanceMaxBytes { get; set; }

        // How a new character starts. Where it starts is the exported world's player spawn marker (ServerLoop.PlayerSpawnMarkerId,
        // backlog C2), not a config value. The game server does not read StartHp and StartMp: a character it never
        // placed starts with the full HP and MP of its ClassDef (spec 3.5). They only fill the new row.
        [Range(1, 1_000)] public int StartLevel { get; set; }
        [Range(1, 1_000_000)] public int StartHp { get; set; }
        [Range(0, 1_000_000)] public int StartMp { get; set; }

        /// <summary>
        /// Unspent skill points of a new character (R-3.5-04: one point per level from level 1, so a new character has 1).
        /// Optional (WP-M2-3); with <see cref="ProgressionSettings"/> loaded it must be <c>startLevel x skillPointsPerLevel</c>.
        /// </summary>
        [Optional, Range(0, 100_000)] public int StartSkillPoints { get; set; } = 1;

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (ProgressionSettings.Find(set) is { } progression && StartSkillPoints != StartLevel * progression.SkillPointsPerLevel)
                errors.Add(nameof(StartSkillPoints), $"must be startLevel x ProgressionSettings.skillPointsPerLevel ({StartLevel * progression.SkillPointsPerLevel}, R-3.5-04)");
        }

        public void Validate(RowErrors errors)
        {
            if (NameMinLength > NameMaxLength)
                errors.Add(nameof(NameMinLength), "must not be greater than NameMaxLength");

            if (NameAllowedCharacters.Length == 0)
                errors.Add(nameof(NameAllowedCharacters), "must not be empty");
            var seenCharacters = new HashSet<char>();
            foreach (var c in NameAllowedCharacters)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c))
                    errors.Add(nameof(NameAllowedCharacters), $"must not contain whitespace, control or surrogate characters (U+{(int)c:X4})");
                else if (!seenCharacters.Add(c))
                    errors.Add(nameof(NameAllowedCharacters), $"'{c}' is listed twice");
            }

            foreach (var word in NameBlockedWords)
            {
                if (string.IsNullOrWhiteSpace(word) || word.Trim() != word)
                    errors.Add(nameof(NameBlockedWords), $"'{word}' must be non-empty with no surrounding spaces");
                else if (word.ToLowerInvariant() != word)
                    errors.Add(nameof(NameBlockedWords), $"'{word}' must be lowercase");
            }

            if (AllowedInitialClasses.Length == 0)
                errors.Add(nameof(AllowedInitialClasses), "must list at least one class");
            var seenClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var classId in AllowedInitialClasses)
            {
                if (string.IsNullOrWhiteSpace(classId))
                    errors.Add(nameof(AllowedInitialClasses), "class IDs must not be empty");
                else if (!seenClasses.Add(classId))
                    errors.Add(nameof(AllowedInitialClasses), $"'{classId}' is listed twice");
            }
        }
    }
}
