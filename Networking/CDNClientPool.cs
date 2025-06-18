using System.Collections.Concurrent;
using SteamKit2.CDN;
using SteamWorkshop.WebAPI.Managers;

namespace SteamWorkshop.WebAPI.Internal;

/// <summary>
/// CDNClientPool provides a pool of connections to CDN endpoints, requesting CDN tokens as needed
/// </summary>
internal class CDNClientPool
{
    private const int ServerEndpointMinimumSize = 8;

    private readonly Steam3Session _steamSession;
    private readonly uint _appId;
    public Client CDNClient { get; }
    public Server ProxyServer { get; private set; }

    private readonly ConcurrentStack<Server> _activeConnectionPool;
    private readonly BlockingCollection<Server> _availableServerEndpoints;

    private readonly AutoResetEvent _populatePoolEvent;
    private readonly Task _monitorTask;
    private readonly CancellationTokenSource _shutdownToken;
    private readonly ConsoleManager? _logger;

    public CDNClientPool(Steam3Session steamSession, uint appId, ConsoleManager? Logger)
    {
        this._logger = Logger;
        this._steamSession = steamSession;
        this._appId = appId;
        this.ProxyServer = new Server();
        this.CDNClient = new Client(steamSession.SteamClient);

        this._activeConnectionPool = new ConcurrentStack<Server>();
        this._availableServerEndpoints = [];

        this._populatePoolEvent = new AutoResetEvent(true);
        this._shutdownToken = new CancellationTokenSource();

        this._monitorTask = Task.Factory.StartNew(this.ConnectionPoolMonitorAsync).Unwrap();
    }

    public void Shutdown()
    {
        this._shutdownToken.Cancel();
        this._monitorTask.Wait();
    }

    private async Task<IReadOnlyCollection<Server>?> FetchBootstrapServerListAsync()
    {
        try
        {
            return await this._steamSession.SteamContent.GetServersForSteamPipe();
        }
        catch (Exception ex)
        {
            this._logger?.WriteLine($"[{this.GetType().FullName}]: Failed to retrieve content server list: {ex.Message}");
        }

        return null;
    }

    private async Task ConnectionPoolMonitorAsync()
    {
        var didPopulate = false;

        while (!this._shutdownToken.IsCancellationRequested)
        {
            this._populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

            // We want the Steam session so we can take the CellID from the session and pass it through to the ContentServer Directory Service
            bool status = this._availableServerEndpoints.Count >= ServerEndpointMinimumSize || !this._steamSession.SteamClient.IsConnected;
            bool failed = this._availableServerEndpoints.Count == 0 && !this._steamSession.SteamClient.IsConnected && didPopulate;

            // Check status
            switch (status)
            {
                case false when failed:
                    return;
                case false:
                    continue;
            }

            var servers = await this.FetchBootstrapServerListAsync().ConfigureAwait(false);

            if (servers == null || servers.Count == 0)
                return;

            this.ProxyServer = servers.First(x => x.UseAsProxy);
            var weightedCdnServers = servers.Where(this.IsEligibleForApp)
                                            .Select(this.TryGetPenalty)
                                            .OrderBy(pair => pair.penalty)
                                            .ThenBy(pair => pair.server.WeightedLoad);

            foreach ((Server server, int _) in weightedCdnServers)
                for (var i = 0; i < server.NumEntries; i++)
                    this._availableServerEndpoints.Add(server);

            didPopulate = true;
        }
    }

    private bool IsEligibleForApp(Server server)
        => (server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(this._appId)) 
           && server.Type is "SteamCache" or "CDN";

    private (Server server, int penalty) TryGetPenalty(Server server)
    {
        this._steamSession.ContentServerPenalty.TryGetValue(server.Host!, out int penalty);
        return (server, penalty);
    }

    private Server BuildConnection(CancellationToken token)
    {
        if (this._availableServerEndpoints.Count < ServerEndpointMinimumSize)
            this._populatePoolEvent.Set();

        return this._availableServerEndpoints.Take(token);
    }

    public Server GetConnection(CancellationToken token)
    {
        if (!this._activeConnectionPool.TryPop(out Server? connection))
            connection = this.BuildConnection(token);

        return connection;
    }
}