using System.Collections.Concurrent;
using SteamKit2.CDN;
using SteamWorkshop.WebAPI.Managers;

namespace SteamWorkshop.WebAPI.Internal
{
    /// <summary>
    /// CDNClientPool provides a pool of connections to CDN endpoints, requesting CDN tokens as needed
    /// </summary>
    internal class CDNClientPool
    {
        private const int ServerEndpointMinimumSize = 8;

        private readonly Steam3Session steamSession;
        private readonly uint appId;
        public Client CDNClient { get; }
        public Server ProxyServer { get; private set; }

        private readonly ConcurrentStack<Server> activeConnectionPool;
        private readonly BlockingCollection<Server> availableServerEndpoints;

        private readonly AutoResetEvent populatePoolEvent;
        private readonly Task monitorTask;
        private readonly CancellationTokenSource shutdownToken;
        private readonly ConsoleManager? Logger;

        public CDNClientPool(Steam3Session steamSession, uint appId, ConsoleManager? Logger)
        {
            this.Logger = Logger;
            this.steamSession = steamSession;
            this.appId = appId;
            this.ProxyServer = new();
            this.CDNClient = new Client(steamSession.steamClient);

            this.activeConnectionPool = new ConcurrentStack<Server>();
            this.availableServerEndpoints = [];

            this.populatePoolEvent = new AutoResetEvent(true);
            this.shutdownToken = new CancellationTokenSource();

            this.monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync).Unwrap();
        }

        public void Shutdown()
        {
            this.shutdownToken.Cancel();
            this.monitorTask.Wait();
        }

        private async Task<IReadOnlyCollection<Server>?> FetchBootstrapServerListAsync()
        {
            try
            {
                var cdnServers = await this.steamSession.steamContent.GetServersForSteamPipe();
                if (cdnServers != null)
                {
                    return cdnServers;
                }
            }
            catch (Exception ex)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Failed to retrieve content server list: {ex.Message}");
            }

            return null;
        }

        private async Task ConnectionPoolMonitorAsync()
        {
            var didPopulate = false;

            while (!this.shutdownToken.IsCancellationRequested)
            {
                this.populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

                // We want the Steam session so we can take the CellID from the session and pass it through to the ContentServer Directory Service
                if (this.availableServerEndpoints.Count < ServerEndpointMinimumSize
                    && this.steamSession.steamClient.IsConnected)
                {
                    var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);

                    if (servers == null || servers.Count == 0)
                    {
                        return;
                    }

                    this.ProxyServer = servers.Where(x => x.UseAsProxy).First();

                    var weightedCdnServers = servers
                        .Where(server =>
                        {
                            var isEligibleForApp = server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(appId);
                            return isEligibleForApp && (server.Type == "SteamCache" || server.Type == "CDN");
                        })
                        .Select(server =>
                        {
                            this.steamSession.ContentServerPenalty.TryGetValue(server.Host!, out var penalty);

                            return (server, penalty);
                        })
                        .OrderBy(pair => pair.penalty).ThenBy(pair => pair.server.WeightedLoad);

                    foreach (var (server, weight) in weightedCdnServers)
                    {
                        for (var i = 0; i < server.NumEntries; i++)
                        {
                            this.availableServerEndpoints.Add(server);
                        }
                    }

                    didPopulate = true;
                }
                else if (this.availableServerEndpoints.Count == 0
                    && !this.steamSession.steamClient.IsConnected
                    && didPopulate)
                {
                    return;
                }
            }
        }

        private Server BuildConnection(CancellationToken token)
        {
            if (this.availableServerEndpoints.Count < ServerEndpointMinimumSize)
            {
                this.populatePoolEvent.Set();
            }

            return this.availableServerEndpoints.Take(token);
        }

        public Server GetConnection(CancellationToken token)
        {
            if (!this.activeConnectionPool.TryPop(out var connection))
            {
                connection = BuildConnection(token);
            }

            return connection;
        }
    }
}
