using Octokit;
using Neo4j.Driver;
using Microsoft.Extensions.Configuration;
using System.Configuration;

namespace bcgovneo4j
{
    /// <summary>
    /// Loads Github data into Neo4j graph database
    /// </summary>
    public class GitData
    {
        private readonly IConfiguration _configuration;
        private readonly GitHubClient github;

        /// <summary>
        /// Initializes a new instance of a GitData.
        /// </summary>
        /// <param name="configuration"></param>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
        public GitData(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _configuration = configuration;
            github = GetGitHubClient(configuration);
        }

        public async Task GetData()
        {
            await GetRates();
            
            var organization = await github.Organization.Get("bcgov");
            var members = await github.Organization.Member.GetAll("bcgov");

            var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var repositories = await github.Repository.GetAllForOrg("bcgov");
            Console.WriteLine($"{members.Count} are a member of bcgov and has {repositories.Count} repos");
            await Throttle();

            using IDriver driver = CreateDriver();
            using (var session = driver.AsyncSession())
            {
                await WipeAllDate(driver);
                await session.WriteTransactionAsync(async tx =>
                {
                    await tx.RunAsync($"CREATE (bcgov:Org {{name:'{organization.Name}', url: '{organization.Url}', id: '{organization.Id}'}})");
                });

                Console.WriteLine($"Iterating members - {members.Count}");
                foreach (var member in members)
                {
                    if (!member.IsBot())
                    {
                        Console.WriteLine($"Member #{users.Count}");
                        await session.WriteTransactionAsync(async tx =>
                        {
                            await tx.RunAsync($"CREATE (member:Person {{name:'{member.Login}', Id:'{member.Id}', Login: '{member.Login}'}})");
                            await tx.RunAsync($"MATCH (a:Person), (b:Org) WHERE a.Id = '{member.Id}' AND b.name = '{organization.Name}' CREATE (a)-[r:MEMBER_OF {{roles:['Developer'], DateJoined:['{member.CreatedAt}']}}]->(b) RETURN type(r)");
                        });
                        users.Add(member.Login);
                    }
                }

                Console.WriteLine($"Iterating repos - {repositories.Count}");
                int repositoryCount = 0;
                foreach (var repository in repositories)
                {
                    Console.WriteLine($"Repo #{++repositoryCount} of {repositories.Count}");
                    await session.WriteTransactionAsync(async tx =>
                    {
                        await tx.RunAsync($"CREATE (repo:Repository {{name:'{repository.Name}', Id:'{repository.Id}', StargazersCount: '{repository.StargazersCount}', WatchersCount: '{repository.WatchersCount}', GitUrl: '{repository.GitUrl}'}})");
                        await tx.RunAsync($"MATCH (a:Org), (b:Repository) WHERE a.Id = '{organization.Id}' AND b.name = '{repository.Id}' CREATE (b)-[r:BELONGS_TO]->(a) RETURN type(r)");
                    });
                    await Throttle();
                    var contributors = await github.Repository.GetAllContributors(repository.Id);
                    Console.WriteLine($"Iterating contributors - {contributors.Count}");
                    int contributorscount = 0;

                    foreach (var contributor in contributors)
                    {
                        if (!contributor.IsBot())
                        {
                            // is this contributor part of bcgov? If not, add them.
                            if (users.Add(contributor.Login))
                            {
                                Console.WriteLine($"Found new contributor {contributor.Login}");
                                await session.WriteTransactionAsync(async tx =>
                                {
                                    await tx.RunAsync($"CREATE (member:Person {{name:'{contributor.Login}', Id:'{contributor.Id}', Login: '{contributor.Login}'}})");
                                });
                            }
                            await session.WriteTransactionAsync(async tx =>
                            {
                                await tx.RunAsync($"MATCH (a:Person), (b:Repository) WHERE a.Id = '{contributor.Id}' AND b.Id = '{repository.Id}' CREATE (a)-[r:CONTRIBUTOR_OF {{roles:['Developer']}}]->(b) RETURN type(r)");
                            });
                            contributorscount++;
                        }
                    }

                    var repositoryLanguages = await github.Repository.GetAllLanguages(repository.Id);
                    foreach (var language in repositoryLanguages)
                    {
                        if (languages.Add(language.Name))
                        {
                            Console.WriteLine($"Found new language {language.Name}");
                            await session.WriteTransactionAsync(async tx =>
                            {
                                await tx.RunAsync($"CREATE (lang:Language {{name:'{language.Name}', Id:'{language.Name}'}})");
                            });
                        }
                        await session.WriteTransactionAsync(async tx =>
                        {
                            await tx.RunAsync($"MATCH (a:Repository), (b:Language) WHERE a.Id = '{repository.Id}' AND b.Id = '{language.Name}' CREATE (a)-[r:DEVELOPED_IN {{Bytes:['{language.NumberOfBytes}']}}]->(b) RETURN type(r)");
                        });
                    }

                    var repositoryTopics = await github.Repository.GetAllTopics(repository.Id);
                    foreach (var topic in repositoryTopics.Names)
                    {
                        if (topics.Add(topic))
                        {
                            Console.WriteLine($"Found new topic {topic}");
                            await session.WriteTransactionAsync(async tx =>
                            {
                                await tx.RunAsync($"CREATE (top:Topic {{name:'{topic}', Id:'{topic}'}})");
                            });
                        }

                        await session.WriteTransactionAsync(async tx =>
                        {
                            await tx.RunAsync($"MATCH (a:Repository), (b:Topic) WHERE a.Id = '{repository.Id}' AND b.Id = '{topic}' CREATE (a)-[r:HAS_TOPIC]->(b) RETURN type(r)");
                        });
                    }
                }
            }
        }

