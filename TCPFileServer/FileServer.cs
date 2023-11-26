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

                SendMessage(stream, GenerateHandshakeResponse(Encoding.UTF8.GetString(buffer, 0, i)));

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
                
                string filePath = Path.Join(_config["DataPath"], $"{fileData.ID}.{FileExtensions[fileData.MimeType]}");

                if (File.Exists(filePath))
                {
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
            
            break;
        }
        
        client.Close();
        Console.WriteLine("Connection closed");
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
        catch (JsonReaderException)
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