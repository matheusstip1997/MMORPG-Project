#nullable enable
using System;
using System.Globalization;
using System.Text;
using Shared.Data;
using Shared.Protocol.Auth;

namespace Game.UI.Auth
{
    /// <summary>
    /// JSON for the Auth API contract (<c>Shared.Protocol.Auth</c>, spec 9.10): camelCase names, enums as their names,
    /// byte arrays as base64. Hand-written on the portable <see cref="ConfigJson"/> parser, so the client needs no JSON
    /// package. Unknown fields are ignored (the server may add fields); a known field of the wrong type makes the whole
    /// response unreadable. The raw <c>reasonCode</c> string is kept, so a code newer than this client still reaches the
    /// text lookup.
    /// </summary>
    public static class AuthJson
    {
        // ---- requests

        public static string Login(LoginRequest request) =>
            new Writer().Begin().String("ticket", request.Ticket).End();

        public static string CreateCharacter(CreateCharacterRequest request)
        {
            var appearance = new Writer().Begin()
                .Number("version", request.Appearance.Version)
                .String("data", Convert.ToBase64String(request.Appearance.Data))
                .End();
            return new Writer().Begin()
                .String("requestId", request.RequestId.ToString("D"))
                .String("name", request.Name)
                .String("initialClass", request.InitialClass)
                .Raw("appearance", appearance)
                .End();
        }

        // ---- responses

        /// <summary>
        /// Reads the common part. <paramref name="reasonCode"/> is the raw name ("" for <c>None</c>);
        /// <paramref name="root"/> is the parsed object for the type-specific reader.
        /// </summary>
        public static bool TryReadBase(string json, AuthResponse into, out string reasonCode, out ConfigJsonValue root)
        {
            reasonCode = "";
            root = null!;
            if (!ConfigJson.TryParse(json, out var parsed, out _) || parsed.Kind != ConfigJsonKind.Object)
                return false;
            root = parsed;
            if (!Bool(root, "ok", out var ok) || !String(root, "reasonCode", out var reason))
                return false;
            into.Ok = ok;
            reasonCode = reason == nameof(AuthReasonCode.None) ? "" : reason;
            into.ReasonCode = Enum.TryParse(reason, false, out AuthReasonCode code) && Enum.IsDefined(typeof(AuthReasonCode), code)
                ? code
                : AuthReasonCode.None;
            return true;
        }

        public static bool TryReadLogin(ConfigJsonValue root, LoginResponse into)
        {
            if (!String(root, "accessToken", out var token) || !Long(root, "accessTokenExpiresAtUtcMs", out var expires)
                || !Long(root, "accountId", out var accountId))
                return false;
            into.AccessToken = token;
            into.AccessTokenExpiresAtUtcMs = expires;
            into.AccountId = accountId;
            if (root.TryGetMember("characters", out var characters) && characters.Kind != ConfigJsonKind.Null)
            {
                var list = new CharacterListResponse();
                if (characters.Kind != ConfigJsonKind.Object || !TryReadCharacterList(characters, list))
                    return false;
                into.Characters = list;
            }
            return true;
        }

