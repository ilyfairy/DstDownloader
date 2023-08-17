using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Internal;
using static SteamKit2.GC.Dota.Internal.CMsgSDOAssert;
using static SteamKit2.SteamApps;
using static SteamKit2.SteamApps.PICSProductInfoCallback;
using static SteamKit2.SteamUnifiedMessages;

namespace DepotDownloader
{
    public class Steam3Session : IDisposable
    {
        public static bool IsDebug { get; set; } = false;
        public class Credentials
        {
            public bool LoggedOn { get; set; }
            public ulong SessionToken { get; set; }

            public bool IsValid
            {
                get { return LoggedOn; }
            }
        }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, ulong> AppTokens { get; private set; }
        public Dictionary<uint, ulong> PackageTokens { get; private set; }
        public Dictionary<uint, byte[]> DepotKeys { get; private set; }
        public ConcurrentDictionary<string, TaskCompletionSource<SteamApps.CDNAuthTokenCallback>> CDNAuthTokens { get; private set; }
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; private set; }
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; private set; }
        public Dictionary<string, byte[]> AppBetaPasswords { get; private set; }

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamContent steamContent;
        readonly SteamApps steamApps;
        readonly SteamCloud steamCloud;
        readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> steamPublishedFile;

        readonly CallbackManager callbacks;

        readonly bool authenticatedUser;
        bool bConnected;
        bool bConnecting;
        bool bAborted;
        bool bExpectingDisconnectRemote;
        bool bDidDisconnect;
        bool bDidReceiveLoginKey;
        bool bIsConnectionRecovery;
        int connectionBackoff;
        int seq; // more hack fixes
        DateTime connectTime;

        // input
        readonly SteamUser.LogOnDetails logonDetails;

        // output
        readonly Credentials credentials;

        static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds(30);

        private readonly HttpClient _httpclient = new();

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
            this.bDidReceiveLoginKey = false;
            this.seq = 0;

            this.AppTokens = new Dictionary<uint, ulong>();
            this.PackageTokens = new Dictionary<uint, ulong>();
            this.DepotKeys = new Dictionary<uint, byte[]>();
            this.CDNAuthTokens = new ConcurrentDictionary<string, TaskCompletionSource<SteamApps.CDNAuthTokenCallback>>();
            this.AppInfo = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            this.PackageInfo = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            this.AppBetaPasswords = new Dictionary<string, byte[]>();



            //var clientConfiguration = SteamConfiguration.Create(config =>
            //    config.WithHttpClientFactory(HttpClientFactory.CreateHttpClient));

            //var clientConfiguration = SteamConfiguration.CreateDefault();

            var clientConfiguration = SteamConfiguration.Create(config =>
                config.WithHttpClientFactory(() => _httpclient));




            this.steamClient = new SteamClient(clientConfiguration);
            
            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();
            this.steamCloud = this.steamClient.GetHandler<SteamCloud>();
            var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>();
            this.steamPublishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();
            this.steamContent = this.steamClient.GetHandler<SteamContent>();

            this.callbacks = new CallbackManager(this.steamClient);

            this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
            this.callbacks.Subscribe<SteamUser.SessionTokenCallback>(SessionTokenCallback);
            this.callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);
            this.callbacks.Subscribe<SteamUser.UpdateMachineAuthCallback>(UpdateMachineAuthCallback);
            this.callbacks.Subscribe<SteamUser.LoginKeyCallback>(LoginKeyCallback);

            if (IsDebug)
            {
                Console.WriteLine($"IsDebug: {IsDebug}");
                Console.Write("Connecting to Steam3...");
            }

            //if (authenticatedUser)
            //{
            //    var fi = new FileInfo(String.Format("{0}.sentryFile", logonDetails.Username));
            //    if (AccountSettingsStore.Instance.SentryData != null && AccountSettingsStore.Instance.SentryData.ContainsKey(logonDetails.Username))
            //    {
            //        logonDetails.SentryFileHash = Util.SHAHash(AccountSettingsStore.Instance.SentryData[logonDetails.Username]);
            //    }
            //    else if (fi.Exists && fi.Length > 0)
            //    {
            //        var sentryData = File.ReadAllBytes(fi.FullName);
            //        logonDetails.SentryFileHash = Util.SHAHash(sentryData);
            //        AccountSettingsStore.Instance.SentryData[logonDetails.Username] = sentryData;
            //        AccountSettingsStore.Save();
            //    }
            //}

            Connect();
        }

        public delegate bool WaitCondition();

        private readonly object steamLock = new();

        public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
        {
            while (!bAborted && !waiter())
            {
                lock (steamLock)
                {
                    submitter();
                }

                var seq = this.seq;
                do
                {
                    lock (steamLock)
                    {
                        WaitForCallbacks();
                    }
                } while (!bAborted && this.seq == seq && !waiter());
            }

            return bAborted;
        }

        public Credentials WaitForCredentials()
        {
            if (credentials.IsValid || bAborted)
                return credentials;

            WaitUntilCallback(() => { }, () => { return credentials.IsValid; });

            return credentials;
        }

        public async Task<PICSProductInfo?> RequestAppInfo(uint appId, bool bForce = false)
        {
            if (bAborted) return null;
            if (!bForce && AppInfo.ContainsKey(appId))
            {
                return AppInfo[appId];
            }

            //获取Token
            PICSTokensCallback appTokens = null;
            try
            {
                appTokens = await steamApps.PICSGetAccessTokens(new List<uint> { appId }, new List<uint>());
            }
            catch (Exception)
            {
                return null;
            }
            if (appTokens == null) return null;

            if (appTokens.AppTokensDenied.Contains(appId))
            {
                //Console.WriteLine("Insufficient privileges to get access token for app {0}", appId);
            }

            foreach (var token_dict in appTokens.AppTokens)
            {
                this.AppTokens[token_dict.Key] = token_dict.Value;
            }

            //获取AppInfo
            var request = new SteamApps.PICSRequest(appId);
            if (AppTokens.ContainsKey(appId))
            {
                request.AccessToken = AppTokens[appId];
            }
            PICSProductInfoCallback? appInfo = null;
            try
            {
                appInfo ??= (await steamApps.PICSGetProductInfo(new List<PICSRequest> { request }, new List<SteamApps.PICSRequest>()).ToTask()).Results.FirstOrDefault();
            }
            catch { }
            if (appInfo == null) return null;

            foreach (var app_value in appInfo.Apps)
            {
                PICSProductInfo? app = app_value.Value;

                if (IsDebug)
                {
                    Console.WriteLine("Got AppInfo for {0}", app.ID);
                }
                AppInfo[app.ID] = app;
            }

            foreach (var app in appInfo.UnknownApps)
            {
                AppInfo[app] = null;
            }

            // 返回
            if (AppInfo.TryGetValue(appId, out var val))
            {
                return val;
            }
            else
            {
                return null;
            }
        }

        public async Task<IEnumerable<PICSProductInfo>?> RequestPackageInfo(IEnumerable<uint> packageIds)
        {
            List<uint> packages = packageIds.ToList();
            packages.RemoveAll(pid => PackageInfo.ContainsKey(pid));

            if (bAborted) return null;
            
            if (packages.Count == 0)
            {
                return PackageInfo.Where(v => packageIds.Any(id => id == v.Key)).Select(v => v.Value);
            }

            var packageRequests = new List<PICSRequest>();
            foreach (var package in packages)
            {
                var request = new PICSRequest(package);
                
                if (PackageTokens.TryGetValue(package, out var token))
                {
                    request.AccessToken = token;
                }

                packageRequests.Add(request);
            }

            
            AsyncJobMultiple<PICSProductInfoCallback>.ResultSet? tmp;
            try
            {
                tmp = await steamApps.PICSGetProductInfo(new List<PICSRequest>(), packageRequests).ToTask();
            }
            catch
            {
                return null;
            }
            var packageInfo = tmp.Results?.FirstOrDefault();
            if (packageInfo == null) return null;

            foreach (var package_value in packageInfo.Packages)
            {
                var package = package_value.Value;
                PackageInfo[package.ID] = package;
            }

            foreach (var package in packageInfo.UnknownPackages)
            {
                PackageInfo[package] = null;
            }

            return PackageInfo.Where(v => packageIds.Any(id => id == v.Key)).Select(v => v.Value);
        }

        public async Task<bool> RequestFreeAppLicense(uint appId)
        {
            var resultInfo = await steamApps.RequestFreeLicense(appId).ToTask();
            if (resultInfo == null) return false;
            return resultInfo.GrantedApps.Contains(appId);
        }

        public async Task<byte[]?> RequestDepotKey(uint depotId, uint appid = 0)
        {
            if (bAborted) return null;
            if (DepotKeys.ContainsKey(depotId)) return DepotKeys[depotId];

            DepotKeyCallback? depotKey = null;
            try
            {
                depotKey ??= await steamApps.GetDepotDecryptionKey(depotId, appid).ToTask();

            }
            catch { }
            if (depotKey == null) return null;
            if(depotKey.Result != EResult.OK)
            {
                Abort();
                return null;
            }
            DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
            return depotKey.DepotKey;
        }

        public Dictionary<(uint, uint,ulong,string),ulong> RequestCodeMap { get; } = new();
        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (bAborted)
                return 0;

            ulong requestCode;
            var key = (depotId, appId, manifestId, branch);
            if (RequestCodeMap.ContainsKey(key)) return RequestCodeMap[key];
            try
            {
                requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);
                RequestCodeMap[key] = requestCode;
            }
            catch
            {
                return 0;
            }

            return requestCode;
        }

        public async Task CheckAppBetaPassword(uint appid, string password)
        {
            var appPassword = await steamApps.CheckAppBetaPassword(appid, password).ToTask();
            foreach (var entry in appPassword.BetaPasswords)
            {
                AppBetaPasswords[entry.Key] = entry.Value;
            }
        }

        public async Task<PublishedFileDetails?> GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
        {
            var pubFileRequest = new CPublishedFile_GetDetails_Request() { appid = appId };
            pubFileRequest.publishedfileids.Add(pubFile);

            PublishedFileDetails? details = null;

            ServiceMethodResponse? callback;
            try
            {
                callback = await steamPublishedFile.SendMessage(api => api.GetDetails(pubFileRequest));
            }
            catch (Exception)
            {
                return null;
            }
            if (callback.Result == EResult.OK)
            {
                var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
                details = response.publishedfiledetails.FirstOrDefault();
            }
            else
            {
                //throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving file details for pubfile {pubFile}.");
                details = null;
            }
            return details;
        }


        public async Task<SteamCloud.UGCDetailsCallback?> GetUGCDetails(UGCHandle ugcHandle)
        {
            SteamCloud.UGCDetailsCallback? details = null;
            var callback = await steamCloud.RequestUGCDetails(ugcHandle).ToTask();
            if (callback.Result == EResult.OK)
            {
                details = callback;
            }
            else if (callback.Result == EResult.FileNotFound)
            {
                details = null;
            }
            else
            {
                //throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC details for {ugcHandle}.");
                details = null;
            }

            return details;
        }

        public void ResetConnectionFlags()
        {
            bExpectingDisconnectRemote = false;
            bDidDisconnect = false;
            bIsConnectionRecovery = false;
            bDidReceiveLoginKey = false;
        }

        void Connect()
        {
            bAborted = false;
            bConnected = false;
            bConnecting = true;
            connectionBackoff = 0;

            ResetConnectionFlags();

            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort(bool sendLogOff = true)
        {
            Disconnect(sendLogOff);
        }

        public void Disconnect(bool sendLogOff = true)
        {
            if (sendLogOff)
            {
                try
                {
                    steamUser.LogOff();
                }
                catch { }
            }

            bAborted = true;
            bConnected = false;
            bConnecting = false;
            bIsConnectionRecovery = false;
            steamClient.Disconnect();

            // flush callbacks until our disconnected event
            while (!bDidDisconnect)
            {
                callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }

        private void Reconnect()
        {
            bIsConnectionRecovery = true;
            steamClient.Disconnect();
        }

        //public void TryWaitForLoginKey()
        //{
        //    if (logonDetails.Username == null || !credentials.LoggedOn) return;

        //    var totalWaitPeriod = DateTime.Now.AddSeconds(3);

        //    while (true)
        //    {
        //        var now = DateTime.Now;
        //        if (now >= totalWaitPeriod) break;

        //        if (bDidReceiveLoginKey) break;

        //        callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
        //    }
        //}

        private void WaitForCallbacks()
        {
            callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));

            var diff = DateTime.Now - connectTime;

            if (diff > STEAM3_TIMEOUT && !bConnected)
            {
                if (IsDebug)
                {
                    Console.WriteLine("Timeout connecting to Steam3.");
                }
                Abort();
            }
        }

        private void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            if (IsDebug)
            {
                Console.WriteLine(" Done!");
            }
            bConnecting = false;
            bConnected = true;
            if (!authenticatedUser)
            {
                if (IsDebug)
                {
                    Console.Write("Logging anonymously into Steam3...");
                }
                steamUser.LogOnAnonymous();
            }
            else
            {
                if (IsDebug)
                {
                    Console.Write("Logging '{0}' into Steam3...", logonDetails.Username);
                }
                steamUser.LogOn(logonDetails);
            }
        }

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            bDidDisconnect = true;

            // When recovering the connection, we want to reconnect even if the remote disconnects us
            if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
            {
                if (IsDebug)
                {
                    Console.WriteLine("Disconnected from Steam");
                }

                // Any operations outstanding need to be aborted
                bAborted = true;
            }
            else if (connectionBackoff >= 10)
            {
                if (IsDebug)
                {
                    Console.WriteLine("Could not connect to Steam after 10 tries");
                }
                Abort(false);
            }
            else if (!bAborted)
            {
                if (IsDebug)
                {
                    if (bConnecting)
                        Console.WriteLine("Connection to Steam failed. Trying again");
                    else
                        Console.WriteLine("Lost connection to Steam. Reconnecting");
                }

                Thread.Sleep(1000 * ++connectionBackoff);

                // Any connection related flags need to be reset here to match the state after Connect
                ResetConnectionFlags();
                steamClient.Connect();
            }
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
            var is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            var isLoginKey = logonDetails.LoginKey != null && loggedOn.Result == EResult.InvalidPassword;

            if (isSteamGuard || is2FA || isLoginKey)
            {
                bExpectingDisconnectRemote = true;
                Abort(false);

                if (IsDebug)
                {
                    if (!isLoginKey) Console.WriteLine("This account is protected by Steam Guard.");
                }

                if (is2FA)
                {
                    do
                    {
                        if (IsDebug)
                        {
                            Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                        }
                        logonDetails.TwoFactorCode = Console.ReadLine();
                    } while (String.Empty == logonDetails.TwoFactorCode);
                }
                else if (isLoginKey)
                {
                    //AccountSettingsStore.Instance.LoginKeys.Remove(logonDetails.Username);
                    //AccountSettingsStore.Save();

                    logonDetails.LoginKey = null;

                    //if (ContentDownloader.Config.SuppliedPassword != null)
                    //{
                    //    Console.WriteLine("Login key was expired. Connecting with supplied password.");
                    //    logonDetails.Password = ContentDownloader.Config.SuppliedPassword;
                    //}
                    //else
                    //{
                    //    Console.Write("Login key was expired. Please enter your password: ");
                    //    logonDetails.Password = Util.ReadPassword();
                    //}
                }
                else
                {
                    do
                    {
                        if (IsDebug)
                        {
                            Console.Write("Please enter the authentication code sent to your email address: ");
                        }
                        logonDetails.AuthCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.AuthCode);
                }

#if DEBUG
                if (IsDebug)
                {
                    Console.Write("Retrying Steam3 connection...");
                }
#endif
                Connect();

                return;
            }

            if (loggedOn.Result == EResult.TryAnotherCM)
            {
                if (IsDebug)
                {
                    Console.Write("Retrying Steam3 connection (TryAnotherCM)...");
                }

                Reconnect();

                return;
            }

            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                if (IsDebug)
                {
                    Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                }
                Abort(false);

                return;
            }

            if (loggedOn.Result != EResult.OK)
            {
                if (IsDebug)
                {
                    Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                }
                Abort();

                return;
            }

            if (IsDebug)
            {
                Console.WriteLine(" Done!");
            }

            this.seq++;
            credentials.LoggedOn = true;

            //if (ContentDownloader.Config.CellID == 0)
            //{
            //    Console.WriteLine("Using Steam3 suggested CellID: " + loggedOn.CellID);
            //    ContentDownloader.Config.CellID = (int)loggedOn.CellID;
            //}
        }

        private void SessionTokenCallback(SteamUser.SessionTokenCallback sessionToken)
        {
            if (IsDebug)
            {
                Console.WriteLine("Got session token!");
            }
            credentials.SessionToken = sessionToken.SessionToken;
        }

        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                if (IsDebug)
                {
                    Console.WriteLine("Unable to get license list: {0} ", licenseList.Result);
                }
                Abort();

                return;
            }

            if (IsDebug)
            {
                Console.WriteLine("Got {0} licenses for account!", licenseList.LicenseList.Count);
            }
            Licenses = licenseList.LicenseList;

            foreach (var license in licenseList.LicenseList)
            {
                if (license.AccessToken > 0)
                {
                    PackageTokens.TryAdd(license.PackageID, license.AccessToken);
                }
            }
        }

        private void UpdateMachineAuthCallback(SteamUser.UpdateMachineAuthCallback machineAuth)
        {
            var hash = Util.SHAHash(machineAuth.Data);
            if (IsDebug)
            {
                Console.WriteLine("Got Machine Auth: {0} {1} {2} {3}", machineAuth.FileName, machineAuth.Offset, machineAuth.BytesToWrite, machineAuth.Data.Length, hash);
            }            

            //AccountSettingsStore.Instance.SentryData[logonDetails.Username] = machineAuth.Data;
            //AccountSettingsStore.Save();

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
            steamUser.SendMachineAuthResponse(authResponse);
        }

        private void LoginKeyCallback(SteamUser.LoginKeyCallback loginKey)
        {
            if (IsDebug)
            {
                Console.WriteLine("Accepted new login key for account {0}", logonDetails.Username);
            }

            //AccountSettingsStore.Instance.LoginKeys[logonDetails.Username] = loginKey.LoginKey;
            //AccountSettingsStore.Save();

            steamUser.AcceptNewLoginKey(loginKey);

            bDidReceiveLoginKey = true;
        }

        public void Dispose()
        {
            Disconnect();
            steamClient.Disconnect();
            _httpclient.Dispose();
            //steamUser;
            //steamApps;
            //steamCloud;
            //steamPublishedFile;
            //callbacks;


            
        }
    }
}
