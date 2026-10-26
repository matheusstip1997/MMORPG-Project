using System.Diagnostics;
using System.Runtime.InteropServices;
using MemoryPack;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// R-2.5-07 reproduction (local chat review, 2026-10-25): MemoryPack 1.21 trusts a UTF-8 string header. The header
/// <c>0x80000000</c> claims <c>int.MaxValue</c> bytes; adding the 4-byte UTF-16 length overflows MemoryPack's bounds check,
/// so it decodes past the end of the buffer until it hits unmapped memory: an <see cref="AccessViolationException"/> that
/// no catch can stop, so one reliable message from a client would kill the server. A child process proves it (the test
/// host survives); the same bytes through <see cref="ReliableMessageReader"/> are refused before MemoryPack sees them.
/// </summary>
public class MemoryPackCrashReproductionTests
{
    /// <summary>A <see cref="ChatSendMessage"/> body: one member, string header <c>~int.MaxValue</c>, UTF-16 length 0, then "AB".</summary>
    internal static readonly byte[] OverflowingStringBody = { 1, 0x00, 0x00, 0x00, 0x80, 0, 0, 0, 0, (byte)'A', (byte)'B' };

    [Fact]
    public void An_unguarded_read_of_an_overflowing_string_header_kills_the_process()
    {
        var (exitCode, output) = MemoryPackCrashProbe.RunChild(OverflowingStringBody);

        // If a MemoryPack upgrade fixes this, the child returns or throws: the guard stays (it also caps sizes, counts and
        // UTF-8), but update this reproduction and spec 11.2.
        Assert.False(output.Contains(MemoryPackCrashProbe.Survived, StringComparison.Ordinal),
            "MemoryPack no longer crashes on this header:\n" + output);
        Assert.True(output.Contains(MemoryPackCrashProbe.Started, StringComparison.Ordinal), "The probe did not start:\n" + output);
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void The_guarded_reader_refuses_the_same_bytes_without_calling_MemoryPack()
    {
        var message = new byte[1 + OverflowingStringBody.Length];
        message[0] = (byte)ReliableMessageId.ChatSend;
        OverflowingStringBody.CopyTo(message, 1);
        ChatSendMessage? value = null;

        Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue));
        Assert.False(ReliableMessageReader.TryRead(message, ReliableMessageId.ChatSend, ref value));
        Assert.False(ChatCodec.TryReadSend(message, out _));
        Assert.Null(value);
    }
}

/// <summary>The child side of <see cref="MemoryPackCrashReproductionTests"/>: deserializes a body with no guard at all.</summary>
internal static class MemoryPackCrashProbe
{
    public const string Verb = "memorypack-crash-probe";
    public const string Started = "probe: deserializing";
    public const string Survived = "probe: survived";

    private const int ChildTimeoutMs = 60_000;

    public static (int ExitCode, string Output) RunChild(byte[] body)
    {
        var assembly = typeof(MemoryPackCrashProbe).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(Verb);
        start.ArgumentList.Add(Convert.ToHexString(body));
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the probe.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(ChildTimeoutMs))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("The probe did not exit.");
        }
        return (child.ExitCode, stdout.Result + stderr.Result);
    }

    public static int Run(string hex)
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox); // no crash dialog for the expected access violation
        var body = Convert.FromHexString(hex);
        Console.WriteLine(Started);
        Console.Out.Flush();
        try
        {
            ChatSendMessage? message = null;
            MemoryPackSerializer.Deserialize(body, ref message, MemoryPackSerializerOptions.Utf8);
            Console.WriteLine($"{Survived}: returned");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{Survived}: threw {e.GetType().Name}");
        }
        return 0;
    }

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
