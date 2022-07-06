using Octokit;
using Neo4j.Driver;

namespace bcgovneo4j
{
    public class GitData
    {
        GitHubClient github { get; set; }
        private readonly IDriver _driver;


        public GitData()
        {
            var tokenAuth = new Credentials(Environment.GetEnvironmentVariable("GitHubToken"));
            github = new GitHubClient(new ProductHeaderValue("GitHubNeo4j"));
            github.Credentials = tokenAuth;
            GetRates().Wait();

            _driver = GraphDatabase.Driver("bolt://localhost:7687", AuthTokens.Basic("neo4j", Environment.GetEnvironmentVariable("neo4jpassword")));

        }

        public async Task GetData()
        {
            var org = await github.Organization.Get("bcgov");
            var members = await github.Organization.Member.GetAll("bcgov");
            List<string> allusers = new List<string>();
            List<string> alllanguages = new List<string>();
            List<string> alltopics = new List<string>();
            var repos = await github.Repository.GetAllForOrg("bcgov");
            Console.WriteLine(members.Count() + " are a member of bcgov and has " + repos.Count() + " repos");
            await Throttle();            
            using (var session = _driver.AsyncSession())
            {
                await WipeAllDate();
                await session.WriteTransactionAsync(async tx =>
                {
                    await tx.RunAsync($"CREATE (bcgov:Org {{name:'{org.Name}', url: '{org.Url}', id: '{org.Id}'}})");
                });
                Console.WriteLine("Iterating members - " + members.Count);
                foreach (var member in members)
                {
                    if (!member.Login.ToUpper().Contains("[BOT]"))
                    {
                        Console.WriteLine("Member #" + allusers.Count());
                        await session.WriteTransactionAsync(async tx =>
                        {
                            await tx.RunAsync($"CREATE (member:Person {{name:'{member.Login}', Id:'{member.Id}', Login: '{member.Login}'}})");
                            await tx.RunAsync($"MATCH (a:Person), (b:Org) WHERE a.Id = '{member.Id}' AND b.name = '{org.Name}' CREATE (a)-[r:MEMBER_OF {{roles:['Developer'], DateJoined:['{member.CreatedAt}']}}]->(b) RETURN type(r)");
                        });
                        allusers.Add(member.Login);
                    }
                }

                Console.WriteLine("Iterating repos - " + repos.Count);
                int reposcount = 0;
                foreach (var repo in repos)
                {
                    Console.WriteLine("Repo #" + reposcount + " of " + repos.Count);
                    await session.WriteTransactionAsync(async tx =>
                    {
                        await tx.RunAsync($"CREATE (repo:Repository {{name:'{repo.Name}', Id:'{repo.Id}', StargazersCount: '{repo.StargazersCount}', WatchersCount: '{repo.WatchersCount}', GitUrl: '{repo.GitUrl}'}})");
                        await tx.RunAsync($"MATCH (a:Org), (b:Repository) WHERE a.Id = '{org.Id}' AND b.name = '{repo.Id}' CREATE (b)-[r:BELONGS_TO]->(a) RETURN type(r)");
                    });
                    await Throttle();
                    var contributors = await github.Repository.GetAllContributors(repo.Id);
                    Console.WriteLine("Iterating contributors - " + contributors.Count);
                    int contributorscount = 0;
                    foreach (var contributor in contributors)
                    {
                        if (!contributor.Login.ToUpper().Contains("[BOT]"))
                        {
                            //is this contributor part of bcgov? If not, add them.
                            if (!allusers.Contains(contributor.Login))
                            {
                                Console.WriteLine("Found new contributor " + contributor.Login);
                                await session.WriteTransactionAsync(async tx =>
                                {
                                    await tx.RunAsync($"CREATE (member:Person {{name:'{contributor.Login}', Id:'{contributor.Id}', Login: '{contributor.Login}'}})");
                                    allusers.Add(contributor.Login);
                                });
                            }
                            await session.WriteTransactionAsync(async tx =>
                            {
                                await tx.RunAsync($"MATCH (a:Person), (b:Repository) WHERE a.Id = '{contributor.Id}' AND b.Id = '{repo.Id}' CREATE (a)-[r:CONTRIBUTOR_OF {{roles:['Developer']}}]->(b) RETURN type(r)");
                            });
                            contributorscount++;
                        }

                    }
                    var repolangs = await github.Repository.GetAllLanguages(repo.Id);
                    foreach (var language in repolangs)
                    {
                        if (!alllanguages.Contains(language.Name))
                        {
                            Console.WriteLine("Found new language " + language.Name);
                            await session.WriteTransactionAsync(async tx =>
                            {
                                await tx.RunAsync($"CREATE (lang:Language {{name:'{language.Name}', Id:'{language.Name}'}})");
                                alllanguages.Add(language.Name);
                            });                            
                        }
                        await session.WriteTransactionAsync(async tx =>
                        {
                            await tx.RunAsync($"MATCH (a:Repository), (b:Language) WHERE a.Id = '{repo.Id}' AND b.Id = '{language.Name}' CREATE (a)-[r:DEVELOPED_IN {{Bytes:['{language.NumberOfBytes}']}}]->(b) RETURN type(r)");
                        });
                    }
                    var repotopics = await github.Repository.GetAllTopics(repo.Id);
                    foreach (var topic in repotopics.Names)
                    {
                        if (!alltopics.Contains(topic))
                        {
                            Console.WriteLine("Found new topic " + topic);
                            await session.WriteTransactionAsync(async tx =>
                            {
                                await tx.RunAsync($"CREATE (top:Topic {{name:'{topic}', Id:'{topic}'}})");
                                alltopics.Add(topic);
                            });                            
                        }
                        await session.WriteTransactionAsync(async tx =>
                        {
                            await tx.RunAsync($"MATCH (a:Repository), (b:Topic) WHERE a.Id = '{repo.Id}' AND b.Id = '{topic}' CREATE (a)-[r:HAS_TOPIC]->(b) RETURN type(r)");
                        });
                    }

                    reposcount++;
                }

                // return default(object);
                // });

            }

        }

