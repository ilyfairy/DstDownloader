using Ilyfairy.DstDownloaders;
using SteamDownloader;
using SteamDownloader.Helpers;
using SteamDownloader.WebApi.Interfaces;
using SteamKit2;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.IO.Compression;
using MoonSharp.Interpreter;
using DstDownloaders.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace DstDownloaders.Mods;

public class DstModsFileService : IDisposable
{
    private readonly DstDownloader dst;
    private readonly ConcurrentDictionary<ulong, InternalCache> _cache = new();
    public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromSeconds(60);

    public string ModsRoot { get; set; }
    public string StoreFileName { get; init; } = ".store.json";
    public string ManifestFileName { get; init; } = ".manifest.bin";

    public static readonly string ModInfoLuaFileName = "modinfo.lua";
    public static readonly string ModMainLuaFileName = "modmain.lua";

    public byte[]? AppDepotKey { get; set; }

    public bool IsDefaultIncludeManifest { get; set; } = false;

    public JsonSerializerOptions JsonOptions { get; }

    private Script _lua;
    private readonly bool isDstNew = false;

    public Func<Uri, Uri>? FileUrlProxy => dst.FileUrlProxy;

    public StoresCache Cache { get; }

    public DstModsFileService(DstDownloader? dstDownloader, string modsRootDirectory)
    {
        isDstNew = dstDownloader is null;
        dst = dstDownloader ?? new();
        ModsRoot = modsRootDirectory;

        JsonOptions = new JsonSerializerOptions(InterfaceBase.JsonOptions);
        JsonOptions.WriteIndented = true;


        LexerGlobalOptions.IgnoreInvalid = InvalidEscapeHandling.Keep;
        LexerGlobalOptions.UnexpectedSymbolHandling = UnexpectedSymbolHandling.Ignore;
        LexerGlobalOptions.PatternMaxCalls = 10000;

        _lua = CreateScript();

        Cache = new(this);
    }

    private Script CreateScript()
    {
        Script script;
        script = new Script(CoreModules.String | CoreModules.Math | CoreModules.Json | CoreModules.Bit32 | CoreModules.Table | CoreModules.TableIterators | CoreModules.Metatables | CoreModules.Basic);
        script.Globals["locale"] = "zh";

        script.DoString("""
            ChooseTranslationTable = function(tbl)
            	return tbl[locale] or tbl[1]
            end
            """);

        script.Options.DebugPrint = v => { };
        script.DebuggerEnabled = false;

        return script;
    }

    public async Task InitializeAsync()
    {
        if (isDstNew || !dst.Steam.SteamClient.IsConnected || dst.Steam.ContentServers.Count == 0)
        {
            await dst.LoginAsync();
            var servers = await dst.Steam.GetCdnServersAsync(1);
            var stableServers = await SteamHelper.TestContentServerConnectionAsync(dst.Steam.HttpClient, servers, TimeSpan.FromSeconds(3));
            dst.Steam.ContentServers = stableServers.ToList();
        }
    }

