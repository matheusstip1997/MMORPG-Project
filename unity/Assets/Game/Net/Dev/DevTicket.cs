#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System;
using System.IO;
using Shared.Protocol;
using Shared.Security;
using UnityEngine;

namespace Game.Net.Dev
{
    /// <summary>
    /// Dev-only stand-in for the Auth API response (R-2.5-01/-02): the file <c>src/Tools/DevConnect</c> writes, with
    /// where to connect, the ticket, the session key, and the NetSecurity size caps. Compiled only in the editor and in
    /// builds with the <c>MMORPG_DEV_LOGIN</c> define. The file is gitignored (unity/UserSettings) and holds a live
    /// session key; nothing here ships in a release build.
    /// </summary>
    public sealed class DevTicket
    {
        public const string DefaultFileName = "DevTicket.json";

        private DevTicket(string host, int port, long accountId, long expiresAtMs, byte[] ticket, byte[] sessionKey, ClientNetLimits limits)
        {
            Host = host;
            Port = port;
            AccountId = accountId;
            ExpiresAtMs = expiresAtMs;
            Ticket = ticket;
            SessionKey = sessionKey;
            Limits = limits;
        }

        public string Host { get; }
        public int Port { get; }
        public long AccountId { get; }

        /// <summary>UTC Unix ms. <c>DevConnect serve</c> rewrites the file well before this, so a ticket past it means no host is running.</summary>
        public long ExpiresAtMs { get; }

        public byte[] Ticket { get; }

        /// <summary>Clear it after <see cref="ClientConnection.Connect"/>.</summary>
        public byte[] SessionKey { get; }

        public ClientNetLimits Limits { get; }

        /// <summary><c>unity/UserSettings/DevTicket.json</c>, where <c>DevConnect</c> writes by default.</summary>
        public static string DefaultPath => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "UserSettings", DefaultFileName);

        /// <summary>Parses and checks the file contents; every field is bound-checked (R-2.5-04).</summary>
        public static bool TryParse(string json, out DevTicket? ticket, out string error)
        {
            ticket = null;
            FileContents? contents;
            try
            {
                contents = JsonUtility.FromJson<FileContents>(json);
            }
            catch (ArgumentException e)
            {
                error = $"not valid JSON: {e.Message}";
                return false;
            }
            if (contents == null)
            {
                error = "empty file";
                return false;
            }
            if (string.IsNullOrEmpty(contents.host))
            {
                error = "host is missing";
                return false;
            }
            if (contents.port < 1 || contents.port > 65535)
            {
                error = $"port {contents.port} is out of range";
                return false;
            }
            if (contents.maxReliableMessageBytes <= 0 || contents.maxUnreliableMessageBytes <= 0)
            {
                error = "message size caps are missing";
                return false;
            }
            if (!TryDecode(contents.ticket, out var ticketBytes) || ticketBytes.Length == 0 || ticketBytes.Length > ConnectRequest.MaxTicketLength)
            {
                error = "ticket is missing or not valid base64";
                return false;
            }
            if (!TryDecode(contents.sessionKey, out var sessionKey) || sessionKey.Length != SessionCrypto.KeySize)
            {
                error = $"session key must be {SessionCrypto.KeySize} bytes of base64";
                return false;
            }

            ticket = new DevTicket(
                contents.host,
                contents.port,
                contents.accountId,
                contents.expiresAtMs,
                ticketBytes,
                sessionKey,
                new ClientNetLimits(contents.maxReliableMessageBytes, contents.maxUnreliableMessageBytes));
            error = string.Empty;
            return true;
        }

        public static bool TryLoad(string path, out DevTicket? ticket, out string error)
        {
            ticket = null;
            if (!File.Exists(path))
            {
                error = $"no dev ticket at {path}. Run: dotnet run --project src/Tools/DevConnect -- serve";
                return false;
            }
            return TryParse(File.ReadAllText(path), out ticket, out error);
        }

        private static bool TryDecode(string? base64, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(base64))
                return false;
            try
            {
                bytes = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // JsonUtility maps public fields by name; these match the camelCase names DevConnect writes.
#pragma warning disable 0649
        [Serializable]
        private sealed class FileContents
        {
            public string? host;
            public int port;
            public long accountId;
            public long expiresAtMs;
            public string? ticket;
            public string? sessionKey;
            public int maxReliableMessageBytes;
            public int maxUnreliableMessageBytes;
        }
#pragma warning restore 0649
    }
}
#endif
