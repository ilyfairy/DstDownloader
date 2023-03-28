using SteamKit2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DepotDownloader
{
    public static class SteamConfig
    {
        public static void SetApiUrl(string url = "https://api.steampowered.com/")
        {
            WebAPI.DefaultBaseAddress = new(url, UriKind.Absolute);
        }
    }
}
