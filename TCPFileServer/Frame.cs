using System.Buffers.Binary;

namespace TCPFileServer;

public enum Opcode
{
    Continuation = 0,
    Text = 1,
    Binary = 2,
    Close = 8,
    Ping = 9,
    Pong = 10,
}

public class Frame
{
    public bool Fin { get; }
    public bool Rsv1 { get; }
    public bool Rsv2 { get; }
    public bool Rsv3 { get; }
    public Opcode Opcode { get; }
    public long PayloadLength { get; }
    public byte[] Mask { get; }
    public byte[] Data { get; private set; }

    public bool HasMask => Mask.Length > 0;
    public bool IsComplete => PayloadLength == Data.Length;
    public long BytesRemaining => PayloadLength - Data.Length;
    public bool IsFragment => !Fin || Opcode == Opcode.Continuation;

    public Frame(byte fin, byte rsv1, byte rsv2, byte rsv3, int opcode, long payloadLength, byte[] mask)
    {
        this.Fin = fin == 1;
        this.Rsv1 = rsv1 == 1;
        this.Rsv2 = rsv2 == 1;
        this.Rsv3 = rsv3 == 1;
        this.Opcode = (Opcode)opcode;
        this.PayloadLength = payloadLength;
        this.Mask = mask;
        this.Data = Array.Empty<byte>();
    }

    public void AppendData(byte[] data)
    {
        if (PayloadLength - Data.Length < data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Data length can't exceed payload length");
        }

        Data = Data.Concat(data).ToArray();

        if (IsComplete)
        {
            UnmaskData();
        }
    }
    
    private void UnmaskData()
    {
        if (!HasMask)
        {
            return;
        }
        
        if (!IsComplete)
        {
            throw new Exception("Can't unmask the data before the whole payload is complete");
        }

        for (long i = 0; i < Data.Length; i++)
        {
            Data[i] ^= Mask[i % Mask.Length];
        }
    }

    public override string ToString() => $"""
                                         FIN: {Fin}
                                         RSV1: {Rsv1}
                                         RSV2: {Rsv2}
                                         RSV3: {Rsv3}
                                         Opcode: {(int)Opcode} ({Opcode})
                                         Mask: {HasMask}
                                         Payload Length: {PayloadLength:N0}
                                         Data Length: {Data.Length:N0}
                                         """;

    public static (Frame frame, long bytesRemaining) Parse(byte[] data, int length)
    {
        byte fin = (byte)((data[0] & 0b10000000) >> 7);
        byte rsv1 = (byte)((data[0] & 0b01000000) >> 6);
        byte rsv2 = (byte)((data[0] & 0b00100000) >> 5);
        byte rsv3 = (byte)((data[0] & 0b00010000) >> 4);
        byte opcode = (byte)(data[0] & 0b00001111);
        
        byte mask = (byte)((data[1] & 0b10000000) >> 7);
        long payloadLength = data[1] & 0b01111111;
        
        int byteOffset = 2;

        switch (payloadLength)
        {
            case 126:
                payloadLength = BinaryPrimitives.ReadInt16BigEndian(data.Skip(byteOffset).Take(2).ToArray());
                byteOffset = 4;
                break;
            case 127:
                payloadLength = BinaryPrimitives.ReadInt64BigEndian(data.Skip(byteOffset).Take(8).ToArray());
                byteOffset = 10;
                break;
        }

        byte[] maskingKey = Array.Empty<byte>();
        
        if (mask == 1)
        {
            maskingKey = data.Skip(byteOffset).Take(4).ToArray();

            byteOffset += 4;
        }
        Console.WriteLine($"{length} - {byteOffset}");
        Console.WriteLine(payloadLength);
        byte[] payload = new byte[length - byteOffset > payloadLength ? payloadLength : length - byteOffset];
        Array.Copy(data, byteOffset, payload, 0, payload.Length);

        Frame frame = new Frame(fin, rsv1, rsv2, rsv3, opcode, payloadLength, maskingKey);
        frame.AppendData(payload);

        return (frame, payloadLength - (length - byteOffset));
    }
}