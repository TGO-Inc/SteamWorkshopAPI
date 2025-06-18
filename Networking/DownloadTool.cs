using SteamKit2;
using SteamKit2.CDN;
using SteamWorkshop.WebAPI.Internal;

namespace SteamWorkshop.WebAPI;

public class DownloadTool
{
    private readonly uint _appid;
    private readonly Steam3Session _steam3;
    private readonly CDNClientPool _cdnClientPool;
    private readonly Server _cdnConnection;

    public DownloadTool(string username, string password, uint appid)
        : this(new Steam3Session(
            new SteamUser.LogOnDetails()
            {
                Username = username,
                Password = password,
                ShouldRememberPassword = true,
                LoginID = 0x534B32
            }), appid) { }

    public DownloadTool(Steam3Session session, uint appid)
    {
        this._appid = appid;
        this._steam3 = session;
        this._cdnClientPool = new CDNClientPool(session, appid, session.Logger);
        this._cdnConnection = this._cdnClientPool.GetConnection(CancellationToken.None);
        this._steam3.RequestDepotKey(appid, appid);
    }

    public DepotManifest? DownloadManifest(uint depotId, uint appid, ulong manifestId)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                ulong manifestRequestCode = this._steam3.GetDepotManifestRequestCodeAsync(depotId, appid, manifestId, "public").GetAwaiter().GetResult();

                return this._cdnClientPool.CDNClient.DownloadManifestAsync(
                               depotId, 
                               manifestId,
                               manifestRequestCode,
                               this._cdnConnection,
                               this._steam3.DepotKeys[depotId],
                               this._cdnClientPool.ProxyServer)
                           .GetAwaiter().GetResult();
            }
            catch
            {
                Task.Delay(10).Wait();
            }
        }
            
        return null;
    }
    public byte[] DownloadFile(uint depotId, DepotManifest.ChunkData data)
    {
        DepotChunk chunkData = this._cdnClientPool.CDNClient.DownloadDepotChunkAsync(
                                       depotId, 
                                       data, 
                                       this._cdnConnection, 
                                       this._steam3.DepotKeys[depotId], 
                                       this._cdnClientPool.ProxyServer)
                                   .GetAwaiter().GetResult();

        return chunkData.Data;
    }
}