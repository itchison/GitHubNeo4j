using Octokit;
using Microsoft.Extensions.Configuration;

namespace bcgovneo4j
{
    class Program
    {
        // neo4j password - cement-gregory-fiesta-invite-telex-6617

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