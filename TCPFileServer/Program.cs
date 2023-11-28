using System.Net;
using Microsoft.Extensions.Configuration;
using TCPFileServer;

static class Program
{
    public static async Task Main(string[] args)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        FileServer server = new FileServer(config, IPAddress.Any, 11000);
        await server.Start();
    }
}