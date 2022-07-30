using DepotDownloader;
using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.ConstrainedExecution;
using System.Security.Cryptography;
using System.Text;
using static SteamKit2.DepotManifest;
using static SteamKit2.SteamApps.PICSProductInfoCallback;

namespace IlyfairyLib.Tools
{
    public class DstDownloader : IDisposable
    {
        #region 属性字段
        public const uint ServerId = 343050; //饥荒服务器id
        public const uint ServerWindowsDepotId = 343051; //饥荒服务器id for Windows
        public const uint AppId = 322330; //饥荒客户端id
        public SteamDownloader Steam { get; } = new();
        public bool IsConnected => Steam.IsConnected;
        private readonly HttpClient httpClient = new();
        #endregion



        #region 构造
        public DstDownloader()
        {
            Steam = new();
        }
        public DstDownloader(SteamDownloader steam)
        {
            Steam = steam ?? throw new ArgumentNullException(nameof(steam));
        }
        #endregion



        #region 方法
        public bool Login() => Steam.Login();
        public void Reconnect() => Steam.Reconnect();
        public void Disconnect() => Steam.Disconnect();
        public void Dispose()
        {
            Disconnect();
        }
        public async Task<long?> GetServerVersion()
        {
            try
            {
                DepotInfo[]? depots = await Steam.GetDepotsInfo(ServerId);
                if (depots == null) return null;
                var depot = depots.First(v => v.DepotId == ServerWindowsDepotId);
                if (depot == null) return null;
                var depotManifest = await Steam.GetDepotManifest(ServerId, depot, "public");
                if (depotManifest == null) return null;
                var file = depotManifest.Files.FirstOrDefault(v => v.FileName == "version.txt");
                if (file == null) return null;
                var chunk = await Steam.DownloadChunk(depot.DepotId, file.Chunks.FirstOrDefault());
                if (chunk == null) return null;
                string version = Encoding.UTF8.GetString(chunk.Data).TrimEnd();
                return long.Parse(version);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 获取服务器的清单文件
        /// </summary>
        /// <param name="platform">平台</param>
        /// <returns></returns>
        public async Task<(DepotInfo depot, DepotManifest manifest)[]?> GetServerManifests(Platform platform, int retry = 3)
        {
            PICSProductInfo? info = null;
            retry++;
            for (int i = 0; i < retry; i++)
            {
                info = await Steam.Session.RequestAppInfo(ServerId);
                if (info != null) break;
            }
            if (info == null)
            {
                return null;
            }

            List<DepotInfo> depots = new();
            var tmp = await Steam.GetDepotsInfo(ServerId);
            if (tmp == null)
            {
                return null;
            }
            foreach (var item in tmp)
            {
                if (item.Config?.Oslist != platform) continue;
                if (!await Steam.AccountHasAccess(item.DepotId)) continue;
                depots.Add(item);
            }

            List<(DepotInfo, DepotManifest)> infos = new();
            foreach (var depot in depots)
            {
                var manifest = await Steam.GetDepotManifest(ServerId, depot);
                if (manifest == null)
                {
                    return null;
                }
                infos.Add((depot, manifest));
            }
            return infos.ToArray();
        }

        /// <summary>
        /// 下载饥荒服务器到指定目录
        /// </summary>
        /// <param name="platform">平台</param>
        /// <param name="downloadDir">要下载的目录</param>
        /// <returns></returns>
        public async Task<bool> DownloadServerToDir(Platform platform, string downloadDir = "DoNot Starve Together Dedicated Server", int fileMaxParallelism = 4, int chunkMaxParallelism = 4, int chunkRetry = 10, Action<FileData, bool>? fileDownloadedCallback = null)
        {
            if (string.IsNullOrWhiteSpace(downloadDir)) return false;
            try
            {
                Directory.CreateDirectory(downloadDir);
            }
            catch (Exception)
            {
                return false;
            }

            var infos = await GetServerManifests(platform);
            if (infos == null || infos.Length == 0)
            {
                return false;
            }

            foreach (var item in infos)
            {
                bool downOne = await Steam.DownloadManifestFilesToDir(item.depot.DepotId,item.manifest, downloadDir, fileMaxParallelism, chunkMaxParallelism, chunkRetry, fileDownloadedCallback);
                if (!downOne) return false;
            }

            return true;
        }

   
        /// <summary>
        /// 获取Mod信息
        /// </summary>
        /// <param name="modId"></param>
        /// <returns></returns>
        public async Task<ModInfo?> GetModInfo(ulong modId)
        {
            PublishedFileDetails? details = await Steam.Session.GetPublishedFileDetails(AppId, modId);
            if (details == null) return null;

            ModInfo mod = new(details);

            return mod;
        }

        /// <summary>
        /// 下载UGC Mod到指定目录
        /// </summary>
        /// <param name="hcontent_file">ModInfo.details.hcontent_file</param>
        /// <param name="dir">目录</param>
        /// <returns></returns>
        public async Task<bool> DownloadUGCModToDir(ulong hcontent_file, string dir)
        {
            var manifest = await Steam.GetDepotManifest(AppId, AppId, hcontent_file);
            if (manifest == null) return false;
            bool s = await Steam.DownloadManifestFilesToDir(AppId, manifest, dir, 8, 8, 20);
            return s;
        }
        /// <summary>
        /// 下载非UGC Mod到指定目录
        /// </summary>
        /// <param name="fileUrl">Mod文件链接</param>
        /// <param name="dir">目录</param>
        /// <returns></returns>
        public async Task<bool> DownloadNonUGCModToDir(string fileUrl, string dir)
        {
            Stream stream;
            try
            {
                stream = await httpClient.GetStreamAsync(fileUrl);
            }
            catch (Exception)
            {
                return false;
            }
            ZipArchive zip = new(stream, ZipArchiveMode.Read);
            foreach (var item in zip.Entries)
            {
                string path = Path.Combine(dir, item.FullName);
                var c = Path.GetDirectoryName(path);
                Directory.CreateDirectory(c);
                var tmp = Path.GetFullPath(c);
                FileStream fs;
                if (File.Exists(path))
                {
                    fs = new(path, FileMode.Open);
                    byte[] fsSha1 = SHA1.Create().ComputeHash(fs);
                    byte[] dwSha1 = SHA1.Create().ComputeHash(item.Open());
                    if (Util.BytesCompare(fsSha1, dwSha1))
                    {
                        fs.Close();
                        continue;
                    }
                    else
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                        fs.SetLength(0);
                    }
                }
                else
                {
                    fs = new(path, FileMode.Create);
                }
                await item.Open().CopyToAsync(fs);
            }
            return true;
        }
        /// <summary>
        /// 下载Mod到指定目录
        /// </summary>
        /// <param name="modId">ModID</param>
        /// <param name="dir">目录</param>
        /// <returns></returns>
        public async Task<bool> DownloadModToDir(ulong modId, string dir)
        {
            var info = await GetModInfo(modId);
            if (info is null) return false;
            if (info.IsUgc)
            {
                return await DownloadUGCModToDir(info.details.hcontent_file, dir);
            }
            else
            {
                return await DownloadNonUGCModToDir(info.FileUrl, dir);
            }
        }
        #endregion



        #region 静态工具方法
        public static async Task<long?> GetVersionByParallel(int count = 5, CancellationToken? cancellationToken = null)
        {
            long? version = null;
            int tasked = 0;
            AutoResetEvent? are = new(false);
            List<DstDownloader> dsts = new(count);
            for (int i = 0; i < count; i++)
            {
                _ = Task.Run((async () =>
                {
                    DstDownloader dst = new();
                    dsts.Add(dst);
                    try
                    {
                        if (!dst.Login()) return null;
                        if (version != null) return null;
                        long? ver = await dst.GetServerVersion();
                        if (ver != null)
                        {
                            lock (are)
                            {
                                version = ver;
                                are.Set();
                            }
                        }
                        return ver;
                    }
                    catch { return null; }
                    finally
                    {
                        lock (are)
                        {
                            tasked++;
                            dst.Disconnect();
                            if (tasked == count) are.Set();
                        }
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
            foreach (var dst in dsts)
            {
                dst.Disconnect();
            }
            return version;
        }
        public static async Task<long?> GetVersionByParallel(int count = 5, int timeout = 30000)
        {
            CancellationTokenSource cts = new();
            cts.CancelAfter(timeout);
            return await GetVersionByParallel(count, cts.Token);
        }
        public static async Task<DstDownloader?> LoginByParallel(int count = 5, CancellationToken? cancellationToken = null)
        {
            var r = await SteamDownloader.LoginByParallel<int>(count, cancellationToken);
            if (r.steam == null) return null;
            return new DstDownloader(r.steam!);
        }
        public static async Task<(DstDownloader?, long?)> LoginAndGetVersionByParallel(int count = 5, CancellationToken? cancellationToken = null)
        {
            var r = await SteamDownloader.LoginByParallel(count, cancellationToken, async (steam) =>
            {
                return await new DstDownloader(steam).GetServerVersion();
            });
            if (r.steam == null) return (null, null);
            return (new DstDownloader(r.steam), r.t);
        }

        #endregion
    }
}