    public async Task RunUpdateAllAsync(Action<UpdateProgressArgs>? progress, CancellationToken cancellationToken = default)
    {
        AppDepotKey = await dst.Steam.GetDepotKeyAsync(dst.AppId, dst.AppId, cancellationToken);

        await Parallel.ForEachAsync(FastGetAllMods(cancellationToken), new ParallelOptions()
        {
            MaxDegreeOfParallelism = 5,
        }, async (item, cancellationToken) =>
        {
            if (item.IsValid is false)
            {
                progress?.Invoke(new UpdateProgressArgs()
                {
                    Type = ModsUpdateType.Failed,
                    WorkshopId = item.WorkshopId,
                    Store = null,
                    UpdateElapsed = TimeSpan.Zero,
                });
                return;
            }

            bool isExists = false;
            DstModStore? store = null;
            {
                var modsPath = Path.Combine(ModsRoot, item.WorkshopId.ToString());
                var storeFilePath = Path.Combine(modsPath, StoreFileName);
                isExists = File.Exists(storeFilePath);
            }

            if (GetOrVerifyStore(item.WorkshopId, item.UpdatedTime) != null)
            {
                progress?.Invoke(new UpdateProgressArgs()
                {
                    Type = ModsUpdateType.Valid,
                    WorkshopId = item.WorkshopId,
                    Store = store,
                    UpdateElapsed = TimeSpan.Zero,
                });
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 1; i <= 3; i++)
            {
                try
                {
                    store = await DownloadAsync(item, cancellationToken);
                    progress?.Invoke(new UpdateProgressArgs()
                    {
                        Type = isExists ? ModsUpdateType.Update : ModsUpdateType.Download,
                        WorkshopId = item.WorkshopId,
                        Store = store,
                        UpdateElapsed = sw.Elapsed,
                    });
                    if (sw.ElapsedMilliseconds > 80 * 1000)
                    {

                    }
                    return;
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i == 3)
                    {
                        if (ex is ConnectionException)
                            throw;

                        progress?.Invoke(new UpdateProgressArgs()
                        {
                            Type = ModsUpdateType.Failed,
                            WorkshopId = item.WorkshopId,
                            Store = null,
                            UpdateElapsed = sw.Elapsed,
                        });
                        return;
                    }
                }
            }

        });
    }

    public async Task<DstModStore> DownloadAsync(SteamModInfo steamModInfo, CancellationToken cancellationToken = default)
    {
        if (steamModInfo.IsValid is false)
            throw new Exception("无效Mod");

        var dir = Path.Combine(ModsRoot, steamModInfo.WorkshopId.ToString());
        string storeFilePath = Path.Combine(dir, StoreFileName);
        string manifestFilePath = Path.Combine(dir, ManifestFileName);
        string modinfoFilePath = Path.Combine(dir, ModInfoLuaFileName);
        string modmainFilePath = Path.Combine(dir, ModMainLuaFileName);

        DstModStore store = new();

        store.WorkshopId = steamModInfo.WorkshopId;
        store.SteamModInfo = await dst.GetModInfoAsync(steamModInfo.WorkshopId, cancellationToken);
        store.UpdatedTime = steamModInfo.UpdatedTime;

        Directory.CreateDirectory(dir);

        if (steamModInfo.IsUGC)
        {
            var manifest = await dst.Steam.GetDepotManifestAsync(dst.AppId, dst.AppId, steamModInfo.details.HContentFile, "public", cancellationToken);

            var modinfoFileData = manifest.Files!.FirstOrDefault(v => v.FileName == ModInfoLuaFileName);
            var modmainFileData = manifest.Files!.FirstOrDefault(v => v.FileName == ModMainLuaFileName);

            List<DepotManifest.FileData> fileList = new();

            if (modinfoFileData is null || modinfoFileData.TotalSize == 0)
                store.ModInfoLuaSHA1 = "0";
            else
                fileList.Add(modinfoFileData);

            if (modmainFileData is null || modmainFileData.TotalSize == 0)
                store.ModMainLuaSHA1 = "0";
            else
                fileList.Add(modmainFileData);

            bool fileValied = false;
            //下载并验证modinfo.lua和modmain.lua, 重试3次
            for (int i = 0; i < 3; i++)
            {
                await dst.Steam.DownloadDepotManifestToDirectoryAsync(dir, dst.AppId, AppDepotKey, fileList, cancellationToken);

                if (modinfoFileData != null && modinfoFileData.TotalSize != 0)
                {
                    if (!File.Exists(modinfoFilePath))
                        continue;

                    using var modinfofs = File.OpenRead(modinfoFilePath);
                    if (SHA1.HashData(modinfofs).SequenceEqual(modinfoFileData.FileHash) is false)
                    {
                        continue;
                    }
                    store.ModInfoLuaSHA1 = Convert.ToHexString(modinfoFileData.FileHash);
                }
                if (modmainFileData != null && modmainFileData.TotalSize != 0)
                {
                    if (!File.Exists(modmainFilePath))
                        continue;

                    using var modmainfs = File.OpenRead(modmainFilePath);
                    if (SHA1.HashData(modmainfs).SequenceEqual(modmainFileData.FileHash) is false)
                    {
                        continue;
                    }
                    store.ModMainLuaSHA1 = Convert.ToHexString(modmainFileData.FileHash);
                }

                fileValied = true;
                break;
            }

            if (fileValied is false)
                throw new Exception("文件损坏");

            MemoryStream ms = new();
            manifest.SerializeToStream(ms);
            ms.Position = 0;

            store.ManifestSHA1 = Convert.ToHexString(SHA1.HashData(ms));
            ms.Position = 0;

            using FileStream fs = new(manifestFilePath, FileMode.Create);
            await ms.CopyToAsync(fs, cancellationToken);
        }
        else
        {
            if (steamModInfo.FileUrl is null)
                throw new Exception("文件URL为空");

            var response = await dst.Steam.HttpClient.GetAsync(FileUrlProxy?.Invoke(steamModInfo.FileUrl) ?? steamModInfo.FileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            nint unmanagedPtr = 0;
            unsafe Stream GetStream()
            {
                if (steamModInfo.FileSize < 85000)
                {
                    return new MemoryStream((int)steamModInfo.FileSize);
                }
                else
                {
                    var ptr = NativeMemory.Alloc((nuint)(steamModInfo.FileSize));
                    unmanagedPtr = (nint)ptr;
                    return new UnmanagedMemoryStream((byte*)ptr, (long)steamModInfo.FileSize, (long)steamModInfo.FileSize, FileAccess.ReadWrite);
                }
            }

            try
            {
                using Stream zipCacheStream = GetStream();

                var cts = new CancellationTokenSource();
                cts.CancelAfter(10_000 + ((int)steamModInfo.FileSize / 1024 * 5)); // 10s + 200KB/s
                await response.Content.CopyToAsync(zipCacheStream, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token).Token);
                response.Dispose();

                using ZipArchive zip = new(zipCacheStream, ZipArchiveMode.Read);

                var modinfoData = zip.GetEntry(ModInfoLuaFileName);
                var modmainData = zip.GetEntry(ModMainLuaFileName);

                if (modinfoData is null)
                {
                    store.ModInfoLuaSHA1 = "0";
                }
                else
                {
                    store.ModInfoLuaSHA1 = Convert.ToHexString(SHA1.HashData(modinfoData.Open()));
                    using var modinfofs = File.Open(modinfoFilePath, FileMode.Create);
                    modinfoData.Open().CopyTo(modinfofs);
                }

                if (modmainData is null)
                {
                    store.ModMainLuaSHA1 = "0";
                }
                else
                {
                    store.ModMainLuaSHA1 = Convert.ToHexString(SHA1.HashData(modmainData.Open()));
                    using var modmainfs = File.Open(modmainFilePath, FileMode.Create);
                    modmainData.Open().CopyTo(modmainfs);
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                if (unmanagedPtr != 0)
                {
                    unsafe
                    {
                        NativeMemory.Free((void*)unmanagedPtr);
                    }
                }
            }
        }

        EnsureLuaInfo(store);
        File.WriteAllText(storeFilePath, JsonSerializer.Serialize(store, JsonOptions));

        return store;
    }

    public async IAsyncEnumerable<SteamModInfo> FastGetAllMods([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var firstTest = await dst.Steam.PublishedFileService.QueryFiles(
            numperpage: 1000,
            appid: 322330,
            return_metadata: false,
            cancellationToken: cancellationToken
            );
        uint pageItemsCount = (uint)firstTest.PublishedFileDetails!.Length;
        int allPage = (int)MathF.Ceiling(firstTest.Total / (float)pageItemsCount);
        HashSet<ulong> cache = new((int)firstTest.Total);

        for (int page = 0; page < allPage; page++)
        {
            SteamDownloader.WebApi.QueryFilesResponse? response = null;
            for (int i = 1; i <= 3; i++)
            {
                try
                {
                    response = await dst.Steam.PublishedFileService.QueryFiles(
                          page: (uint)page,
                          numperpage: pageItemsCount,
                          appid: 322330,
                          return_metadata: true,
                          return_short_description: true,
                          cancellationToken: cancellationToken
                          );
                    break;
                }
                catch (Exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i == 3)
                        throw;
                }
            }

            foreach (var details in response!.PublishedFileDetails ?? [])
            {
                SteamModInfo info = new(details);

                if (info.IsValid is false)
                    continue;

                if (cache.Contains(info.WorkshopId))
                {
                    continue;
                }
                else
                {
                    yield return info;
                }
            }
        }
    }

    public async IAsyncEnumerable<SteamModInfo> FastParallelGetAllMods([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SteamModInfo>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        var firstTest = await dst.Steam.PublishedFileService.QueryFiles(
            numperpage: 1000,
            appid: 322330,
            return_metadata: false,
            cancellationToken: cancellationToken
            );
        uint pageItemsCount = (uint)firstTest.PublishedFileDetails!.Length;
        int allPage = (int)MathF.Ceiling(firstTest.Total / (float)pageItemsCount);
        HashSet<ulong> cache = new((int)firstTest.Total);

        var parallelOptions = new ParallelOptions()
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken,
        };

        _ = Start();

        async ValueTask Start()
        {
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, allPage), parallelOptions, async (page, cancellationToken) =>
                {
                    var response = await dst.Steam.PublishedFileService.QueryFiles(
                        page: (uint)page,
                        numperpage: pageItemsCount,
                        appid: 322330,
                        return_metadata: true,
                        return_short_description: true,
                        cancellationToken: cancellationToken
                        );

                    foreach (var details in response.PublishedFileDetails ?? [])
                    {
                        SteamModInfo info = new(details);

                        if (info.IsValid is false)
                            continue;

                        bool success = false;
                        lock (cache)
                        {
                            if (cache.Contains(info.WorkshopId))
                                continue;

                            success = true;
                            cache.Add(info.WorkshopId);
                        }
                        if (success)
                        {
                            await channel.Writer.WriteAsync(info, cancellationToken);
                        }
                    }
                });
                channel.Writer.Complete();
            }
            catch (Exception e)
            {
                channel.Writer.Complete(e);
            }
        }

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public async Task<SteamModInfo> FastGetModInfoAsync(ulong workshopId)
    {
        SteamModInfo? info = null;
        if (!string.IsNullOrEmpty(dst.Steam.SteamClient.Configuration.WebAPIKey))
        {
            info = await dst.GetModInfoFromWebApiAsync(workshopId);
        }
        if (info is null || info.IsValid is false)
        {
            info = await dst.GetModInfoAsync(workshopId);
        }
        return info;
    }

    public DstModStore? GetOrVerifyStore(ulong workshopId, DateTimeOffset? updateTime = null)
    {
        var modsPath = Path.Combine(ModsRoot, workshopId.ToString());
        var storeFilePath = Path.Combine(modsPath, StoreFileName);
        var manifestFilePath = Path.Combine(modsPath, ManifestFileName);
        var modinfoFilePath = Path.Combine(modsPath, ModInfoLuaFileName);
        var modmainFilePath = Path.Combine(modsPath, ModMainLuaFileName);

        if (!Directory.Exists(ModsRoot) || !File.Exists(storeFilePath))
        {
            return null;
        }

        DstModStore? store;
        using FileStream metadataFile = File.Open(storeFilePath, FileMode.Open);
        store = JsonSerializer.Deserialize<DstModStore>(metadataFile, JsonOptions)!;

        if (store is null)
            return null;

        if (store.ModInfoLuaSHA1 is null || store.ModMainLuaSHA1 is null)
            return null;

        if (store.SteamModInfo is null)
            return null;

        if (updateTime != null && store.UpdatedTime != updateTime)
            return null;

        if (store.ManifestSHA1 == null)
        {
            if (store.SteamModInfo!.IsUGC is true) // UGC Flags不匹配
                return null;

            if (store.ModInfoLuaSHA1 != "0")
            {
                using FileStream modinfoTempFs = new(modinfoFilePath, FileMode.Open);
                if (Convert.FromHexString(store.ModInfoLuaSHA1).SequenceEqual(SHA1.HashData(modinfoTempFs)) is false)
                    return null; // modinfo.lua损坏
            }
            if (store.ModMainLuaSHA1 != "0")
            {
                using FileStream modmainTempFs = new(modmainFilePath, FileMode.Open);
                if (Convert.FromHexString(store.ModMainLuaSHA1).SequenceEqual(SHA1.HashData(modmainTempFs)) is false)
                    return null; // modmain.lua损坏
            }

            return store;
        }

        if (!File.Exists(manifestFilePath))
            return null;

        FileStream manifestFile = File.OpenRead(manifestFilePath);
        if (SHA1.HashData(manifestFile).SequenceEqual(Convert.FromHexString(store.ManifestSHA1)) is false)
            return null; //manifest损坏

        manifestFile.Position = 0;

        var manifest = DepotManifest.Deserialize(manifestFile);

        if (manifest.FilenamesEncrypted)
            return null;

        DepotManifest.FileData? modinfoFileData = null;
        DepotManifest.FileData? modmainFileData = null;

        foreach (var item in manifest.Files!)
        {
            if (item.FileName == ModInfoLuaFileName)
                modinfoFileData = item;
            else if (item.FileName == ModMainLuaFileName)
                modmainFileData = item;
        }

        if (store.ModInfoLuaSHA1 != "0" && modinfoFileData != null)
        {
            if (modinfoFileData.FileHash.SequenceEqual(Convert.FromHexString(store.ModInfoLuaSHA1)) is false)
                return null; // manifest modinfo.lua的SHA1不一致

            using FileStream modinfofs = new(modinfoFilePath, FileMode.Open);
            if (Convert.FromHexString(store.ModInfoLuaSHA1).SequenceEqual(SHA1.HashData(modinfofs)) is false)
                return null; // modinfo.lua损坏
        }

        if (store.ModMainLuaSHA1 != "0" && modmainFileData != null)
        {
            if (modmainFileData.FileHash.SequenceEqual(Convert.FromHexString(store.ModMainLuaSHA1)) is false)
                return null; // manifest modmain.lua的SHA1不一致

            using FileStream modmainfs = new(modmainFilePath, FileMode.Open);
            if (Convert.FromHexString(store.ModMainLuaSHA1).SequenceEqual(SHA1.HashData(modmainfs)) is false)
                return null; // modmain.lua损坏
        }

        if (IsDefaultIncludeManifest)
        {
            store.Manifest = manifest;
        }

        return store;
    }

    public async Task<DstModStore?> GetOrDownloadAsync(ulong workshopId, CancellationToken cancellationToken = default)
    {
        var cache = _cache.GetOrAdd(workshopId, v => new InternalCache()
        {
            DateTime = DateTimeOffset.MinValue,
            Store = null,
        });

        try
        {
            await cache.Lock.WaitAsync(cancellationToken);

            SteamModInfo temp;

            if (DateTimeOffset.Now - cache.DateTime < CacheExpiration && cache.Store != null)
            {
                return cache.Store;
            }
            else
            {
                temp = await FastGetModInfoAsync(workshopId);
                if (!temp.IsValid)
                {
                    var localStore = GetOrVerifyStore(workshopId);
                    cache.Store = localStore;
                    cache.DateTime = DateTimeOffset.Now;
                    return localStore;
                }
            }

            var store = GetOrVerifyStore(workshopId, temp.UpdatedTime);

            if (store is { })
            {
                cache.Store = store;
                cache.DateTime = DateTimeOffset.Now;
                return store;
            }

            var result = await DownloadAsync(temp, cancellationToken);

            cache.DateTime = DateTimeOffset.Now;
            cache.Store = result;
            return result;
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    public void EnsureCache()
    {
        foreach (var store in GetAllStores())
        {
            _cache[store.WorkshopId] = new InternalCache() { DateTime = DateTimeOffset.Now, Store = store };
        }
    }

    public IEnumerable<DstModStore> GetAllStores()
    {
        foreach (var dir in Directory.EnumerateDirectories(ModsRoot))
        {
            if (!ulong.TryParse(Path.GetFileName(dir), out var id))
                continue;

            var info = GetOrVerifyStore(id);

            if (info == null)
            {
                continue;
            }

            yield return info;
        }
    }

    public bool EnsureLuaInfo(DstModStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(store.SteamModInfo);

        var path = Path.Combine(ModsRoot, store.SteamModInfo.WorkshopId.ToString(), ModInfoLuaFileName);
        if (!File.Exists(path))
            return false;

        var code = File.ReadAllText(path);
        var info = GetLuaInfo(code, store.SteamModInfo.WorkshopId);

        if (info is null)
            return false;

        store.ModInfoLua = info;
        return true;
    }

    public ModInfoLua? GetLuaInfo(string luaCode, ulong id)
    {
        if (luaCode.Length == 0)
            return null;

        var table = new Table(_lua);
        foreach (var k in _lua.Globals.Keys)
        {
            table[k] = _lua.Globals[k];
        }
        table["folder_name"] = $"workshop-{id}";

        DynValue r;
        try
        {
            r = _lua.DoStringAndRemoveSource(luaCode, table);
            _lua.ClearByteCode();
        }
        catch (Exception e)
        {
            try
            {
                _lua = CreateScript();
                table = new Table(_lua);
                foreach (var k in _lua.Globals.Keys)
                {
                    table[k] = _lua.Globals[k];
                }
                table["folder_name"] = $"workshop-{id}";

                r = _lua.DoString(luaCode, table);
            }
            catch
            {
                return null;
            }
        }

        table.Remove("folder_name");
        foreach (var key in _lua.Globals.Keys)
        {
            table.Remove(key);
        }

        var author = table["author"];
        var name = table["name"];
        var description = table["description"];
        var version = table["version"]!;
        var api_version = table["api_version"];
        var api_version_dst = table["api_version_dst"];
        var configuration_options = table["configuration_options"] as Table;

        try
        {
            DstConfigurationOption[]? options = null;
            if (configuration_options != null)
            {
                options = ParseOptions(configuration_options);
            }

            ModInfoLua modInfo = new()
            {
                Author = (string)author,
                Name = name.ToString()!,
                Description = description switch
                {
                    string str => str,
                    Table _table => "",
                    _ => description?.ToString()
                },
                Version = version?.ToString(),
                ApiVersion = (int?)(double?)api_version,
                ApiVersionDst = (int?)(double?)api_version_dst,
                ConfigurationOptions = options,
            };

            return modInfo;
        }
        catch (Exception e)
        {
            return null;
        }

    }

    public DstConfigurationOption[] ParseOptions(Table table)
    {
        List<DstConfigurationOption> config_options = new(table.Length);
        foreach (var obj in table.Values)
        {
            var table_item = obj.Table;
            if (table_item is null)
                continue;

            DstConfigurationOption item = new();

            var name = ToString(table_item["name"]);
            var label = ToString(table_item["label"]);
            var default_value = table_item["default"];
            var options = table_item["options"] as Table;
            var hover = table_item["hover"];

            item.Name = name;
            item.Label = label;
            item.Default = LuaConverter.ToClrObject(default_value);
            item.Hover = ToString(hover);

            if (options != null)
            {
                List<DstConfigurationOptionItem> list = new(options.Length);
                foreach (var table_option in options.Values)
                {
                    if (table_option is null || table_option.IsNil())
                        continue;

                    if (table_option.Table is null)
                        continue;

                    DstConfigurationOptionItem option = new();
                    option.Description = ToString(table_option.Table!["description"])!;
                    var data = table_option.Table!["data"];
                    option.Data = LuaConverter.ToClrObject(data);
                    option.Hover = ToString(table_option.Table!["hover"]);

                    list.Add(option);
                }
                item.Options = list.ToArray();
            }

            config_options.Add(item);
        }
        return config_options.ToArray();

        static string? ToString(object obj)
        {
            return obj switch
            {
                string str => str,
                Table _table => "",
                double _double => _double.ToString(),
                bool _bool => _bool.ToString(),
                null => null,
                _ => ""
            };
        }
    }

    public void SaveToFile(DstModStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        JsonSerializer.Serialize(store.ModInfoLua, JsonOptions);

        var modsPath = Path.Combine(ModsRoot, store.SteamModInfo!.WorkshopId.ToString(), StoreFileName);
        File.WriteAllText(modsPath, JsonSerializer.Serialize(store, JsonOptions));
    }

    public bool IncludeManifest(DstModStore store)
    {
        if (store.SteamModInfo is null)
            return false;

        var modsPath = Path.Combine(ModsRoot, store.SteamModInfo.WorkshopId.ToString());
        var storeFilePath = Path.Combine(modsPath, StoreFileName);
        var manifestFilePath = Path.Combine(modsPath, ManifestFileName);

        if (store.ManifestSHA1 is null)
            return false;

        FileStream manifestFile = File.OpenRead(manifestFilePath);
        if (SHA1.HashData(manifestFile).SequenceEqual(Convert.FromHexString(store.ManifestSHA1)) is false)
            return false; //manifest损坏

        manifestFile.Position = 0;

        var manifest = DepotManifest.Deserialize(manifestFile);

        if (manifest.FilenamesEncrypted)
            return false;

        store.Manifest = manifest;
        return true;
    }

    public void Dispose()
    {
        _cache.Clear();
        _lua.ClearByteCode();
        if (isDstNew)
            dst.Dispose();
    }

    private class InternalCache
    {
        public DstModStore? Store { get; set; }
        public SemaphoreSlim Lock { get; } = new(1);
        public DateTimeOffset DateTime { get; set; }
    }


    public enum ModsUpdateType
    {
        /// <summary>
        /// 已存在, 并且最新
        /// </summary>
        Valid,
        /// <summary>
        /// 需要更新
        /// </summary>
        Update,
        /// <summary>
        /// 不存在, 需要下载
        /// </summary>
        Download,
        /// <summary>
        /// 下载失败
        /// </summary>
        Failed,
    }

    public class UpdateProgressArgs
    {
        public ModsUpdateType Type { get; set; }
        public ulong WorkshopId { get; set; }
        public DstModStore? Store { get; set; }
        public TimeSpan UpdateElapsed { get; set; }
    }

    public class StoresCache(DstModsFileService service) : IReadOnlyCollection<DstModStore>
    {
        public DstModStore this[ulong workshopId]
        {
            get => service._cache[workshopId].Store!;
        }

        public int Count => service._cache.Count;

        public IEnumerable<ulong> Keys => service._cache.Select(v => v.Key);

        public IEnumerable<DstModStore> Values => service._cache.Select(v => v.Value.Store!);

        public bool ContainsKey(ulong key) => service._cache.ContainsKey(key);

        public bool TryGetValue(ulong key, [MaybeNullWhen(false)] out DstModStore value)
        {
            var result = service._cache.TryGetValue(key, out var temp);
            value = temp?.Store;
            return result;
        }

        public IEnumerator<DstModStore> GetEnumerator() => service._cache.Values.Select(v => v.Store!).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
