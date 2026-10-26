using System;
using System.Linq;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// The chat messages under Unity's compile (C# 9, MemoryPack's netstandard2.1 build and its source generator in Unity):
    /// golden bytes written by the server's build (tests/Shared.Tests ChatMessageGoldenTests holds the same strings), so
    /// client and server are proven to speak the same bytes. Round trips and fuzzing run in .NET.
    /// </summary>
    public class ChatMessageTests
    {
        private const string SendHex = "0601f7ffffff080000006869207468657265";
        private const string LineHex = "070304030201fbffffff0400000041726961f4ffffff0800000068c3a96c6c6f20f09f9880";
        private const string RefusedHex = "080104";

        private readonly ReliableMessageWriter _writer = new ReliableMessageWriter();

        private static string ToHex(ReadOnlySpan<byte> bytes) => string.Concat(bytes.ToArray().Select(b => b.ToString("x2")));

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return bytes;
        }

        [Test]
        public void Writes_the_servers_bytes()
        {
            Assert.AreEqual(SendHex, ToHex(ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hi there" })));
            Assert.AreEqual(LineHex, ToHex(ChatCodec.WriteLine(_writer,
                new ChatLineMessage { SenderEntityId = 0x01020304, SenderName = "Aria", Text = "héllo 😀" })));
            Assert.AreEqual(RefusedHex, ToHex(ChatCodec.WriteRefused(_writer,
                new ChatRefusedMessage { Reason = ChatReason.ChatRateLimited })));
        }

        [Test]
        public void Reads_the_servers_bytes()
        {
            Assert.IsTrue(ChatCodec.TryReadSend(Hex(SendHex), out var text));
            Assert.AreEqual("hi there", text);

            Assert.IsTrue(ChatCodec.TryReadLine(Hex(LineHex), out var line));
            Assert.AreEqual(0x01020304u, line.SenderEntityId);
            Assert.AreEqual("Aria", line.SenderName);
            Assert.AreEqual("héllo 😀", line.Text);

            Assert.IsTrue(ChatCodec.TryReadRefused(Hex(RefusedHex), out var reason));
            Assert.AreEqual(ChatReason.ChatRateLimited, reason);
        }

        [Test]
        public void Refuses_a_corrupt_string_header_without_throwing()
        {
            var bytes = Hex(LineHex);
            bytes[6] = 0x00; // the name's byte count header

            Assert.IsFalse(ChatCodec.TryReadLine(bytes, out _));
        }
    }
}