        public static bool TryReadCharacterList(ConfigJsonValue root, CharacterListResponse into)
        {
            if (!Int(root, "realmId", out var realm) || !Int(root, "slotsPerRealm", out var slots)
                || !Long(root, "serverTimeUtcMs", out var serverTime))
                return false;
            into.RealmId = realm;
            into.SlotsPerRealm = slots;
            into.ServerTimeUtcMs = serverTime;
            if (!root.TryGetMember("characters", out var array) || array.Kind != ConfigJsonKind.Array)
                return false;
            var cards = new CharacterCard[array.Items.Count];
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i] = new CharacterCard();
                if (array.Items[i].Kind != ConfigJsonKind.Object || !TryReadCard(array.Items[i], cards[i]))
                    return false;
            }
            into.Characters = cards;
            return true;
        }

        public static bool TryReadCard(ConfigJsonValue root, CharacterCard into)
        {
            if (!Long(root, "characterId", out var id) || !Int(root, "slot", out var slot) || !String(root, "name", out var name)
                || !String(root, "classId", out var classId) || !Int(root, "level", out var level)
                || !Bool(root, "pendingDeletion", out var pending) || !Long(root, "purgeAfterUtcMs", out var purge))
                return false;
            into.CharacterId = id;
            into.Slot = slot;
            into.Name = name;
            into.ClassId = classId;
            into.Level = level;
            into.PendingDeletion = pending;
            into.PurgeAfterUtcMs = purge;
            return true;
        }

        public static bool TryReadCreate(ConfigJsonValue root, CreateCharacterResult into)
        {
            if (!Long(root, "characterId", out var id))
                return false;
            into.CharacterId = id;
            if (root.TryGetMember("character", out var card) && card.Kind != ConfigJsonKind.Null)
            {
                into.Character = new CharacterCard();
                if (card.Kind != ConfigJsonKind.Object || !TryReadCard(card, into.Character))
                    return false;
            }
            return true;
        }

        public static bool TryReadDelete(ConfigJsonValue root, DeleteCharacterResult into)
        {
            if (!Long(root, "purgeAfterUtcMs", out var purge))
                return false;
            into.PurgeAfterUtcMs = purge;
            return true;
        }

        public static bool TryReadEnterWorld(ConfigJsonValue root, EnterWorldResult into)
        {
            if (!Bytes(root, "ticket", out var ticket) || !Bytes(root, "sessionKey", out var key)
                || !Long(root, "ticketExpiresAtUtcMs", out var expires))
                return false;
            into.Ticket = ticket;
            into.SessionKey = key;
            into.TicketExpiresAtUtcMs = expires;
            return true;
        }

        // ---- field readers: a missing field reads as its default (refusals carry only ok and reasonCode)

        private static bool Bool(ConfigJsonValue root, string name, out bool value)
        {
            value = false;
            if (!root.TryGetMember(name, out var field))
                return true;
            if (field.Kind != ConfigJsonKind.True && field.Kind != ConfigJsonKind.False)
                return false;
            value = field.Kind == ConfigJsonKind.True;
            return true;
        }

        private static bool Long(ConfigJsonValue root, string name, out long value)
        {
            value = 0;
            return !root.TryGetMember(name, out var field) || field.TryGetInt64(out value);
        }

        private static bool Int(ConfigJsonValue root, string name, out int value)
        {
            value = 0;
            if (!Long(root, name, out var number) || number < int.MinValue || number > int.MaxValue)
                return false;
            value = (int)number;
            return true;
        }

        private static bool String(ConfigJsonValue root, string name, out string value)
        {
            value = "";
            if (!root.TryGetMember(name, out var field) || field.Kind == ConfigJsonKind.Null)
                return true;
            if (field.Kind != ConfigJsonKind.String)
                return false;
            value = field.Text;
            return true;
        }

        private static bool Bytes(ConfigJsonValue root, string name, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!String(root, name, out var base64))
                return false;
            try
            {
                value = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Writes one flat JSON object; strings escaped per RFC 8259.</summary>
        private sealed class Writer
        {
            private readonly StringBuilder _text = new StringBuilder();
            private bool _first = true;

            public Writer Begin()
            {
                _text.Append('{');
                return this;
            }

            public string End() => _text.Append('}').ToString();

            public Writer String(string name, string value)
            {
                Name(name);
                Quote(value);
                return this;
            }

            public Writer Number(string name, long value)
            {
                Name(name);
                _text.Append(value.ToString(CultureInfo.InvariantCulture));
                return this;
            }

            public Writer Raw(string name, string json)
            {
                Name(name);
                _text.Append(json);
                return this;
            }

            private void Name(string name)
            {
                if (!_first)
                    _text.Append(',');
                _first = false;
                Quote(name);
                _text.Append(':');
            }

            private void Quote(string value)
            {
                _text.Append('"');
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': _text.Append("\\\""); break;
                        case '\\': _text.Append("\\\\"); break;
                        case '\n': _text.Append("\\n"); break;
                        case '\r': _text.Append("\\r"); break;
                        case '\t': _text.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                _text.Append(c);
                            break;
                    }
                }
                _text.Append('"');
            }
        }
    }
}
