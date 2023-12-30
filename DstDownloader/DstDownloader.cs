using SteamDownloader;
using SteamKit2;
using SteamKit2.Internal;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static SteamKit2.DepotManifest;
using static SteamKit2.SteamApps.PICSProductInfoCallback;

namespace Ilyfairy.Tools;

public class DstDownloader : IDisposable
{
    public const uint ServerAppId = 343050; //饥荒服务器AppId
    public const uint ServerWindowsDepotId = 343051; //饥荒服务器AppId for Windows
    public const uint AppId = 322330; //饥荒客户端AppId
    public SteamSession Steam { get; }
    private static readonly HttpClient httpClient = new();


    public DstDownloader()
    {
        Steam = new();
    }
    public DstDownloader(SteamSession steam)
    {
        Steam = steam ?? throw new ArgumentNullException(nameof(steam));
    }

    public void Dispose()
    {
        Steam.Dispose();
    }

    public async Task<long> GetServerVersion()
    {
        var appInfo = await Steam.GetAppInfoAsync(ServerAppId);
        var depotsContent = Steam.GetAppInfoDepotsSection(appInfo);
        var windst = depotsContent.DepotsInfo[ServerWindowsDepotId];
        var manifest = await Steam.GetDepotManifestAsync(ServerAppId, windst.DepotId, windst.Manifests["public"].ManifestId, "public");
        var file = manifest.Files!.First(v => v.FileName is "version.txt");

        var bytes = await Steam.DownloadChunkDecryptBytesAsync(windst.DepotId, file.Chunks.First());
        var str = Encoding.UTF8.GetString(bytes);
        return long.Parse(str);
    }

    /// <summary>
    /// 下载饥荒服务器到指定目录
    /// </summary>
    /// <param name="platform">平台</param>
    /// <param name="downloadDir">要下载的目录</param>
    /// <returns></returns>
    public async Task DownloadServerToDir(DepotsContent.OS platform, string downloadDir = "DoNot Starve Together Dedicated Server", int fileMaxParallelism = 4, int chunkMaxParallelism = 4, int chunkRetry = 10, Action<FileData, bool>? fileDownloadedCallback = null)
    {
        Directory.CreateDirectory(downloadDir);

        var appInfo = await Steam.GetAppInfoAsync(ServerAppId);
        var depotsContent = Steam.GetAppInfoDepotsSection(appInfo);

        var depots = depotsContent.Where(v => v.Config?.Oslist is null or DepotsContent.OS.Unknow || v.Config.Oslist == platform);
        foreach (var item in depots)
        {
            if (item.Manifests.Count > 0)
            {
                var manifest = await Steam.GetDepotManifestAsync(ServerAppId, item.DepotId, item.Manifests["public"].ManifestId, "public");
                await Steam.DownloadDepotManifestToDirectoryAsync(downloadDir, item.DepotId, manifest);
            }
        }
    }


    /// <summary>
    /// 获取Mod信息
    /// </summary>
    /// <param name="modId"></param>
    /// <returns></returns>
    public async Task<ModInfo> GetModInfo(ulong modId)
    {
        PublishedFileDetails details = await Steam.GetPublishedFileAsync(AppId, modId);
        ModInfo mod = new(details);
        return mod;
    }

    /// <summary>
    /// 获取Mod信息
    /// </summary>
    /// <param name="modIds"></param>
    /// <returns></returns>
    public async Task<ModInfo[]?> GetModInfo(params ulong[] modIds)
    {
        ICollection<PublishedFileDetails>? details = await Steam.GetPublishedFileAsync(AppId, modIds);
        if (details == null) return null;

        return details.Select(v => new ModInfo(v)).ToArray();
    }

    /// <summary>
    /// 下载UGC Mod到指定目录
    /// </summary>
    /// <param name="hcontent_file">ModInfo.details.hcontent_file</param>
    /// <param name="dir">目录</param>
    /// <returns></returns>
    public async Task DownloadUGCModToDirectory(ulong hcontent_file, string dir)
    {
        var manifest = await Steam.GetWorkshopManifestAsync(AppId, hcontent_file);
        await Steam.DownloadDepotManifestToDirectoryAsync(dir, AppId, manifest);
    }

    /// <summary>
    /// 下载非UGC Mod到指定目录
    /// </summary>
    /// <param name="fileUrl">Mod文件链接</param>
    /// <param name="dir">目录</param>
    /// <returns></returns>
    public async Task<bool> DownloadNonUGCModToDirectoryAsync(string fileUrl, string dir, CancellationToken cancellationToken = default)
    {
        Stream? stream = await httpClient.GetStreamAsync(fileUrl, cancellationToken);

        ZipArchive zip = new(stream, ZipArchiveMode.Read);
        foreach (var item in zip.Entries)
        {
            string path = Path.Combine(dir, item.FullName);
            var c = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(c);

            FileStream fs = new(path, FileMode.OpenOrCreate);

            if(fs.Length == item.Length)
            {
                using var tempStream = item.Open();
                byte[] fsSha1 = SHA1.Create().ComputeHash(fs);
                byte[] dwSha1 = SHA1.Create().ComputeHash(tempStream);
                if (fsSha1.AsSpan().SequenceEqual(dwSha1))
                {
                    continue;
                }
            }
            else
            {
                fs.SetLength(item.Length);
                fs.Position = 0;
            }

            using var zipFileStream = item.Open();
            await zipFileStream.CopyToAsync(fs, cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// 下载Mod到指定目录
    /// </summary>
    /// <param name="modId">ModID</param>
    /// <param name="dir">目录</param>
    /// <returns></returns>
    public async Task DownloadModToDir(ulong modId, string dir)
    {
        var info = await GetModInfo(modId);

        if (info.IsUGC)
        {
            await DownloadUGCModToDirectory(info.details.hcontent_file, dir);
        }
        else
        {
            await DownloadNonUGCModToDirectoryAsync(info.FileUrl, dir);
        }
    }
}