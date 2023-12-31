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

    public async Task Start()
    {
        try
        {
            _listener.Start();

            Console.WriteLine($"Listening on {_ipEndPoint.Address}:{_ipEndPoint.Port}");

            while (true)
            {
                Console.WriteLine("Waiting for connection...");

                using TcpClient client = await _listener.AcceptTcpClientAsync();

                Console.WriteLine("Connected");

                await HandleClient(client);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task HandleClient(TcpClient client)
    {
        Console.WriteLine($"Client IP: {client.Client.RemoteEndPoint}");
        
        NetworkStream stream = client.GetStream();

        byte[] buffer = new byte[int.Parse(_config["BufferSize"] ?? "1048576")];
        int bufferOffset = 0;

        bool handshakePassed = false;

        FileData? fileData = null;
        FileStream? fileStream = null;
        int fileProgress = 0;

        Frame? currentFrame = null;

        int i = 0;
        while (true)
        {
            if (bufferOffset == 0)
            {
                i = stream.Read(buffer, bufferOffset,  buffer.Length - bufferOffset);
                Console.WriteLine($"{i} new bytes");
            }
            else
            {
                buffer = RemoveFirst(buffer, bufferOffset);
                i -= bufferOffset;
                
                Console.WriteLine($"{i} old bytes");
            }
            
            if (i == 0)
            {
                continue;
            }
            
            if (!handshakePassed)
            {
                Console.WriteLine("Processing handshake...");
                
                string hs = Encoding.UTF8.GetString(buffer, 0, i);
                
                Console.WriteLine("--- Handshake Request ---");
                Console.WriteLine(hs);
                Console.WriteLine("-------------------------");

                string? res = GenerateHandshakeResponse(hs);

                if (res == null)
                {
                    Console.WriteLine("Invalid handshake");
                    break;
                }

                Console.WriteLine("--- Handshake Response ---");
                Console.WriteLine(res);
                Console.WriteLine("-------------------------");
                
                await SendMessageRaw(stream, res);

                handshakePassed = true;
                
                continue;
            }

            long bytesRemaining;

            if (currentFrame == null || currentFrame.IsComplete)
            {
                (currentFrame, bytesRemaining) = Frame.Parse(buffer, i);
                Console.WriteLine(currentFrame);
            }
            else
            {
                long bytesToAppend = currentFrame.BytesRemaining > i ? i : currentFrame.BytesRemaining;
                byte[] dataToAppend = new byte[bytesToAppend];
                Array.Copy(buffer, 0, dataToAppend, 0, bytesToAppend);
                currentFrame.AppendData(dataToAppend);

                bytesRemaining = bytesToAppend - i;

            }

            if (!currentFrame.IsComplete)
            {
                bufferOffset = 0;
                continue;
            }

            if (bytesRemaining < 0)
            {
                bytesRemaining = long.Abs(bytesRemaining);
                bufferOffset = i - Int64ToInt32(bytesRemaining);
                Console.WriteLine($"{bytesRemaining} extra");
            }
            else
            {
                bufferOffset = 0;
            }

            if (fileData == null)
            {
                Console.WriteLine("Processing file data...");
                
                fileData = ParseFileData(Encoding.UTF8.GetString(currentFrame.Data));

                if (fileData == null)
                {
                    Console.WriteLine("Invalid file data");
                    await SendMessage(stream, Frame.Close(ClosingCode.InvalidData, "Invalid file metadata"));
                    break;
                }
                
                Console.WriteLine("Authenticating user...");
                
                if (!await AuthenticateUser(fileData.ID, fileData.MimeType, fileData.Token))
                {
                    Console.WriteLine("Authentication failed...");
                    await SendMessage(stream, Frame.Close(ClosingCode.InvalidData, "Authentication failed"));
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
                
                string filePath = Path.Join(_config["DataPath"], $"{fileData.ID}.{fileData.Extension}");

                if (File.Exists(filePath))
                {
                    await SendMessage(stream, Frame.Close(ClosingCode.InvalidData, "File already exists"));

                    break;
                }

                fileStream = new FileStream(filePath, FileMode.CreateNew);
            }
            
            fileStream.Write(currentFrame.Data);
            
            fileProgress += currentFrame.Data.Length;
            
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
            
            await SendMessage(stream, Frame.Close(ClosingCode.Normal, "File uploaded successfully"));
            
            break;
        }
        
        client.Close();
        Console.WriteLine("Connection closed");
    }
    
    public static async Task SendMessage(NetworkStream stream, byte[] message) => 
        await stream.WriteAsync(message);
    
    public static async Task SendMessage(NetworkStream stream, Frame frame) =>
        await SendMessage(stream, frame.ToBytes());

    public static async Task SendMessage(NetworkStream stream, string message) =>
        await SendMessage(stream, Frame.Text(message));

    public static async Task SendMessageRaw(NetworkStream stream, string message) =>
        await SendMessage(stream, Encoding.UTF8.GetBytes(message));
    
    public FileData? ParseFileData(string data)
    {
        try
        {
            return JsonConvert.DeserializeObject<FileData>(data);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    public static string? GenerateHandshakeResponse(string request)
    {
        List<string> lines = request.Split("\n").ToList();
        
        string userAgent = lines.First(l => l.StartsWith("User-Agent"))
            .Split(": ")[1]
            .Trim();

        if (userAgent.Contains("Let's Encrypt prevalidation check"))
        {
            return null;
        }
                    
        string key = lines.First(l => l.StartsWith("Sec-WebSocket-Key"))
            .Split(": ")[1]
            .Trim();

        key += "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                    
        key = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key)));

        return "HTTP/1.1 101 Switching Protocols\r\n" +
               "Connection: Upgrade\r\n" +
               "Upgrade: websocket\r\n" +
               $"Sec-WebSocket-Accept: {key}\r\n\r\n";
    }
    
    public async Task<bool> AuthenticateUser(long attachmentId, string mimeType, string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage res = await _httpClient.GetAsync(
            _config["ServerUrl"] + $"/api/attachments/{attachmentId}/authenticate?mimeType={mimeType}");

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
    
    public static int Int64ToInt32(long x)
    {
        if (x < int.MinValue || x > int.MaxValue)
        {
            throw new OverflowException("Value is outside the valid range for int32");
        }

        return (int)x;
    }
    
    public static T[] RemoveFirst<T>(T[] array, int n)
    {
        if (n >= 0 && n <= array.Length)
        {
            T[] modifiedArray = new T[array.Length];

            for (int i = 0; i < array.Length - n; i++)
            {
                modifiedArray[i] = array[i + n];
            }

            return modifiedArray;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(n), "Invalid value of n. It should be between 0 and the length of the array.");
        }
    }
}