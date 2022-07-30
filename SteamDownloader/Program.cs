using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDownloader
{

    class Program
    {
        static string MakeProgressStr(float progress, int length = 12, char start = '[', char end = ']', char elapsed = '#', char whitespace = ' ')
        {
            int elapsedChars = (int)((length - 2) * progress);
            int whiteChars = (int)((length - 2) - elapsedChars);

            return $"{start}{new string(elapsed, elapsedChars)}{new string(whitespace, whiteChars)}{end}";
        }
        static async Task Main(string[] args)
        {
            //{

            //    var client = new HttpClient();

            //    var msg = await client.SendAsync(
            //        new HttpRequestMessage(HttpMethod.Get, "https://drive.233.pink/Video/%E3%80%90%E4%B8%AD%E6%96%87%E7%89%88%E3%80%91%E5%A4%A7%E4%BE%A6%E6%8E%A2%E7%9A%AE%E5%8D%A1%E4%B8%98%EF%BC%882019%EF%BC%89.mp4"),
            //        HttpCompletionOption.ResponseHeadersRead);

            //    Console.ReadLine();
            //    long? length = msg.Content.Headers.ContentLength;
            //    byte[] buffer = new byte[1024];
            //    var stream = await msg.Content.ReadAsStreamAsync();
            //    var ms = new MemoryStream();
            //    var cpTask = stream.CopyToAsync(ms);
            //    await Task.Run(async () =>
            //    {
            //        while (!cpTask.IsCompleted)
            //        {
            //            await Task.Delay(100);

            //            float? progress = (float)ms.Position / length;
            //            Console.WriteLine($"{MakeProgressStr(progress ?? 1f, 32)} {(progress.HasValue ? progress?.ToString("P") : "(Progress unsupported)")}");
            //        }
            //    });
            //    return;
            //}


            //MainAsync(args).GetAwaiter().GetResult();


            //args = new string[] { "-app", "322330", "-pubfile", "791838548" };
            //args = new string[] { "-app", "343050" };

            //uint appId = 322330; //¼¢»ÄÓÎÏ·id
            uint serId = 343050; //¼¢»Ä·þÎñÆ÷id

            SteamDownloader steam = new();
            if (!steam.Login())
            {
                Console.WriteLine("µÇÂ¼Ê§°Ü");
                return;
            }

            //steam.Session.RequestAppInfo(322330);

            var cdn = (await steam.GetSteamCDNAsync(0))?.FirstOrDefault();
            var depots = await steam.GetDepotsInfo(serId);
            var depot = depots.First(v => v.DepotId == 343051);
            var depotManifest = await steam.GetDepotManifest(cdn, serId, depot, "public");

            var file = depotManifest.Files.FirstOrDefault(v => v.FileName == "version.txt");
            var chunk = await steam.DownloadChunk(cdn, depot.DepotId, file.Chunks.FirstOrDefault());
            Console.WriteLine(Encoding.UTF8.GetString(chunk.Data));


            //var details = steam.Session.GetPublishedFileDetails(appId, 791838548);
            //if (details.hcontent_file > 0) // is ugc
            //{

            //}
            //else
            //{
            //    //18446744073709551615
            //    //7809214632547068155
            //}



        }

     

    }
}
