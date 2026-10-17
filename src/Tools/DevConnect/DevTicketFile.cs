using System.Text.Json;
using GameServer.Net.Tickets;
using Shared.Data.Tables;

namespace DevConnect;

/// <summary>
/// What the Unity client reads in place of the Auth API response (dev only, R-2.5-01/-02): where to connect, the
/// opaque ticket, the session key, and the message size caps from the NetSecurity table. The Unity side is
/// <c>unity/Assets/Game/Net/Dev/DevTicketFile.cs</c>; keep the field names in step.
/// The file holds a live session key: it goes to a gitignored path (default <c>unity/UserSettings/</c>) and is never committed.
/// </summary>
public static class DevTicketFile
{
    public const string DefaultPath = "unity/UserSettings/DevTicket.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static void Write(string path, string host, int port, long accountId, long expiresAtMs, IssuedTicket issued, NetSecurity limits)
    {
        var contents = new
        {
            host,
            port,
            accountId,
            expiresAtMs,
            ticket = Convert.ToBase64String(issued.Ticket),
            sessionKey = Convert.ToBase64String(issued.SessionKey),
            maxReliableMessageBytes = limits.MaxReliableMessageBytes,
            maxUnreliableMessageBytes = limits.MaxUnreliableMessageBytes,
        };

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Write then move, so the client never reads half a file.
        var temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(contents, Options));
        File.Move(temporary, fullPath, overwrite: true);
    }
}
