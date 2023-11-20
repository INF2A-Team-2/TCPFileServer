using System.Net;
using Microsoft.Extensions.Configuration;
using TCPFileServer;

static class Program
{
    public static void Main(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        FileServer server = new FileServer(config, IPAddress.Any, 11000);
        server.Start();
    }
}