        async Task WipeAllDate()
        {
            using (var session = _driver.AsyncSession())
            {
                await session.WriteTransactionAsync(async tx =>
                    {
                        await tx.RunAsync($"MATCH (n) DETACH DELETE n");
                    });
            }
        }

        async Task GetRates()
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
            Console.WriteLine("coreRateLimit=" + coreRateLimit);
            Console.WriteLine("howManyCoreRequestsCanIMakePerHour=" + howManyCoreRequestsCanIMakePerHour);
            Console.WriteLine("howManyCoreRequestsDoIHaveLeft=" + howManyCoreRequestsDoIHaveLeft);
            Console.WriteLine("whenDoesTheCoreLimitReset=" + whenDoesTheCoreLimitReset);
            Console.WriteLine("searchRateLimit=" + searchRateLimit);
            Console.WriteLine("howManySearchRequestsCanIMakePerMinute=" + howManySearchRequestsCanIMakePerMinute);
            Console.WriteLine("howManySearchRequestsDoIHaveLeft=" + howManySearchRequestsDoIHaveLeft);
            Console.WriteLine("whenDoesTheSearchLimitReset=" + whenDoesTheSearchLimitReset);
        }

        private async Task Throttle()
        {
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long secondsSinceEpoch = (long)t.TotalSeconds;

            var Rates = await github.Miscellaneous.GetRateLimits();
            if (Rates.Resources.Core.Remaining < 1000)
            {
                var wait = (int)(Rates.Resources.Core.ResetAsUtcEpochSeconds - secondsSinceEpoch) * 1000;
                Console.WriteLine("THROTTLING! " + wait + " milliseconds");
                await Task.Delay(wait);
            }
            else
            {
                Console.WriteLine("Rates.Resources.Core.Remaining = " + Rates.Resources.Core.Remaining);
            }
        }

    }
}