using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FifineCaptureStudio.Core.Streaming.Nixani;

// Wire format re-implemented from the Nixani Engine protocol description (PROTOCOL.md,
// protocol interface MIT-licensed, engine AGPL-3.0). The engine is a separate executable:
// FIFINE talks to it over stdin/stdout and never links its code.

/// <summary>Builds one engine message: u32 size (type + payload), u16 type, payload.</summary>
internal sealed class NxeWriter
{
    private readonly MemoryStream _payload = new(256);
    private readonly BinaryWriter _writer;

    public NxeWriter(ushort type)
    {
        _writer = new BinaryWriter(_payload, Encoding.UTF8, leaveOpen: true);
        _writer.Write(type);
    }

    public NxeWriter U8(byte value) { _writer.Write(value); return this; }
    public NxeWriter U16(ushort value) { _writer.Write(value); return this; }
    public NxeWriter I16(short value) { _writer.Write(value); return this; }
    public NxeWriter I32(int value) { _writer.Write(value); return this; }
    public NxeWriter U32(uint value) { _writer.Write(value); return this; }
    public NxeWriter U64(ulong value) { _writer.Write(value); return this; }
    public NxeWriter F32(float value) { _writer.Write(value); return this; }

    public NxeWriter Bytes(ReadOnlySpan<byte> value)
    {
        _writer.Write(value);
        return this;
    }

    public NxeWriter Str(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        _writer.Write((uint)bytes.Length);
        _writer.Write(bytes);
        return this;
    }

    public byte[] ToArray()
    {
        _writer.Flush();
        var body = _payload.ToArray();
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        Buffer.BlockCopy(body, 0, frame, 4, body.Length);
        return frame;
    }
}

/// <summary>Reads the payload of one engine message.</summary>
internal ref struct NxeReader
{
    private readonly ReadOnlySpan<byte> _payload;
    private int _position;

    public NxeReader(ReadOnlySpan<byte> payload)
    {
        _payload = payload;
        _position = 0;
    }

    public byte U8() => _payload[_position++];

    public ushort U16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_payload[_position..]);
        _position += 2;
        return value;
    }

    public short I16()
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(_payload[_position..]);
        _position += 2;
        return value;
    }

    public uint U32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_position..]);
        _position += 4;
        return value;
    }

    public int I32()
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(_payload[_position..]);
        _position += 4;
        return value;
    }

    public ulong U64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_payload[_position..]);
        _position += 8;
        return value;
    }

    public float F32()
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(_payload[_position..]);
        _position += 4;
        return value;
    }

    public byte[] Bytes(int count)
    {
        var value = _payload.Slice(_position, count).ToArray();
        _position += count;
        return value;
    }

    public string Str()
    {
        var length = (int)U32();
        if (length <= 0)
        {
            return string.Empty;
        }

        var value = Encoding.UTF8.GetString(_payload.Slice(_position, length));
        _position += length;
        return value;
    }

    /// <summary>Everything left in the frame (the spec's <c>blob</c>).</summary>
    public byte[] Blob() => _payload[_position..].ToArray();
}

internal static class NxeMessage
{
    // client -> engine
    public const ushort Hello = 1;
    public const ushort DiscoveryStart = 10;
    public const ushort DiscoveryStop = 11;
    public const ushort Wake = 12;
    public const ushort RegistStart = 20;
    public const ushort RegistCancel = 21;
    public const ushort RegistPsn = 22;
    public const ushort SessionStart = 40;
    public const ushort SessionStop = 41;
    public const ushort Pad = 42;
    public const ushort LoginPin = 43;
    public const ushort RequestKeyframe = 44;

    // engine -> client
    public const ushort HelloReply = 101;
    public const ushort Log = 102;
    public const ushort Hosts = 110;
    public const ushort RegistResult = 120;
    public const ushort RegistProgress = 121;
    public const ushort SessionState = 140;
    public const ushort Video = 141;
    public const ushort AudioFormat = 142;
    public const ushort Audio = 143;
    public const ushort Haptics = 144;
    public const ushort Rumble = 145;
    public const ushort LoginPinRequest = 148;
    public const ushort Nickname = 149;
    public const ushort CantDisplay = 150;
    public const ushort Stats = 151;
    public const ushort SessionStopped = 152;

    public const uint ProtocolVersion = 3;

    /// <summary>Largest frame accepted from the engine; a 4K intra frame is well under this.</summary>
    public const int MaxFrameBytes = 32 * 1024 * 1024;
}
