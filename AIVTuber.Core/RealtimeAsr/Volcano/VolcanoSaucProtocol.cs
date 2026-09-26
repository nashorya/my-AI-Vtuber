using System.IO.Compression;
using System.Text;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// Byte-level codec for the 豆包双向流式 ASR (sauc bigmodel_async) binary framing, per the
/// documented [D4] semantics:
///
/// - 4-byte default header: (protocolVersion&lt;&lt;4 | headerSize), (messageType&lt;&lt;4 |
///   messageFlags), (serialization&lt;&lt;4 | compression), reserved.
/// - Full client requests: header + 4-byte big-endian sequence + 4-byte big-endian payload
///   size + payload (JSON, gzip when compression=1).
/// - Audio-only client requests: header + raw audio payload (no sequence/size when flags=0).
///   messageFlags: 0 = no sequence, 1 = positive sequence (more to follow),
///   2 = negative sequence (last packet of the audio stream).
/// - Full server responses: header + optional 4-byte sequence + 4-byte payload size +
///   (possibly gzipped) JSON payload, mirroring the request flags.
///
/// 未实测 against the live endpoint; encode/decode is verified by round-trip fixtures.
/// </summary>
public static class VolcanoSaucProtocol
{
    public const byte ProtocolVersion = 0x1;
    public const byte HeaderSizeUnits = 0x1; // 1 unit = 4 bytes

    public const byte MessageTypeFullClientRequest = 0x1;
    public const byte MessageTypeAudioOnlyRequest = 0x2;
    public const byte MessageTypeFullServerResponse = 0x9;
    public const byte MessageTypeError = 0xF;

    public const byte MessageFlagNone = 0x0;        // no sequence
    public const byte MessageFlagPositiveSequence = 0x1; // more packets follow
    public const byte MessageFlagLastPacket = 0x2;  // negative sequence: final packet

    public const byte SerializationNone = 0x0;
    public const byte SerializationJson = 0x1;
    public const byte CompressionNone = 0x0;
    public const byte CompressionGzip = 0x1;

    public static byte[] EncodeHeader(byte messageType, byte messageFlags, byte serialization, byte compression)
    {
        return
        [
            (byte)((ProtocolVersion << 4) | HeaderSizeUnits),
            (byte)((messageType << 4) | messageFlags),
            (byte)((serialization << 4) | compression),
            0x00,
        ];
    }

    /// <summary>Full client request: header + sequence + payload size + payload (compressed when requested).</summary>
    public static byte[] EncodeFullClientRequest(
        byte messageType, byte messageFlags, uint sequence, ReadOnlySpan<byte> payload,
        byte serialization = SerializationJson, byte compression = CompressionGzip)
    {
        var body = compression == CompressionGzip ? Gzip(payload.ToArray()) : payload.ToArray();
        var header = EncodeHeader(messageType, messageFlags, serialization, compression);
        var packet = new byte[header.Length + 4 + 4 + body.Length];
        header.CopyTo(packet, 0);
        WriteUInt32BE(packet, header.Length, sequence);
        WriteUInt32BE(packet, header.Length + 4, (uint)body.Length);
        body.CopyTo(packet, header.Length + 8);
        return packet;
    }

    /// <summary>Audio-only request. With <paramref name="messageFlags"/> = PositiveSequence a
    /// 4-byte big-endian sequence precedes the raw audio; with None the payload is bare.</summary>
    public static byte[] EncodeAudioOnlyRequest(byte messageFlags, uint sequence, ReadOnlySpan<byte> audio)
    {
        var header = EncodeHeader(MessageTypeAudioOnlyRequest, messageFlags, SerializationNone, CompressionNone);
        if (messageFlags == MessageFlagNone)
        {
            var packet = new byte[header.Length + audio.Length];
            header.CopyTo(packet, 0);
            audio.CopyTo(packet.AsSpan(header.Length));
            return packet;
        }
        else
        {
            var packet = new byte[header.Length + 4 + audio.Length];
            header.CopyTo(packet, 0);
            WriteUInt32BE(packet, header.Length, sequence);
            audio.CopyTo(packet.AsSpan(header.Length + 4));
            return packet;
        }
    }

    public readonly record struct DecodedMessage(
        byte MessageType,
        byte MessageFlags,
        byte Serialization,
        byte Compression,
        uint Sequence,
        byte[] Payload);

    /// <summary>Decodes a full server response (or any sequenced message).</summary>
    public static DecodedMessage Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
            throw new InvalidDataException("火山SAUC包过短（<4字节头）");
        var messageType = (byte)(packet[1] >> 4);
        var messageFlags = (byte)(packet[1] & 0x0F);
        var serialization = (byte)(packet[2] >> 4);
        var compression = (byte)(packet[2] & 0x0F);
        uint sequence = 0;
        var offset = 4;
        if (messageFlags != MessageFlagNone)
        {
            if (packet.Length < 8) throw new InvalidDataException("火山SAUC带序号包缺少序号字段");
            sequence = ReadUInt32BE(packet, 4);
            offset = 8;
        }
        byte[] payload;
        if (messageType is MessageTypeFullClientRequest or MessageTypeFullServerResponse)
        {
            if (packet.Length < offset + 4) throw new InvalidDataException("火山SAUC全量包缺少payload长度");
            var size = (int)ReadUInt32BE(packet, offset);
            offset += 4;
            if (packet.Length < offset + size) throw new InvalidDataException("火山SAUCpayload不完整");
            payload = packet.Slice(offset, size).ToArray();
        }
        else
        {
            payload = packet.Slice(offset).ToArray();
        }
        if (compression == CompressionGzip)
            payload = Gunzip(payload);
        return new DecodedMessage(messageType, messageFlags, serialization, compression, sequence, payload);
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> buffer, int offset)
        => (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);

    internal static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    internal static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
