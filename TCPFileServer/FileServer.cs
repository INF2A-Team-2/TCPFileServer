using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
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

        while (true)
        {
            int i = stream.Read(buffer, 0, buffer.Length);
            
            Console.WriteLine($"Bytes received: {i}");

            string s = Encoding.UTF8.GetString(buffer, 0, i);
            
            if (!handshakePassed)
            {
                Console.WriteLine("Processing handshake...");
                
                SendMessage(stream, GenerateHandshakeResponse(s));

                handshakePassed = true;
                
                continue;
            }

            byte[] data = ParseFrame(buffer);
            
            data.ToList().ForEach(b => Console.Write($"{b} "));
            Console.WriteLine();
            Console.WriteLine($"Payload length: {data.Length}");

            string textData = Encoding.UTF8.GetString(data, 0, data.Length);

            if (fileData == null)
            {
                Console.WriteLine(textData);
                Console.WriteLine("Processing file data...");

                fileData = ParseFileData(textData);

                if (fileData == null)
                {
                    Console.WriteLine("Invalid file data");
                    break;
                }
                
                continue;
            }
            
            (double size, string id) fSize = ReduceSize(fileData.Size);
            
            Console.WriteLine($"ID: {fileData.ID}");
            Console.WriteLine($"MimeType: {fileData.MimeType}");
            Console.WriteLine($"Size: {fSize.size} {fSize.id}");

            if (fileStream == null)
            {
                Console.WriteLine("Setting up file stream...");

                if (_config["DataPath"] != null && !Directory.Exists(_config["DataPath"]))
                {
                    Directory.CreateDirectory(_config["DataPath"] ?? "");
                }

                string path = Path.Join(_config["DataPath"], $"{fileData.ID}.{FileExtensions[fileData.MimeType]}");

                if (File.Exists(path))
                {
                    break;
                }

                fileStream = new FileStream(path, FileMode.CreateNew);

                continue;
            }
            
            fileStream.Write(data, 0, data.Length);

            fileProgress += i;
            
            double p = Math.Round((double)fileProgress / fileData.Size * 100);
            
            (double size, string id) pSize = ReduceSize(fileProgress);
            (double size, string id) tSize = ReduceSize(fileData.Size);

            Console.WriteLine($"Writing data: {pSize.size} {pSize.id} / {tSize.size} {tSize.id} | {p}% {ProgressBar(p)}");

            if (fileProgress != fileData.Size)
            {
                continue;
            }
            
            Console.WriteLine("Finished writing data");
            break;
        }
        
        Console.WriteLine("Connection closed");
        client.Close();
    }

    public static byte[] ParseFrame(byte[] frame)
    {
        byte fin = (byte)((frame[0] & 0b10000000) >> 7);
        byte opcode = (byte)(frame[0] & 0b00001111);
        
        byte mask = (byte)((frame[1] & 0b10000000) >> 7);
        long payloadLength = frame[1] & 0b01111111;
        
        int byteOffset = 2;

        if (payloadLength == 126)
        {
            payloadLength = BinaryPrimitives.ReadInt64BigEndian(frame.Skip(byteOffset).Take(2).ToArray());
            byteOffset = 4;
        } 
        else if (payloadLength == 127)
        {
            payloadLength = BinaryPrimitives.ReadInt64BigEndian(frame.Skip(byteOffset).Take(8).ToArray());
            byteOffset = 10;
        }
        
        if (mask == 1)
        {
            byte[] maskingKey = frame.Skip(byteOffset).Take(4).ToArray();

            byteOffset += 4;

            for (long i = 0; i < payloadLength; i++)
            {
                frame[byteOffset + i] ^= maskingKey[1 % 4];
            }
        }

        byte[] data = new byte[payloadLength];
        Array.Copy(frame, byteOffset, data, 0, payloadLength);

        return data;
    }
    
    public static void SendMessage(NetworkStream stream, byte[] message)
    {
        stream.Write(message, 0, message.Length);
    }

    public static void SendMessage(NetworkStream stream, string message) =>
        SendMessage(stream, Encoding.UTF8.GetBytes(message));

    public static byte[] ConstructMessage(byte[] data)
    {
        byte metadata = 0b00000010;
        byte[] payloadLen = GetPayloadLengthBytes(data.Length);

        byte[] result = new byte[data.Length + payloadLen.Length + 1];
        result[0] = metadata;
        payloadLen.CopyTo(result, 1);
        data.CopyTo(result, payloadLen.Length + 1);

        return result;
    }

    public static byte[] GetPayloadLengthBytes(long length) => length switch
    {
        <= 125 => new byte[] { (byte)length },
        <= 32767 => new byte[] { 0b01111110 }.Concat(BitConverter.GetBytes((ushort)length)).ToArray(),
        _ => new byte[] { 0b01111111 }.Concat(BitConverter.GetBytes(length)).ToArray()
    };

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
                    
        string key = lines.First(l => l.StartsWith("Sec-WebSocket-Key")).Split(": ")[1].Trim();

        key += "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                    
        key = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
                    
        string extensions = lines.First(l => l.StartsWith("Sec-WebSocket-Extensions")).Split(": ")[1].Trim() + "=15";
                    
        return "HTTP/1.1 101 Switching Protocols\r\n" +
               "Connection: Upgrade\r\n" +
               "Upgrade: websocket\r\n" +
                $"Sec-WebSocket-Accept: {key}\r\n" +
                $"Sec-WebSocket-Extensions: {extensions}\r\n\r\n";
    }
    
    public bool AuthenticateUser(long attachmentId, string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage res = _httpClient.GetAsync(
            _config["ServerUrl"] + $"/api/issues/attachments/{attachmentId}/authenticate").Result;

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