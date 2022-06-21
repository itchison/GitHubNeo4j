using Octokit;
using Microsoft.Extensions.Configuration;

namespace bcgovneo4j
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Started");
            var root = Directory.GetCurrentDirectory();
            var dotenv = Path.Combine(root, ".env");
            DotEnv.Load(dotenv);

            var config =
                new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var g = new GitData();
            g.GetData().Wait();
        }



    }
}