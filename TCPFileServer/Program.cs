using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using TCPFileServer;

static class Program
{
    public static readonly Dictionary<string, string> FileExtensions = new Dictionary<string, string>()
    {
        { "image/png",  "png"   },
        { "image/jpeg", "jpeg"  },
        { "image/jpg",  "jpg"   },
        { "video/mp4",  "mp4"   }
    };

    public static IConfiguration? Config;

    public static void Main(string[] args)
    {
        Config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();
        
        IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Any, 11000);
        TcpListener listener = new TcpListener(ipEndPoint);

        try
        {
            listener.Start();
            
            Console.WriteLine($"Listening on {ipEndPoint.Address}:{ipEndPoint.Port}");

            byte[] buffer = new byte[int.Parse(Config["BufferSize"] ?? "1048576")];
            string? data;
            FileData? fileData;

            FileStream? fs = null;

            byte[] msg;

            while (true)
            {
                Console.WriteLine("Waiting for connection...");

                using TcpClient client = listener.AcceptTcpClient();
            
                Console.WriteLine("Connected");

                NetworkStream stream = client.GetStream();

                fileData = null;

                bool failed = false;

                long total = 0;
                
                int i;

                while ((i = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (fileData == null)
                    {
                        data = Encoding.ASCII.GetString(buffer, 0, i);
                        
                        try
                        {
                            fileData = JsonConvert.DeserializeObject<FileData>(data);
                        }
                        catch (JsonReaderException e)
                        {
                            Console.WriteLine("Invalid data, closing connection...");
                        
                            msg = "error_json"u8.ToArray();
                            stream.Write(msg, 0, msg.Length);

                            failed = true;
                            
                            break;
                        }

                        if (fileData == null)
                        {
                            Console.WriteLine("Invalid data, closing connection...");
                        
                            msg = "error_json_invalid"u8.ToArray();
                            stream.Write(msg, 0, msg.Length);
                            
                            failed = true;

                            break;
                        }

                        (double size, string id) fSize = ReduceSize(fileData.Size);
                        
                        Console.WriteLine("Receiving file");
                        Console.WriteLine($"ID: {fileData.ID}");
                        Console.WriteLine($"MimeType: {fileData.MimeType}");
                        Console.WriteLine($"Size: {fSize.size} {fSize.id}");

                        if (!Directory.Exists(Config["DataPath"]))
                        {
                            Directory.CreateDirectory(Config["DataPath"] ?? "");
                        }
                        
                        string path = Path.Join(Config["DataPath"], $"{fileData.ID}.{FileExtensions[fileData.MimeType]}");
                        
                        if (File.Exists(path))
                        {
                            Console.WriteLine("File already exists, closing connection...");
                        
                            msg = "error_file_exists"u8.ToArray();
                            stream.Write(msg, 0, msg.Length);
                            
                            failed = true;
                            
                            break;
                        }
                        
                        fs = new FileStream(path, FileMode.CreateNew);
                        
                        continue;
                    }

                    if (fs == null)
                    {
                        continue;
                    }
                    
                    total += i;
                    double p = Math.Round((double)total / fileData.Size * 100);
                    
                    fs.Write(buffer, 0, i);
                    
                    (double size, string id) pSize = ReduceSize(total);
                    (double size, string id) tSize = ReduceSize(fileData.Size);

                    Console.WriteLine($"Writing data: {pSize.size} {pSize.id} / {tSize.size} {tSize.id} | {p}% {ProgressBar(p)}");

                    if (total == fileData.Size)
                    {
                        break;
                    }
                }

                if (failed)
                {
                    continue;
                }
                
                Console.WriteLine("Finished writing data");

                msg = "success"u8.ToArray();
                    
                stream.Write(msg, 0, msg.Length);
                    
                Console.WriteLine("Transaction completed");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            listener.Stop();
        }
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