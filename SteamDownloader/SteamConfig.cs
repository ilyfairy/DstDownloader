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
            //WebAPI.DefaultBaseAddress = new(url, UriKind.Absolute);
            typeof(WebAPI).GetProperty("DefaultBaseAddress", (System.Reflection.BindingFlags)(-1)).SetValue(null, new Uri(url));
        }
    }
}