        private string GetRequiredConfiguration(string name)
        {
            var value = _configuration[name];
            if (string.IsNullOrEmpty(value))
            {
                throw new ConfigurationErrorsException($"Configuration value {name} is missing");
            }

            return value;
        }

        private GitHubClient GetGitHubClient(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var tokenAuth = new Credentials(GetRequiredConfiguration("GitHubToken"));
            var client = new GitHubClient(new ProductHeaderValue("GitHubNeo4j"));
            client.Credentials = tokenAuth;
            return client;
        }

        private IDriver CreateDriver() => GraphDatabase.Driver("bolt://localhost:7687", AuthTokens.Basic("neo4j", GetRequiredConfiguration("neo4jpassword")));

        private async Task WipeAllDate(IDriver driver)
        {
            using (var session = driver.AsyncSession())
            {
                await session.WriteTransactionAsync(async tx =>
                    {
                        await tx.RunAsync($"MATCH (n) DETACH DELETE n");
                    });
            }
        }

        private async Task GetRates()
        {
            var miscellaneousRateLimit = await github.Miscellaneous.GetRateLimits();

            //  The "core" object provides your rate limit status except for the Search API.
            var coreRateLimit = miscellaneousRateLimit.Resources.Core;

            var howManyCoreRequestsCanIMakePerHour = coreRateLimit.Limit;
            var howManyCoreRequestsDoIHaveLeft = coreRateLimit.Remaining;
            var whenDoesTheCoreLimitReset = coreRateLimit.Reset; // UTC time

            // the "search" object provides your rate limit status for the Search API.
            var searchRateLimit = miscellaneousRateLimit.Resources.Search;

            var howManySearchRequestsCanIMakePerMinute = searchRateLimit.Limit;
            var howManySearchRequestsDoIHaveLeft = searchRateLimit.Remaining;
            var whenDoesTheSearchLimitReset = searchRateLimit.Reset; // UTC time

            Console.WriteLine($"coreRateLimit={coreRateLimit}");
            Console.WriteLine($"howManyCoreRequestsCanIMakePerHour={howManyCoreRequestsCanIMakePerHour}");
            Console.WriteLine($"howManyCoreRequestsDoIHaveLeft={howManyCoreRequestsDoIHaveLeft}");
            Console.WriteLine($"whenDoesTheCoreLimitReset={whenDoesTheCoreLimitReset}");
            Console.WriteLine($"searchRateLimit={searchRateLimit}");
            Console.WriteLine($"howManySearchRequestsCanIMakePerMinute={howManySearchRequestsCanIMakePerMinute}");
            Console.WriteLine($"howManySearchRequestsDoIHaveLeft={howManySearchRequestsDoIHaveLeft}");
            Console.WriteLine($"whenDoesTheSearchLimitReset={whenDoesTheSearchLimitReset}");
        }

        private async Task Throttle()
        {
            TimeSpan t = DateTime.UtcNow - DateTime.UnixEpoch;
            long secondsSinceEpoch = (long)t.TotalSeconds;

            var Rates = await github.Miscellaneous.GetRateLimits();
            if (Rates.Resources.Core.Remaining < 1000)
            {
                var wait = (int)(Rates.Resources.Core.ResetAsUtcEpochSeconds - secondsSinceEpoch) * 1000;
                Console.WriteLine($"THROTTLING! {wait} milliseconds");
                await Task.Delay(wait);
            }
            else
            {
                Console.WriteLine($"Rates.Resources.Core.Remaining = {Rates.Resources.Core.Remaining}");
            }
        }
    }

    public static class Extensions
    {
        /// <summary>
        /// Returns a value indicating if the contributor is a bot.
        /// </summary>
        public static bool IsBot(this RepositoryContributor contributor)
        {
            return Isbot(contributor.Login);
        }

        /// <summary>
        /// Returns a value indicating if the user is a bot.
        /// </summary>
        public static bool IsBot(this User user)
        {
            return Isbot(user.Login);
        }

        private static bool Isbot(string login) => login.Contains("[BOT]", StringComparison.OrdinalIgnoreCase);
    }
}