using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamKit2;
using SteamKit2.CDN;
using static SteamKit2.DepotManifest;

namespace DepotDownloader
{
    public class SteamDownloader : IDisposable
    {
        #region 属性字段
        public Steam3Session Session { get; }
        private readonly HttpClient _client;
        public uint? CellId => Session?.steamClient?.CellID;
        public Dictionary<string, SteamCDN> CDN { get; } = new();
        private HashSet<uint> acquiredCDN = new();
        public bool IsConnected
        {
            get
            {
                return Session.steamClient?.IsConnected ?? false;
            }
        }
        #endregion

        public SteamDownloader()
        {
            _client = new();
            Session = new(new SteamUser.LogOnDetails
            {
                Username = null,
                Password = null,
                LoginKey = null,
                LoginID = 0x534B32, // "SK2"
            });
        }

        #region 方法
        public bool Login()
        {
            try
            {
                var r = Session.WaitForCredentials();
                return r.IsValid;
            }
            catch (Exception)
            {
                return false;
            }
        }
        public void Reconnect()
        {
            Session.ResetConnectionFlags();
            Session.steamClient.Connect();
        }
        public void Disconnect()
        {
            try
            {
                Session?.Disconnect();
            }
            catch (Exception)
            {

            }
        }
        public void Dispose()
        {
            Session.Disconnect();
        }
        public async Task<SteamCDN> GetDefaultCDN()
        {
            if (!acquiredCDN.Contains(Session?.steamClient?.CellID ?? 0))
            {
                var cellCDN = await GetSteamCDNAsync();
                if (cellCDN != null)
                {
                    foreach (var item in cellCDN)
                    {
                        CDN[item.Host] = item;
                    }
                }
            }
            acquiredCDN.Add(Session?.steamClient?.CellID ?? 0);

            uint random = (uint)Random.Shared.Next(0, 5000);
            if (random <= 500 && !acquiredCDN.Contains(random))
            {
                var randomCDN = await GetSteamCDNAsync();
                if (randomCDN != null)
                {
                    foreach (var item in randomCDN)
                    {
                        CDN[item.Host] = item;
                    }
                }
                acquiredCDN.Add(random);
            }

            return CDN.Values.ToList()[Random.Shared.Next(0, CDN.Count)];
        }
        public async Task<bool> AccountHasAccess(uint depotId)
        {
            if (!IsConnected) Reconnect();
            if (Session == null || Session.steamUser.SteamID == null || (Session.Licenses == null && Session.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint>? licenseQuery;
            if (Session.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = new List<uint> { 17906 };
            }
            else
            {
                licenseQuery = Session.Licenses?.Select(x => x.PackageID).Distinct();
            }
            if (licenseQuery == null) return false;

            await Session.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                if (Session.PackageInfo.TryGetValue(license, out package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;

                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;
                }
            }

            return false;
        }
        private KeyValue? GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (!IsConnected) Reconnect();
            if (Session.AppInfo == null) return null;

            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
            if (!Session.AppInfo.TryGetValue(appId, out app) || app == null)
            {
                return null;
            }

            var appinfo = app.KeyValues;

            string section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };

            var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        public async Task<byte[]?> DownloadManifestBytesAsync(uint depotId, ulong manifestId, ulong? manifestRequestCode = null, int retry = 3)
        {
            var cdn = await GetDefaultCDN();
            retry++;
            if (manifestRequestCode == 0) manifestRequestCode = null;
            for (int i = 0; i < retry; i++)
            {
                try
                {
                    string url = $"http://{cdn?.Host}/depot/{depotId}/manifest/{manifestId}/5/{manifestRequestCode}";
                    return await _client.GetByteArrayAsync(url);
                }
                catch (Exception)
                {
                    cdn = await GetDefaultCDN();
                }
            }
            return null;
        }

        public async Task<SteamCDN[]?> GetSteamCDNAsync(uint? cellId = null)
        {
            cellId ??= CellId ?? 0;
            try
            {
                string json = await _client.GetStringAsync($"http://steamapi.233.pink/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={cellId}");
                JsonNode? obj = JsonNode.Parse(json);
                if (obj is null) return null;
                return JsonSerializer.Deserialize<SteamCDN[]>(obj["response"]?["servers"]);
            }
            catch (Exception)
            {
                if (cellId != 0) return await GetSteamCDNAsync(0);
                return null;
            }
        }

        public async Task<DepotInfo[]?> GetDepotsInfo(uint appId)
        {
            await Session.RequestAppInfo(appId);
            var values = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (values is null) return null;
            List<DepotInfo> list = new(8);
            foreach (var item in values.Children)
            {
                if (uint.TryParse(item.Name, out _))
                {
                    list.Add(new DepotInfo(item));
                }
            }
            return list.ToArray();
        }

        public async Task<bool> DownloadManifestFilesToDir(uint depotId, DepotManifest manifest, string downloadDir, int fileMaxParallelism = 4, int chunkMaxParallelism = 4, int chunkRetry = 10, Action<FileData, bool>? fileDownloadedCallback = null)
        {
            CancellationTokenSource cts = new();
            ParallelOptions options = new();
            options.CancellationToken = cts.Token;
            options.MaxDegreeOfParallelism = fileMaxParallelism;

            bool down = true;

            //创建目录
            var dirs = manifest.Files.Where(v => (v.Flags & EDepotFileFlag.Directory) == EDepotFileFlag.Directory).ToArray();
            foreach (var dirData in dirs)
            {
                string path = Path.Combine(downloadDir, dirData.FileName);
                Directory.CreateDirectory(path);
                manifest.Files.Remove(dirData);
            }

            //下载文件
            try
            {
                await Parallel.ForEachAsync(manifest.Files, options, async (file, token) =>
                {
                    if (!down) return;
                    string path = Path.Combine(downloadDir, file.FileName);
                    bool downOne = await DownloadFileData(path, depotId, file, chunkMaxParallelism, chunkRetry);
                    fileDownloadedCallback?.Invoke(file, downOne);
                    if (!downOne)
                    {
                        down = false;
                        cts.Cancel();
                        return;
                    }
                });
            }
            catch { }
            if (!down)
            {
                return false;
            }

            return true;
        }
        /// <summary>
        /// 下载FileData到文件
        /// </summary>
        /// <param name="path"></param>
        /// <param name="cdn"></param>
        /// <param name="depotId"></param>
        /// <param name="fileData"></param>
        /// <returns></returns>
        public async Task<bool> DownloadFileData(string path, uint depotId, FileData fileData, int chunkMaxParallelism = 4, int chunkRetry = 10)
        {
            FileStream fs;
            //文件存在时 如果Hash一样则跳过
            if (File.Exists(path))
            {
                fs = new FileStream(path, FileMode.Open);
                if (fs.Length == fs.Length)
                {
                    var origin = SHA1.Create().ComputeHash(fs);
                    if (Util.BytesCompare(fileData.FileHash, origin)) return true;
                }
                fs.Seek(0, SeekOrigin.Begin);
                fs.SetLength(0);
            }
            else
            {
                fs = new FileStream(path, FileMode.Create);
            }

            CancellationTokenSource cts = new();
            bool isSuccess = true;
            ParallelOptions options = new();
            options.MaxDegreeOfParallelism = chunkMaxParallelism;

            //并行下载
            await Parallel.ForEachAsync(fileData.Chunks, options, async (chunk, v) =>
            {
                if (!isSuccess) return;
                DepotChunk? data = await DownloadChunk(depotId, chunk, chunkRetry);
                if (data == null)
                {
                    //如果下载失败
                    cts.Cancel();
                    isSuccess = false;
                    return;
                }
                lock (fs)
                {
                    //写入下载数据
                    fs.Position = (long)chunk.Offset;
                    fs.Write(data.Data);
                }
            });

            return isSuccess;
        }
        public async Task<DepotChunk?> DownloadChunk(uint depotId, DepotManifest.ChunkData chunk, int retry = 10)
        {
            retry++;
            var cdn = await GetDefaultCDN();
            for (int i = 0; i < retry; i++)
            {
                try
                {
                    string url = $"http://{cdn?.Host}/depot/{depotId}/chunk/{BitConverter.ToString(chunk.ChunkID).Replace("-", "")}";
                    var data = await _client.GetByteArrayAsync(url);
                    DepotChunk? r = new(chunk, data);
                    r.Process(Session.DepotKeys[depotId]);
                    return r;
                }
                catch (Exception e)
                {
                    cdn = await GetDefaultCDN();
                }
            }
            return null;
        }

        public async Task<DepotManifest?> GetDepotManifest(uint appId, DepotInfo depotInfo, string branch = "public")
        {
            var code = await Session.GetDepotManifestRequestCodeAsync(depotInfo.DepotId, appId, depotInfo.Manifests[branch], branch);
            if (code == 0) return null;
            byte[]? data = await DownloadManifestBytesAsync(depotInfo.DepotId, depotInfo.Manifests[branch], code, 5);
            if (data == null) return null;
            data = ZipUtil.Decompress(data);
            var depotManifest = new DepotManifest(data);
            var key = await Session.RequestDepotKey(depotInfo.DepotId, appId);
            if (key == null) return null;
            depotManifest.DecryptFilenames(key);
            return depotManifest;
        }
        public async Task<DepotManifest?> GetDepotManifest(uint appId, uint depotId, ulong manifestId, string branch = "public")
        {
            var code = await Session.GetDepotManifestRequestCodeAsync(depotId, appId, manifestId, branch);
            if (code == 0) return null;
            byte[]? data = await DownloadManifestBytesAsync(depotId, manifestId, code, 5);
            if (data == null) return null;
            data = ZipUtil.Decompress(data);
            var depotManifest = new DepotManifest(data);
            var key = await Session.RequestDepotKey(depotId, appId);
            if (key == null) return null;
            depotManifest.DecryptFilenames(key);
            return depotManifest;
        }
        #endregion

        #region 静态工具方法
        public static async Task<(SteamDownloader? steam, T? t)> LoginByParallel<T>(int count = 5, CancellationToken? cancellationToken = null, Func<SteamDownloader, Task<T?>>? condition = null)
        {
            SteamDownloader? dst = null;
            T? t = default;
            int tasked = 0;
            AutoResetEvent? are = new(false);
            HashSet<SteamDownloader> dsts = new(count);

            for (int i = 0; i < count; i++)
            {
                _ = Task.Run((async () =>
                {
                    SteamDownloader tmp = new();
                    dsts.Add(tmp);
                    try
                    {
                        if (tmp.Login())
                        {
                            T? r = default;
                            if (condition != null)
                            {
                                r = await condition(tmp);
                                if (r == null) return;
                            }
                            lock (are)
                            {
                                if (dst != null) return;
                                dsts.Remove(tmp);
                                dst = tmp;
                                t = r;
                                are.Set();
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        lock (are)
                        {
                            tasked++;
                        }
                        if (dst != tmp) tmp.Disconnect();
                        if (tasked == count) are.Set();
                    }
                }));
            }
            if (cancellationToken != null)
            {
                _ = Task.Run(async () =>
                {
                    while (!cancellationToken.Value.IsCancellationRequested)
                    {
                        await Task.Delay(100);
                    }
                    are.Set();
                });
            }
            await Task.Run(() => are.WaitOne());
            foreach (var tmp in dsts)
            {
                tmp.Disconnect();
            }
            return (dst, t);
        }

        public static async Task<SteamDownloader?> LoginByParallel(int count = 5, CancellationToken? cancellationToken = null)
        {
            return (await LoginByParallel<int>(count, cancellationToken, null)).steam;
        }
        #endregion
    }
}
