using Octokit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using System.Configuration;

namespace bcgovneo4j
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("Started");
            var root = Directory.GetCurrentDirectory();
            var dotenv = ".env";

            // default PhysicalFileProvider will hide DotPrefixed (.env) files, so disable exclusion
            var fileProvider = new PhysicalFileProvider(root, ExclusionFilters.None);

            // later configured sources override values from earlier configured sources
            var configuration =
                new ConfigurationBuilder()
                .SetFileProvider(fileProvider)
                .AddIniFile(dotenv, optional: true)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            try
            {
                var g = new GitData(configuration);
                await g.GetData();
                return 0;
            }
            catch (ConfigurationErrorsException exception)
            {
                OnError(exception, "Configuration Error", -1);
            }
            catch (AuthorizationException exception)
            {
                OnError(exception, "Github Token Error", -2);
            }
            catch (Exception exception)
            {
                OnError(exception, "Error", -2);
            }
        }

        private static int OnError(Exception exception, string label, int errorCode)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  {label}: {exception.Message}");
            return errorCode;

        }
    }
}
