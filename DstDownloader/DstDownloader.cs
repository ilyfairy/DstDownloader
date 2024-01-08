﻿using SteamDownloader;
using SteamDownloader.Helpers;
using SteamDownloader.WebApi;
using SteamKit2;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Ilyfairy.Tools;

public class DstDownloader : IDisposable
{
    public readonly uint ServerAppId = 343050; //饥荒服务器AppId
    public readonly uint ServerWindowsDepotId = 343051; //饥荒服务器AppId for Windows
    public readonly uint AppId = 322330; //饥荒客户端AppId
    public SteamSession Steam { get; }

    public string? AccessToken => Steam.Authentication.AccessToken;
    public bool IsCache { get => Steam.IsCache; set => Steam.IsCache = value; }

    public DstDownloader()
    {
        Steam = new();
    }
    public DstDownloader(SteamSession steam)
    {
        Steam = steam ?? throw new ArgumentNullException(nameof(steam));
    }

    /// <summary>
    /// 匿名登录
    /// </summary>
    /// <returns></returns>
    public Task LoginAsync(CancellationToken cancellationToken = default)
    {
        return Steam.Authentication.LoginAnonymousAsync(cancellationToken);
    }

    /// <summary>
    /// 账号密码登录
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="password">密码</param>
    /// <param name="shouldRememberPassword">是否记住密码, 之后可以用AccessToken登录</param>
    /// <returns></returns>
    public Task LoginAsync(string username, string password, bool shouldRememberPassword, CancellationToken cancellationToken = default)
    {
        return Steam.Authentication.LoginAsync(username, password, shouldRememberPassword, cancellationToken);
    }

    /// <summary>
    /// 使用AccessToken登录
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="accessToken">AccessToken</param>
    /// <returns></returns>
    public Task LoginAsync(string username, string accessToken, CancellationToken cancellationToken = default)
    {
        return Steam.Authentication.LoginFromAccessTokenAsync(username, accessToken, cancellationToken);
    }


    public void Dispose()
    {
        Steam.Dispose();
    }

    public async Task<long> GetServerVersionAsync(CancellationToken cancellationToken = default)
    {
        var appInfo = await Steam.GetProductInfoAsync(ServerAppId, cancellationToken).ConfigureAwait(false);
        var depotsContent = appInfo.GetProductInfoDepotsSection();
        var windst = depotsContent.DepotsInfo[ServerWindowsDepotId];
        var key = await Steam.GetDepotKeyAsync(appInfo.ID, windst.DepotId).ConfigureAwait(false);
        var manifest = await Steam.GetDepotManifestAsync(ServerAppId, windst.DepotId, windst.Manifests["public"].ManifestId, key, "public", cancellationToken).ConfigureAwait(false);
        var file = manifest.Files!.First(v => v.FileName is "version.txt");

        var bytes = await Steam.DownloadChunkDataAsync(windst.DepotId, file.Chunks.First(), key, cancellationToken).ConfigureAwait(false);
        var str = Encoding.UTF8.GetString(bytes);
        return long.Parse(str);
    }

    /// <summary>
    /// 下载饥荒服务器到指定目录
    /// </summary>
    /// <param name="platform">平台</param>
    /// <param name="dir">要下载的目录</param>
    /// <returns></returns>
    public async Task DownloadServerToDirectoryAsync(DepotsSection.OS platform, string dir, Action<FileProgress>? downloadCallback = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dir);

        var appInfo = await Steam.GetProductInfoAsync(ServerAppId, cancellationToken).ConfigureAwait(false);
        var depotsContent = appInfo.GetProductInfoDepotsSection();

        var depots = depotsContent.Where(v => v.Config?.Oslist is null or DepotsSection.OS.Unknow || v.Config.Oslist == platform);

        List<DepotManifest> manifests = new();
        foreach (var depot in depots)
        {
            if (depot.Manifests.Count == 0)
                continue;

            var manifest = await Steam.GetDepotManifestAsync(ServerAppId, depot.DepotId, depot.Manifests["public"].ManifestId, "public", cancellationToken).ConfigureAwait(false);
            manifests.Add(manifest);
        }

        var allFiles = manifests.SelectMany(v => v.Files!).Where(v => v.Flags.HasFlag(EDepotFileFlag.Directory) is false).ToList();

        int totalFileCount = allFiles.Count;
        long totalFileSize = allFiles.Sum(v => (long)v.TotalSize);

        int completedFileCount = 0;
        long completedFileSize = 0;

