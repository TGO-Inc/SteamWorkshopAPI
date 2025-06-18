using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using JetBrains.Annotations;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamWorkshop.WebAPI.Managers;
using static SteamKit2.SteamApps;

namespace SteamWorkshop.WebAPI.Internal;

[PublicAPI]
public class Steam3Session
{
    public class Credentials
    {
        public bool LoggedOn { get; set; }
        public ulong SessionToken { get; set; }

        public bool IsValid => this.LoggedOn;
    }

    public ReadOnlyCollection<LicenseListCallback.License>? Licenses
    {
        get;
        private set;
    }

    public Dictionary<uint, ulong> AppTokens { get; private set; }
    public Dictionary<uint, ulong> PackageTokens { get; private set; }
    public Dictionary<uint, byte[]> DepotKeys { get; private set; }
    public ConcurrentDictionary<string, TaskCompletionSource<CDNAuthTokenCallback>> CDNAuthTokens { get; private set; }
    public Dictionary<uint, PICSProductInfoCallback.PICSProductInfo> AppInfo { get; private set; }
    public Dictionary<uint, PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; private set; }
    public Dictionary<string, byte[]> AppBetaPasswords { get; private set; }
    public Dictionary<string, byte[]> SentryData { get; private set; }
    public Dictionary<string, string> LoginTokens { get; private set; }
    public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }

    public readonly SteamClient SteamClient;
    public readonly SteamUser SteamUser;
    public readonly SteamContent SteamContent;
    public readonly SteamApps SteamApps;
    private readonly SteamCloud _steamCloud;
    private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> _steamPublishedFile;

    public readonly CallbackManager Callbacks;

    private readonly bool _authenticatedUser;
    private bool _bConnected;
    private bool _bConnecting;
    private bool _bAborted;
    private bool _bExpectingDisconnectRemote;
    private bool _bDidDisconnect;
    private bool _bIsConnectionRecovery;
    private int _connectionBackoff;
    private int _seq; // more hack fixes
    private DateTime _connectTime;
    private AuthSession? _authSession;

    // input
    private readonly SteamUser.LogOnDetails _logonDetails;
    // output
    private readonly Credentials _credentials;

    private static readonly TimeSpan Steam3Timeout = TimeSpan.FromSeconds(30);

    public delegate void OnPICSChanged(PICSChangesCallback cb);
    public event OnPICSChanged? OnPICSChanges;

    public delegate void OnClientLogin(SteamUser.LoggedOnCallback logon);
    public event OnClientLogin? OnClientsLogin;

    public delegate void OnClientDisconnect(SteamClient.DisconnectedCallback disconnect);
    public event OnClientDisconnect? OnClientsDisconnect;

    public delegate void FailedToReconnect();
    public event FailedToReconnect? OnFailedToReconnect;

    public readonly ConsoleManager? Logger;

    public Steam3Session(string username, string password, ConsoleManager Logger)
        : this(new SteamUser.LogOnDetails()
        {
            Username = username,
            Password = password,
            ShouldRememberPassword = true,
            LoginID = 0x534B32
        }) => this.Logger = Logger;

    public Steam3Session(SteamUser.LogOnDetails details)
    {
        this._logonDetails = details;
        this._authenticatedUser = details.Username != null;
        this._credentials = new Credentials();
        this._bConnected = false;
        this._bConnecting = false;
        this._bAborted = false;
        this._bExpectingDisconnectRemote = false;
        this._bDidDisconnect = false;
        this._seq = 0;

        this.AppTokens = [];
        this.PackageTokens = [];
        this.DepotKeys = [];
        this.CDNAuthTokens = new ConcurrentDictionary<string, TaskCompletionSource<CDNAuthTokenCallback>>();
        this.AppInfo = [];
        this.PackageInfo = [];
        this.AppBetaPasswords = [];

        var clientConfiguration = SteamConfiguration.Create(config => config.WithHttpClientFactory(HttpClientFactory.CreateHttpClient));
        this.SteamClient = new SteamClient(clientConfiguration);

        this.SteamUser = this.SteamClient.GetHandler<SteamUser>()!;
        this.SteamApps = this.SteamClient.GetHandler<SteamApps>()!;
        this._steamCloud = this.SteamClient.GetHandler<SteamCloud>()!;
        var steamUnifiedMessages = this.SteamClient.GetHandler<SteamUnifiedMessages>()!;
        this._steamPublishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();
        this.SteamContent = this.SteamClient.GetHandler<SteamContent>()!;

        this.Callbacks = new CallbackManager(this.SteamClient);
        this.SubscribeAll();

        this.SentryData = [];
        this.ContentServerPenalty = [];
        this.LoginTokens = [];
    }

    private void SubscribeAll()
    {
        this.Callbacks.Subscribe<SteamClient.ConnectedCallback>(this.ConnectedCallback);
        this.Callbacks.Subscribe<SteamClient.DisconnectedCallback>(this.DisconnectedCallback);
        this.Callbacks.Subscribe<SteamUser.LoggedOnCallback>(this.LogOnCallback);
        this.Callbacks.Subscribe<SteamUser.SessionTokenCallback>(this.SessionTokenCallback);
        this.Callbacks.Subscribe<LicenseListCallback>(this.LicenseListCallback);
        // this.callbacks.Subscribe<SteamUser.UpdateMachineAuthCallback>(UpdateMachineAuthCallback);
        this.Callbacks.Subscribe<PICSChangesCallback>(this.PICSChanged);
    }

    private void PICSChanged(PICSChangesCallback callback)
        => this.OnPICSChanges?.Invoke(callback);

    public delegate bool WaitCondition();

    private readonly object _steamLock = new();

    public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
    {
        while (!this._bAborted && !waiter())
        {
            lock (this._steamLock)
                submitter();

            int seq = this._seq;
            do 
            {
                lock (this._steamLock)
                    this.WaitForCallbacks();
            } while (!this._bAborted && this._seq == seq && !waiter());
        }

        return this._bAborted;
    }

    public Credentials WaitForCredentials()
    {
        if (this._credentials.IsValid || this._bAborted)
            return this._credentials;

        this.WaitUntilCallback(() => { }, () => this._credentials.IsValid);

        return this._credentials;
    }

    public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
    {
        if (this._bAborted)
            return 0;

        for (var i = 0; i < 10; i++)
            try
            {
                return await this.SteamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);
            }
            catch 
            {
                Task.Delay(10).Wait();
            }
            
        return 0;
    }

    public void RequestDepotKey(uint depotId, uint appid = 0)
    {
        if (this.DepotKeys.ContainsKey(depotId) || this._bAborted)
            return;

        var completed = false;

        Action<DepotKeyCallback> cbMethod = depotKey =>
        {
            completed = true;
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Got depot key for {depotKey.DepotID} result: {depotKey.Result}");

            if (depotKey.Result != EResult.OK)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Error getting depot key!");
                this.Abort();
                return;
            }

            this.DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
        };

        this.WaitUntilCallback(() =>
        {
            this.Callbacks.Subscribe(this.SteamApps.GetDepotDecryptionKey(depotId, appid), cbMethod);
        }, () => completed);
    }

    private void ResetConnectionFlags()
    {
        this._bExpectingDisconnectRemote = false;
        this._bDidDisconnect = false;
        this._bIsConnectionRecovery = false;
    }

    public void Connect()
    {
        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Connecting to Steam3...");

        this._bAborted = false;
        this._bConnected = false;
        this._bConnecting = true;
        this._connectionBackoff = 0;
        this._authSession = null;

        this.ResetConnectionFlags();

        this._connectTime = DateTime.Now;
        this.SteamClient.Connect();
    }

    private void Abort(bool sendLogOff = true)
    {
        this.OnFailedToReconnect?.Invoke();
        this.Disconnect(sendLogOff);
    }

    public void Disconnect(bool sendLogOff = true)
    {
        if (sendLogOff)
            this.SteamUser.LogOff();

        this._bAborted = true;
        this._bConnected = false;
        this._bConnecting = false;
        this._bIsConnectionRecovery = false;
        this.SteamClient.Disconnect();

        // flush callbacks until our disconnected event
        while (!this._bDidDisconnect)
            this.Callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
    }

    public void Reconnect()
    {
        this._bIsConnectionRecovery = true;
        this.Connect();
    }

    private void WaitForCallbacks()
    {
        this.Callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));

        TimeSpan diff = DateTime.Now - this._connectTime;

        if (diff <= Steam3Timeout || this._bConnected)
            return;
            
        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Timeout connecting to Steam3.");
        this.OnFailedToReconnect?.Invoke();
        this.Abort();
    }

    private async void ConnectedCallback(SteamClient.ConnectedCallback connected)
    {
        this._bConnecting = false;
        this._bConnected = true;

        // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
        // e.g. if the authentication phase takes a while and therefore multiple connections.
        this._connectTime = DateTime.Now;
        this._connectionBackoff = 0;

        if (!this._authenticatedUser)
        {
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Logging anonymously into Steam3...");
            this.SteamUser.LogOnAnonymous();
            return;
        }

        if (this._logonDetails.Username != null)
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Logging '{this._logonDetails.Username}' into Steam3...");

        if (this._authSession is null && this._logonDetails is { Username: not null, Password: not null, AccessToken: null })
        {
            try
            {
                this._authSession = await this.SteamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = this._logonDetails.Username,
                    Password = this._logonDetails.Password,
                    IsPersistentSession = true
                });
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                //this.Logger?.Error.WriteLine($"[{this.GetType().FullName}]: Failed to authenticate with Steam: " + ex.Message);
                this.Logger?.Error.WriteLine(ex);
                this.Abort(false);
                return;
            }
        }
            

        if (this._authSession is not null)
        {
            try
            {
                AuthPollResult result = await this._authSession.PollingWaitForResultAsync();

                this._logonDetails.Username = result.AccountName;
                this._logonDetails.Password = null;
                this._logonDetails.AccessToken = result.RefreshToken;

                this.LoginTokens[result.AccountName] = result.RefreshToken;
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                this.Logger?.Error.WriteLine($"[{this.GetType().FullName}]: Failed to authenticate with Steam: " + ex.ToString());
                this.OnFailedToReconnect?.Invoke();
                this.Abort(false);
                return;
            }

            this._authSession = null;
        }

        this.SteamUser.LogOn(this._logonDetails);
    }

    private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
    {
        this._bDidDisconnect = true;

        this.Logger?.WriteLine(
            $"[{this.GetType().FullName}]: Disconnected: bIsConnectionRecovery = {this._bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {this._bExpectingDisconnectRemote}");

        // When recovering the connection, we want to reconnect even if the remote disconnects us
        if (!this._bIsConnectionRecovery && (disconnected.UserInitiated || this._bExpectingDisconnectRemote))
        {
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Disconnected from Steam");

            // Any operations outstanding need to be aborted
            this._bAborted = true;

            this.OnClientsDisconnect?.Invoke(disconnected);
            this.OnFailedToReconnect?.Invoke();
        }
        else if (this._connectionBackoff >= 10)
        {
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Could not connect to Steam after 10 tries");
            this.OnFailedToReconnect?.Invoke();
            this.Abort(false);
        }
        else if (!this._bAborted)
        {
            this.Logger?.WriteLine(this._bConnecting ? $"[{this.GetType().FullName}]: Connection to Steam failed. Trying again" : $"[{this.GetType().FullName}]: Lost connection to Steam. Reconnecting");
            Thread.Sleep(1000 * ++this._connectionBackoff);

            // Any connection related flags need to be reset here to match the state after Connect
            this.ResetConnectionFlags();
            this.SteamClient.Connect();
        }
    }

    private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
    {
        bool isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
        bool is2Fa = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
        bool isAccessToken = true && this._logonDetails.AccessToken != null && loggedOn.Result == EResult.InvalidPassword; // TODO: Get EResult for bad access token

        if (isSteamGuard || is2Fa || isAccessToken)
        {
            this._bExpectingDisconnectRemote = true;
            this.Abort(false);

            this.Logger?.WriteLine($"[{this.GetType().FullName}]: 2FA, SteamGuard, and Access Tokens are not supported.");

            // if (is2Fa)
            // {
            //     do
            //     {
            //         this.Logger?.WriteLine($"[{this.GetType().FullName}]: Please enter your 2 factor auth code from your authenticator app");
            //         break;
            //         //this.logonDetails.TwoFactorCode = this.Logger?.ReadLine();
            //     } while (string.Empty == this.logonDetails.TwoFactorCode);
            // }
            // else if (isAccessToken)
            // {
            //     this.LoginTokens.Remove(this.logonDetails.Username!);
            //     //this.settings.Save();
            //
            //     // TODO: Handle gracefully by falling back to password prompt?
            //     this.Logger?.WriteLine($"[{this.GetType().FullName}]: Access token was rejected.");
            //     this.OnFailedToReconnect?.Invoke();
            //     this.Abort(false);
            //     return;
            // }
            // else
            // {
            //     do
            //     {
            //         this.Logger?.WriteLine($"[{this.GetType().FullName}]: Please enter the authentication code sent to your email address: ");
            //         //this.logonDetails.AuthCode = this.Logger?.ReadLine();
            //         break;
            //     } while (string.Empty == this.logonDetails.AuthCode);
            // }

            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Retrying Steam3 connection...");
            this.Connect();

            return;
        }

        switch (loggedOn.Result)
        {
            case EResult.TryAnotherCM:
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Retrying Steam3 connection (TryAnotherCM)...");
                this.Reconnect();
                return;
            case EResult.ServiceUnavailable:
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Unable to login to Steam3: {loggedOn.Result}");
                this.OnFailedToReconnect?.Invoke();
                this.Abort(false);
                return;
            case EResult.OK:
                break;
            default:
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Unable to login to Steam3: {loggedOn.Result}");
                this.OnFailedToReconnect?.Invoke();
                this.Abort();
                return;
        }

        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Logged In");
        this.OnClientsLogin?.Invoke(loggedOn);

        this._seq++;
        this._credentials.LoggedOn = true;
    }

    private void SessionTokenCallback(SteamUser.SessionTokenCallback sessionToken)
        => this._credentials.SessionToken = sessionToken.SessionToken;

    private void LicenseListCallback(LicenseListCallback licenseList)
    {
        if (licenseList.Result != EResult.OK)
        {
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Unable to get license list: {licenseList.Result} ");
            this.OnFailedToReconnect?.Invoke();
            this.Abort();

            return;
        }

        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Got {licenseList.LicenseList.Count} licenses for account");
        this.Licenses = licenseList.LicenseList;

        foreach (LicenseListCallback.License license in licenseList.LicenseList.Where(l => l.AccessToken > 0))
            this.PackageTokens.TryAdd(license.PackageID, license.AccessToken);
    }

    /*
    private void UpdateMachineAuthCallback(SteamUser.UpdateMachineAuthCallback machineAuth)
    {
        var hash = SHA1.HashData(machineAuth.Data);
        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Got Machine Auth: {machineAuth.FileName} {machineAuth.Offset} {machineAuth.BytesToWrite} {machineAuth.Data.Length}");

        this.SentryData[this.logonDetails.Username!] = machineAuth.Data;
        //this.settings.Save();

        var authResponse = new SteamUser.MachineAuthDetails
        {
            BytesWritten = machineAuth.BytesToWrite,
            FileName = machineAuth.FileName,
            FileSize = machineAuth.BytesToWrite,
            Offset = machineAuth.Offset,

            SentryFileHash = hash, // should be the sha1 hash of the sentry file we just wrote

            OneTimePassword = machineAuth.OneTimePassword, // not sure on this one yet, since we've had no examples of steam using OTPs

            LastError = 0, // result from win32 GetLastError
            Result = EResult.OK, // if everything went okay, otherwise ~who knows~

            JobID = machineAuth.JobID, // so we respond to the correct server job
        };

        // send off our response
        this.steamUser.SendMachineAuthResponse(authResponse);
    }
    */
}