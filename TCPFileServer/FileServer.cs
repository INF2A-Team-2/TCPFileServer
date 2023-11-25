using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace TCPFileServer;

public class FileServer
{
    public static readonly Dictionary<string, string> FileExtensions = new Dictionary<string, string>()
    {
        { "image/png", "png" },
        { "image/jpeg", "jpeg" },
        { "image/jpg", "jpg" },
        { "video/mp4", "mp4" }
    };

    private readonly IConfiguration _config;

    private readonly IPEndPoint _ipEndPoint;
    private readonly TcpListener _listener;

    private readonly HttpClient _httpClient;

    public FileServer(IConfiguration config, IPAddress ip, int port)
    {
        this._config = config;
        this._ipEndPoint = new IPEndPoint(ip, port);
        this._listener = new TcpListener(this._ipEndPoint);
        this._httpClient = new HttpClient();
    }

    public void Start()
    {
        try
        {
            _listener.Start();

            Console.WriteLine($"Listening on {_ipEndPoint.Address}:{_ipEndPoint.Port}");

            while (true)
            {
                Console.WriteLine("Waiting for connection...");

                using TcpClient client = _listener.AcceptTcpClient();

                Console.WriteLine("Connected");

                HandleClient(client);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void HandleClient(TcpClient client)
    {
        Console.WriteLine($"Client IP: {client.Client.RemoteEndPoint}");
        
        NetworkStream stream = client.GetStream();

        byte[] buffer = new byte[int.Parse(_config["BufferSize"] ?? "1048576")];

        bool handshakePassed = false;
        FileData? fileData = null;
        FileStream? fileStream = null;
        long fileProgress = 0;
        string filePath = null!;
        byte[] data;
        byte[] mask = Array.Empty<byte>();
        int lastMaskIndex = 3;
        long fragmentBytesRemaining = 0;

        while (true)
        {
            int i = stream.Read(buffer, 0,  buffer.Length);

            if (i == 0)
            {
                continue;
            }
            
            if (!handshakePassed)
            {
                Console.WriteLine("Processing handshake...");

                SendMessage(stream, GenerateHandshakeResponse(Encoding.UTF8.GetString(buffer, 0, i)));

                handshakePassed = true;
                
                continue;
            }

            if (fragmentBytesRemaining > 0)
            {
                long bytesToRead = fragmentBytesRemaining;
                
                if (fragmentBytesRemaining > i)
                {
                    bytesToRead = i;
                    fragmentBytesRemaining -= i;
                }
                else
                {
                    fragmentBytesRemaining = 0;
                }

                data = new byte[bytesToRead];
                Array.Copy(buffer, 0, data, 0, bytesToRead);
                Console.WriteLine("BP 1");
                if (mask.Length == 4)
                {
                    (byte[] d, int lmi) = UnmaskData(data, mask, 0, lastMaskIndex);
                    data = d;
                    lastMaskIndex = lmi;
                }
            }
            else
            {
                (byte[] d, byte[] m, int lmi, long payloadLength) = ParseFrame(buffer.Take(i).ToArray(), i);
                Console.WriteLine("BP 2");
                data = d;
                mask = m;
                lastMaskIndex = lmi;

                if (payloadLength > i)
                {
                    fragmentBytesRemaining = payloadLength - i;
                }
            }
            
            if (fileData == null)
            {
                Console.WriteLine("Processing file data...");
                
                Console.WriteLine(Encoding.UTF8.GetString(data, 0, data.Length));
                
                fileData = ParseFileData(Encoding.UTF8.GetString(data, 0, data.Length));

                if (fileData == null)
                {
                    Console.WriteLine("Invalid file data");
                    break;
                }
                
                Console.WriteLine("Authenticating user...");
                
                if (!AuthenticateUser(fileData.ID, fileData.MimeType, fileData.Token))
                {
                    Console.WriteLine("Authentication failed...");
                    break;
                }
                
                Console.WriteLine("Authentication successful");
                
                (double size, string id) fSize = ReduceSize(fileData.Size);
            
                Console.WriteLine($"ID: {fileData.ID}");
                Console.WriteLine($"MimeType: {fileData.MimeType}");
                Console.WriteLine($"Size: {fSize.size} {fSize.id}");
                
                continue;
            }

            if (fileStream == null)
            {
                Console.WriteLine("Setting up file stream...");

                if (_config["DataPath"] != null && !Directory.Exists(_config["DataPath"]))
                {
                    Directory.CreateDirectory(_config["DataPath"] ?? "");
                }

                filePath = Path.Join(_config["DataPath"], $"{fileData.ID}.{FileExtensions[fileData.MimeType]}");

                if (File.Exists(filePath))
                {
                    break;
                }

                fileStream = new FileStream(filePath, FileMode.CreateNew);
            }
            
            fileStream.Write(data, 0,  data.Length);
            
            fileProgress += i;
            
            double p = Math.Round((double)fileProgress / fileData.Size * 100);
            
            (double size, string id) pSize = ReduceSize(fileProgress);
            (double size, string id) tSize = ReduceSize(fileData.Size);

            Console.WriteLine($"Writing data: {pSize.size} {pSize.id} / {tSize.size} {tSize.id} | {p}% {ProgressBar(p)}");
            
            if (fileProgress < fileData.Size)
            {
                continue;
            }
            
            fileStream.Close();
            
            Console.WriteLine("Finished writing data");
            
            break;
        }
        
        Console.WriteLine("Connection closed");
        client.Close();
    }

    public static (byte[]data, byte[] mask, int lastMaskIndex, long payloadLength) ParseFrame(byte[] frame, int length)
    {
        byte fin = (byte)((frame[0] & 0b10000000) >> 7);
        byte rsv1 = (byte)((frame[0] & 0b01000000) >> 6);
        byte rsv2 = (byte)((frame[0] & 0b00100000) >> 5);
        byte rsv3 = (byte)((frame[0] & 0b00010000) >> 4);
        byte opcode = (byte)(frame[0] & 0b00001111);
        
        byte mask = (byte)((frame[1] & 0b10000000) >> 7);
        long payloadLength = frame[1] & 0b01111111;
        
        Console.WriteLine("--- Frame ---");
        Console.WriteLine($"FIN: {fin}");
        Console.WriteLine($"RSV1: {rsv1}");
        Console.WriteLine($"RSV2: {rsv2}");
        Console.WriteLine($"RSV3: {rsv3}");
        Console.WriteLine($"OPCODE: {opcode}");
        Console.WriteLine($"MASK: {mask}");
        
        int byteOffset = 2;

        if (payloadLength == 126)
        {
            payloadLength = BinaryPrimitives.ReadInt16BigEndian(frame.Skip(byteOffset).Take(2).ToArray());
            byteOffset = 4;
        } 
        else if (payloadLength == 127)
        {
            payloadLength = BinaryPrimitives.ReadInt64BigEndian(frame.Skip(byteOffset).Take(8).ToArray());
            byteOffset = 10;
        }
        
        Console.WriteLine($"PAYLOAD LENGTH: {payloadLength}");
        Console.WriteLine("-------------");

        byte[] maskingKey = Array.Empty<byte>();
        int lastMaskIndex = 0;
        
        if (mask == 1)
        {
            maskingKey = frame.Skip(byteOffset).Take(4).ToArray();

            byteOffset += 4;

            (byte[] unmaskedData, int lmi) = UnmaskData(frame, maskingKey, byteOffset);
            frame = unmaskedData;
            lastMaskIndex = lmi;
        }
        
        byte[] data = new byte[length - byteOffset];
        Array.Copy(frame, byteOffset, data, 0, length - byteOffset);

        return (data, maskingKey, lastMaskIndex, payloadLength);
    }

    public static (byte[] data, int lastMaskIndex) UnmaskData(byte[] data, byte[] mask, int offset = 0, int lastMaskIndex = 3)
    {
        int mi = lastMaskIndex + 1;
        for (long i = 0; i < data.Length - offset; i++)
        {
            mi %= 4;
            data[offset + i] ^= mask[mi];
            mi++;
        }
        
        return (data, mi - 1);
    }
    
    public static void SendMessage(NetworkStream stream, byte[] message)
    {
        stream.Write(message, 0, message.Length);
    }

    public static void SendMessage(NetworkStream stream, string message) =>
        SendMessage(stream, Encoding.UTF8.GetBytes(message));
    
    public FileData? ParseFileData(string data)
    {
        try
        {
            return JsonConvert.DeserializeObject<FileData>(data);
        }
        catch (JsonReaderException e)
        {
            return null;
        }
    }

    public static string GenerateHandshakeResponse(string request)
    {
        List<string> lines = request.Split("\n").ToList();
                    
        string key = lines.First(l => l.StartsWith("Sec-WebSocket-Key"))
            .Split(": ")[1]
            .Trim();

        key += "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                    
        key = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        
        string extensions = lines.First(l => l.StartsWith("Sec-WebSocket-Extensions"))
            .Split(": ")[1]
            .Split(";")[0]
            .Trim();

        return "HTTP/1.1 101 Switching Protocols\r\n" +
               "Connection: Upgrade\r\n" +
               "Upgrade: websocket\r\n" +
               $"Sec-WebSocket-Accept: {key}\r\n\r\n";
    }
    
    public bool AuthenticateUser(long attachmentId, string mimeType, string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage res = _httpClient.GetAsync(
            _config["ServerUrl"] + $"/api/issues/attachments/{attachmentId}/authenticate?mimeType={mimeType}").Result;

        return res.StatusCode == HttpStatusCode.OK;
    }
    
    public static (double size, string identifier) ReduceSize(double bytes)
    {
        string[] sizes = new[] { "B", "KB", "MB", "GB", "TB"};

        double size = bytes;

        for (int i = 0; i < sizes.Length; i++)
        {
            if (size < 1000 || i == sizes.Length - 1)
            {
                return (Math.Round(size, 2), sizes[i]);
            }
            
            size /= 1000;
        }

        return (bytes, sizes[0]);
    }

    public static string ProgressBar(double p) => 
        string.Join("", Enumerable.Range(1, 10).Select(x => p >= x * 10 ? "\u2588" : "\u2591"));
}