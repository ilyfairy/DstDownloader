using SteamKit2;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ilyfairy.Tools;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        SteamConfig.SetApiUrl();
        DstAction dstAction = new(args);

        if (!dstAction.NoGUI)
        {
            PrintCopyright();
        }

        //显示帮助
        if (args.Length == 0)
        {
            if (!dstAction.NoGUI) PrintUsage();
            return 0;
        }

        //无事可做
        if (!dstAction.IsDownloadMod && !dstAction.IsDownloadServer && !dstAction.IsShowVersion)
        {
            Console.WriteLine("今日无事可做");
            return 0;
        }

        //登录
        Console.WriteLine("正在登录...");
        using DstDownloader? dst = await DstDownloader.LoginByParallel(8);
        if (dst == null)
        {
            Console.WriteLine("登录失败");
            Environment.Exit(-1);
            return -1;
        }
        Console.WriteLine("登录成功");
        Console.WriteLine();
        await dst.Steam.GetDefaultCDN();

        //获取版本
        if (dstAction.IsShowVersion)
        {
            Console.WriteLine("正在获取饥荒版本...");
            long? version = await dst.GetServerVersion();
            Console.WriteLine($"饥荒最新版本: {(version is null ? "获取失败" : version)}");
            Console.WriteLine();
        }

        //下载Server
        if (dstAction.IsDownloadServer)
        {
            Console.WriteLine("开始下载饥荒服务器...");
            string dir = dstAction.ServerDir!;
            Platform platform = Platform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) platform = Platform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) platform = Platform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) platform = Platform.MacOS;

            bool s = await dst.DownloadServerToDir(platform, dir, 8, 8, 50, (file, isDown) =>
            {
                Console.WriteLine($"下载 {isDown} {Path.Combine(dir, file.FileName)}");
            });
            Console.WriteLine($"饥荒Server-{platform}下载{(s ? "成功" : "失败")}");

            if (!s)
            {
                dst.Disconnect();
                Environment.Exit(-1);
                return -1;
            }
            Console.WriteLine();
        }

        int modDownloadFailedCount = 1000;

        //下载Mods
        if (dstAction.IsDownloadMod)
        {
            Console.WriteLine("开始下载Mod");
            Console.WriteLine($"ModRoot: \t{dstAction.ModRoot}");
            Console.WriteLine($"UgcModRoot: \t{dstAction.UgcModRoot}");
            foreach (var item in dstAction.Mods)
            {
                Console.WriteLine($"准备下载 Mod\t{item}");
            }
            Console.WriteLine();
            await Parallel.ForEachAsync(dstAction.Mods, async (id, token) =>
            {
                var info = await dst.GetModInfo(id);
                if (info == null)
                {
                    Console.WriteLine($"下载 Mod {id} 失败");
                    modDownloadFailedCount++;
                    return;
                }
                string dir;
                bool isSuccess;
                if (info.IsUgc)
                {
                    dir = dstAction.UgcModRoot.Replace("{id}", id.ToString(), StringComparison.InvariantCultureIgnoreCase);
                    isSuccess = await dst.DownloadUGCModToDir(info.details.hcontent_file, dir);
                }
                else
                {
                    dir = dstAction.ModRoot.Replace("{id}", id.ToString(), StringComparison.InvariantCultureIgnoreCase);
                    isSuccess = await dst.DownloadNonUGCModToDir(info.FileUrl, dir);
                }
                Console.WriteLine($"下载 Mod{(info.IsUgc?"-UGC":"")}\t{id} " + (isSuccess ? "成功" : "失败"));
            });
        }

        dst.Disconnect();
        Environment.Exit(0);
        return 0;

    }

    static void PrintCopyright()
    {
        Console.WriteLine("DstDownloader [Version 1.1.1]");
        Console.WriteLine("Copyright (c) ilyfairy. All rights reserved.");
        Console.WriteLine();
    }
    static void PrintUsage()
    {
        Console.WriteLine("   --NoGui, -n \t\t\t不显示帮助信息");
        Console.WriteLine("   --Version, -v \t\t获取饥荒版本");
        Console.WriteLine("   --Server, -s <dir>\t\t下载Server到指定目录");
        Console.WriteLine("   --ModRoot, -mr <dir>\t\t指定Mod目录, 使用{id}代替ModID");
        Console.WriteLine("   --UgcModRoot, -umr <dir>\t指定UgcMod目录, 使用{id}代替ModID");
        Console.WriteLine("   --Mod, -m <id>\t\t下载mod到指定目录, 可以指定多次");

    }

    class DstAction
    {
        public bool IsShowVersion { get; }
        public bool IsDownloadServer { get; }
        public string? ServerDir { get; }
        public bool IsDownloadMod => Mods.Count > 0;
        public bool NoGUI { get; }
        public List<ulong> Mods { get; } = new();
        public string ModRoot { get; } = "mods/{id}";
        public string UgcModRoot { get; } = "mods/{id}";
        public DstAction(string[] args)
        {
            var enumerator = args.GetEnumerator();
            while (enumerator.MoveNext())
            {
                string item = (enumerator.Current as string)!;
                if (EqualsAny(item, "-NoLogo","-nol"))
                {
                    NoGUI = true;
                }
                else if (EqualsAny(item, "-version", "-v"))
                {
                    IsShowVersion = true;
                }
                else if (EqualsAny(item, "-server", "-s"))
                {
                    if (enumerator.MoveNext())
                    {
                        string dir = (string)enumerator.Current;
                        IsDownloadServer = true;
                        ServerDir = dir;
                    }
                }
                else if (EqualsAny(item, "-mod", "-m"))
                {
                    if (enumerator.MoveNext())
                    {
                        if (!ulong.TryParse((string)enumerator.Current, out ulong id)) return;
                        Mods.Add(id);
                    }
                }
                else if (EqualsAny(item, "-ModRoot", "-mr"))
                {
                    if (enumerator.MoveNext())
                    {
                        ModRoot = (string)enumerator.Current;
                    }
                }
                else if (EqualsAny(item, "-UgcModRoot", "-umr"))
                {
                    if (enumerator.MoveNext())
                    {
                        UgcModRoot = (string)enumerator.Current;
                    }
                }
            }

        }
        private static bool EqualsAny(string text, params string[] strings)
        {
            foreach (string item in strings)
            {
                if (string.Equals(text,item, StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}