        foreach (var manifest in manifests)
        {
            var depotKey = await Steam.GetDepotKeyAsync(ServerAppId, manifest.DepotID).ConfigureAwait(false);

            var flagsGroup = manifest.Files!.GroupBy(v => v.Flags.HasFlag(EDepotFileFlag.Directory));
            var dirs = flagsGroup.FirstOrDefault(v => v.Key is true);
            var files = flagsGroup.FirstOrDefault(v => v.Key is false);

            if (dirs is { })
            {
                foreach (var item in dirs)
                {
                    Directory.CreateDirectory(Path.Combine(dir, item.FileName));
                }
            }

            if (files is null)
                return;

            var sizeGroup = files.OrderByDescending(v => v.TotalSize).GroupBy(v => v.TotalSize switch
            {
                <= 100 * 1024 => "100kb",
                <= 1024 * 1024 => "1mb",
                <= 10 * 1024 * 1024 => "10mb",
                _ => "max",
            });
            var files100kb = sizeGroup.FirstOrDefault(v => v.Key is "100kb");
            var files1mb = sizeGroup.FirstOrDefault(v => v.Key is "1mb");
            var files10mb = sizeGroup.FirstOrDefault(v => v.Key is "10mb");
            var filesMax = sizeGroup.FirstOrDefault(v => v.Key is "max");

            if (filesMax is { })
                await ParallelForEachAsync(filesMax, 1).ConfigureAwait(false);
            if (files10mb is { })
                await ParallelForEachAsync(files10mb, 3).ConfigureAwait(false);
            if (files1mb is { })
                await ParallelForEachAsync(files1mb, 10).ConfigureAwait(false);
            if (files100kb is { })
                await ParallelForEachAsync(files100kb, 30).ConfigureAwait(false);

            Task ParallelForEachAsync(IEnumerable<DepotManifest.FileData> fileDatas, int maxDegreeOfParallelism)
            {
                var opt = new ParallelOptions()
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                };
                return Parallel.ForEachAsync(fileDatas, opt, async (fileData, cancellationToken) =>
                {
                    var fullPath = Path.Combine(dir, fileData.FileName);
                    var d = Path.GetDirectoryName(fullPath)!;
                    if (!Directory.Exists(d))
                        Directory.CreateDirectory(d);

                    using FileStream fs = new(fullPath, FileMode.OpenOrCreate);

                    if ((long)fileData.TotalSize == fs.Length)
                    {
                        var fileSHA1 = SHA1.HashData(fs);
                        if (fileData.FileHash.SequenceEqual(fileSHA1))
                        {
                            downloadCallback?.Invoke(new FileProgress(fileData, true, Interlocked.Add(ref completedFileSize, (long)fileData.TotalSize), Interlocked.Increment(ref completedFileCount), totalFileSize, totalFileCount));
                            return;
                        }
                        fs.Seek(0, SeekOrigin.Begin);
                    }

                    await Steam.DownloadFileDataToStreamAsync(fs, manifest.DepotID, depotKey, fileData, cancellationToken).ConfigureAwait(false);
                    downloadCallback?.Invoke(new FileProgress(fileData, false, Interlocked.Add(ref completedFileSize, (long)fileData.TotalSize), Interlocked.Increment(ref completedFileCount), totalFileSize, totalFileCount));
                });
            }
        }
    }


    /// <summary>
    /// 获取Mod信息
    /// </summary>
    /// <param name="modId"></param>
    /// <returns></returns>
    public async Task<ModInfo> GetModInfoAsync(ulong modId)
    {
        WorkshopFileDetails details = await Steam.GetPublishedFileAsync(AppId, modId).ConfigureAwait(false);
        ModInfo mod = new(details);
        return mod;
    }

    /// <summary>
    /// 获取Mod信息
    /// </summary>
    /// <param name="modIds"></param>
    /// <returns></returns>
    public async Task<ModInfo[]?> GetModInfoAsync(params ulong[] modIds)
    {
        ICollection<WorkshopFileDetails>? details = await Steam.GetPublishedFileAsync(AppId, modIds).ConfigureAwait(false);
        if (details == null) return null;

        return details.Select(v => new ModInfo(v)).ToArray();
    }

    /// <summary>
    /// 下载UGC Mod到指定目录
    /// </summary>
    /// <param name="hcontent_file"><see cref="WorkshopFileDetails.HContentFile"/> or <see cref="SteamKit2.Internal.PublishedFileDetails.hcontent_file"/></param>
    /// <param name="dir">目录</param>
    /// <returns></returns>
    public async Task DownloadUGCModToDirectoryAsync(ulong hcontent_file, string dir)
    {
        var manifest = await Steam.GetWorkshopManifestAsync(AppId, hcontent_file);
        await Steam.DownloadDepotManifestToDirectoryAsync(dir, AppId, AppId, manifest).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载非UGC Mod到指定目录
    /// </summary>
    /// <param name="fileUrl">Mod文件链接</param>
    /// <param name="dir">目录</param>
    /// <returns></returns>
    public async Task<bool> DownloadNonUGCModToDirectoryAsync(string fileUrl, string dir, CancellationToken cancellationToken = default)
    {
        Stream? stream = await Steam.HttpClient.GetStreamAsync(fileUrl, cancellationToken);

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
            await zipFileStream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// 下载Mod到指定目录
    /// </summary>
    /// <param name="modId">ModID</param>
    /// <param name="dir">目录</param>
    /// <returns></returns>
    public async Task DownloadModToDirectoryAsync(ulong modId, string dir)
    {
        var info = await GetModInfoAsync(modId).ConfigureAwait(false);

        if (info.IsUGC)
        {
            await DownloadUGCModToDirectoryAsync(info.details.HContentFile, dir).ConfigureAwait(false);
        }
        else
        {
            await DownloadNonUGCModToDirectoryAsync(info.FileUrl, dir).ConfigureAwait(false);
        }
    }


    public record FileProgress(
        DepotManifest.FileData FileData,
        bool IsExist,
        long CompletedFileSize,
        int CompletedFileCount,
        long TotalFileSize,
        int TotalFileCount
        );
}