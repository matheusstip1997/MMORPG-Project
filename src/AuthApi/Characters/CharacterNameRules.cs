using Shared.Data.Tables;

namespace AuthApi.Characters;

/// <summary>
/// Name rules from <see cref="CharacterCreationConfig"/> (spec 9.10): length, allowed characters, and the filter.
/// Uniqueness (per realm, ignoring case, R-2.1-05) is checked by the database on insert.
/// </summary>
public sealed class CharacterNameRules(CharacterCreationConfig config)
{
    private readonly HashSet<char> _allowed = [.. config.NameAllowedCharacters];

    public bool IsValid(string? name)
    {
        if (name == null || name.Length < config.NameMinLength || name.Length > config.NameMaxLength)
            return false;

        foreach (var c in name)
        {
            if (!_allowed.Contains(c))
                return false;
        }

        foreach (var word in config.NameBlockedWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
