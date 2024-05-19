using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamWorkshop.WebAPI.Managers;
using static SteamKit2.SteamApps;

namespace SteamWorkshop.WebAPI.Internal
{
    public class Steam3Session
    {
        public class Credentials
        {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }

            public bool IsValid
            {
                get { return LoggedOn; }
            }
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

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamContent steamContent;
        public readonly SteamApps steamApps;
        private readonly SteamCloud steamCloud;
        private readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> steamPublishedFile;

        public CallbackManager callbacks;

        private readonly bool authenticatedUser;
        private bool bConnected;
        private bool bConnecting;
        private bool bAborted;
        private bool bExpectingDisconnectRemote;
        private bool bDidDisconnect;
        private bool bIsConnectionRecovery;
        private int connectionBackoff;
        private int seq; // more hack fixes
        private DateTime connectTime;
        private AuthSession? authSession;

        // input
        private readonly SteamUser.LogOnDetails logonDetails;
        // output
        private readonly Credentials credentials;

        private static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds(30);

        public delegate void OnPICSChanged(SteamApps.PICSChangesCallback cb);
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
            }) 
        {
            this.Logger = Logger;
        }

        public Steam3Session(SteamUser.LogOnDetails details)
        {
            this.logonDetails = details;
            this.authenticatedUser = details.Username != null;
            this.credentials = new Credentials();
            this.bConnected = false;
            this.bConnecting = false;
            this.bAborted = false;
            this.bExpectingDisconnectRemote = false;
            this.bDidDisconnect = false;
            this.seq = 0;

            this.AppTokens = [];
            this.PackageTokens = [];
            this.DepotKeys = [];
            this.CDNAuthTokens = new ConcurrentDictionary<string, TaskCompletionSource<CDNAuthTokenCallback>>();
            this.AppInfo = [];
            this.PackageInfo = [];
            this.AppBetaPasswords = [];

            var clientConfiguration = SteamConfiguration.Create(config => config.WithHttpClientFactory(HttpClientFactory.CreateHttpClient));
            this.steamClient = new SteamClient(clientConfiguration);

            this.steamUser = this.steamClient.GetHandler<SteamUser>()!;
            this.steamApps = this.steamClient.GetHandler<SteamApps>()!;
            this.steamCloud = this.steamClient.GetHandler<SteamCloud>()!;
            var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>()!;
            this.steamPublishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();
            this.steamContent = this.steamClient.GetHandler<SteamContent>()!;

            this.callbacks = new CallbackManager(this.steamClient);
            this.SubscribeAll();

            this.SentryData = [];
            this.ContentServerPenalty = new();
            this.LoginTokens = [];
        }

        public void SubscribeAll()
        {
            this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
            this.callbacks.Subscribe<SteamUser.SessionTokenCallback>(SessionTokenCallback);
            this.callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
            // this.callbacks.Subscribe<SteamUser.UpdateMachineAuthCallback>(UpdateMachineAuthCallback);
            this.callbacks.Subscribe<SteamApps.PICSChangesCallback>(PICSChanged);
        }

        private void PICSChanged(PICSChangesCallback callback)
        {
            this.OnPICSChanges?.Invoke(callback);
        }

        public delegate bool WaitCondition();

        private readonly object steamLock = new();

        public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
        {
            while (!this.bAborted && !waiter())
            {
                lock (this.steamLock)
                {
                    submitter();
                }

                var seq = this.seq;
                do 
                {
                    lock (this.steamLock)
                    {
                        this.WaitForCallbacks();
                    }
                } while (!this.bAborted && this.seq == seq && !waiter());
            }

            return this.bAborted;
        }

        public Credentials WaitForCredentials()
        {
            if (this.credentials.IsValid || this.bAborted)
                return this.credentials;

            this.WaitUntilCallback(() => { }, () => { return this.credentials.IsValid; });

            return this.credentials;
        }

        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (this.bAborted)
                return 0;

            for (int i = 0; i < 10; i++)
                try
                {
                    return await this.steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);
                }
                catch 
                {
                    Task.Delay(10).Wait();
                    continue;
                }
            return 0;
        }

        public void RequestDepotKey(uint depotId, uint appid = 0)
        {
            if (this.DepotKeys.ContainsKey(depotId) || this.bAborted)
                return;

            var completed = false;

            Action<SteamApps.DepotKeyCallback> cbMethod = depotKey =>
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
                this.callbacks.Subscribe(this.steamApps.GetDepotDecryptionKey(depotId, appid), cbMethod);
            }, () => { return completed; });
        }

        private void ResetConnectionFlags()
        {
            this.bExpectingDisconnectRemote = false;
            this.bDidDisconnect = false;
            this.bIsConnectionRecovery = false;
        }

        public void Connect()
        {
            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Connecting to Steam3...");

            this.bAborted = false;
            this.bConnected = false;
            this.bConnecting = true;
            this.connectionBackoff = 0;
            this.authSession = null;

            this.ResetConnectionFlags();

            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort(bool sendLogOff = true)
        {
            this.OnFailedToReconnect?.Invoke();
            this.Disconnect(sendLogOff);
        }

        public void Disconnect(bool sendLogOff = true)
        {
            if (sendLogOff)
            {
                this.steamUser.LogOff();
            }

            this.bAborted = true;
            this.bConnected = false;
            this.bConnecting = false;
            this.bIsConnectionRecovery = false;
            this.steamClient.Disconnect();

            // flush callbacks until our disconnected event
            while (!this.bDidDisconnect)
            {
                this.callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }

        public void Reconnect()
        {
            this.bIsConnectionRecovery = true;
            this.Connect();
        }

        private void WaitForCallbacks()
        {
            this.callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));

            var diff = DateTime.Now - this.connectTime;

            if (diff > STEAM3_TIMEOUT && !this.bConnected)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Timeout connecting to Steam3.");
                this.OnFailedToReconnect?.Invoke();
                Abort();
            }
        }

        private async void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            this.bConnecting = false;
            this.bConnected = true;

            // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
            // e.g. if the authentication phase takes a while and therefore multiple connections.
            this.connectTime = DateTime.Now;
            this.connectionBackoff = 0;

            if (!this.authenticatedUser)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Logging anonymously into Steam3...");
                this.steamUser.LogOnAnonymous();
            }
            else
            {
                if (this.logonDetails.Username != null)
                {
                    this.Logger?.WriteLine($"[{this.GetType().FullName}]: Logging '{this.logonDetails.Username}' into Steam3...");
                }

                if (this.authSession is null)
                {
                    if (this.logonDetails.Username != null && this.logonDetails.Password != null && this.logonDetails.AccessToken is null)
                    {
                        try
                        {
                            this.authSession = await this.steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                            {
                                Username = logonDetails.Username,
                                Password = logonDetails.Password,
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
                }

                if (this.authSession != null)
                {
                    try
                    {
                        var result = await authSession.PollingWaitForResultAsync();

                        this.logonDetails.Username = result.AccountName;
                        this.logonDetails.Password = null;
                        this.logonDetails.AccessToken = result.RefreshToken;

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

                    this.authSession = null;
                }

                this.steamUser.LogOn(this.logonDetails);
            }
        }

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            this.bDidDisconnect = true;

            this.Logger?.WriteLine(
                $"[{this.GetType().FullName}]: Disconnected: bIsConnectionRecovery = {this.bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {this.bExpectingDisconnectRemote}");

            // When recovering the connection, we want to reconnect even if the remote disconnects us
            if (!this.bIsConnectionRecovery && (disconnected.UserInitiated || this.bExpectingDisconnectRemote))
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Disconnected from Steam");

                // Any operations outstanding need to be aborted
                this.bAborted = true;

                this.OnClientsDisconnect?.Invoke(disconnected);
                this.OnFailedToReconnect?.Invoke();
            }
            else if (this.connectionBackoff >= 10)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Could not connect to Steam after 10 tries");
                this.OnFailedToReconnect?.Invoke();
                this.Abort(false);
            }
            else if (!this.bAborted)
            {
                if (this.bConnecting)
                {
                    this.Logger?.WriteLine($"[{this.GetType().FullName}]: Connection to Steam failed. Trying again");
                }
                else
                {
                    this.Logger?.WriteLine($"[{this.GetType().FullName}]: Lost connection to Steam. Reconnecting");
                }

                Thread.Sleep(1000 * ++connectionBackoff);

                // Any connection related flags need to be reset here to match the state after Connect
                this.ResetConnectionFlags();
                this.steamClient.Connect();
            }
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
            var is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            var isAccessToken = true && this.logonDetails.AccessToken != null && loggedOn.Result == EResult.InvalidPassword; // TODO: Get EResult for bad access token

            if (isSteamGuard || is2FA || isAccessToken)
            {
                this.bExpectingDisconnectRemote = true;
                this.Abort(false);

                if (!isAccessToken)
                {
                    this.Logger?.WriteLine($"[{this.GetType().FullName}]: This account is protected by Steam Guard.");
                }

                if (is2FA)
                {
                    do
                    {
                        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Please enter your 2 factor auth code from your authenticator app");
                        break;
                        //this.logonDetails.TwoFactorCode = this.Logger?.ReadLine();
                    } while (string.Empty == this.logonDetails.TwoFactorCode);
                }
                else if (isAccessToken)
                {
                    this.LoginTokens.Remove(this.logonDetails.Username!);
                    //this.settings.Save();

                    // TODO: Handle gracefully by falling back to password prompt?
                    this.Logger?.WriteLine($"[{this.GetType().FullName}]: Access token was rejected.");
                    this.OnFailedToReconnect?.Invoke();
                    this.Abort(false);
                    return;
                }
                else
                {
                    do
                    {
                        this.Logger?.WriteLine($"[{this.GetType().FullName}]: Please enter the authentication code sent to your email address: ");
                        //this.logonDetails.AuthCode = this.Logger?.ReadLine();
                        break;
                    } while (string.Empty == this.logonDetails.AuthCode);
                }

                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Retrying Steam3 connection...");
                this.Connect();

                return;
            }

            if (loggedOn.Result == EResult.TryAnotherCM)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Retrying Steam3 connection (TryAnotherCM)...");

                this.Reconnect();

                return;
            }

            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Unable to login to Steam3: {loggedOn.Result}");
                this.OnFailedToReconnect?.Invoke();
                this.Abort(false);

                return;
            }

            if (loggedOn.Result != EResult.OK)
            {
                this.Logger?.WriteLine($"[{this.GetType().FullName}]: Unable to login to Steam3: {loggedOn.Result}");
                this.OnFailedToReconnect?.Invoke();
                this.Abort();

                return;
            }

            this.Logger?.WriteLine($"[{this.GetType().FullName}]: Logged In");

            this.OnClientsLogin?.Invoke(loggedOn);

            this.seq++;
            this.credentials.LoggedOn = true;
        }

        private void SessionTokenCallback(SteamUser.SessionTokenCallback sessionToken)
        {
            this.credentials.SessionToken = sessionToken.SessionToken;
        }

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

            foreach (var license in licenseList.LicenseList)
            {
                if (license.AccessToken > 0)
                {
                    this.PackageTokens.TryAdd(license.PackageID, license.AccessToken);
                }
            }
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
}
