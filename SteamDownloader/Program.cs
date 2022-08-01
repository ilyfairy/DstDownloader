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
        static async Task Main(string[] args)
        {
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

            var depots = await steam.GetDepotsInfo(serId);
            var depot = depots.First(v => v.DepotId == 343051);
            var depotManifest = await steam.GetDepotManifest(serId, depot, "public");

            var file = depotManifest.Files.FirstOrDefault(v => v.FileName == "version.txt");
            var chunk = await steam.DownloadChunk(depot.DepotId, file.Chunks.FirstOrDefault());
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
