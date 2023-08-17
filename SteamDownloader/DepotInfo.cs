using SteamKit2;

namespace DepotDownloader
{
    public class DepotInfo
    {
        public uint DepotId { get; set; }
        public string Name { get; set; }
        public Dictionary<string, ulong> Manifests { get; set; }
        public long MaxSize { get; set; }
        public uint? DepotFromApp { get; set; }
        public DepotConfig? Config { get; set; }
        public DepotInfo(KeyValue keyValue)
        {
            DepotId = uint.Parse(keyValue.Name);
            Manifests = new Dictionary<string, ulong>();
            var map = keyValue.Children.ToDictionary(v => v.Name);
            if (map.TryGetValue("name", out var name))
            {
                Name = name.Value;
            }
            if (map.TryGetValue("maxsize", out var maxsize))
            {
                MaxSize = long.Parse(maxsize.Value);
            }
            if (map.TryGetValue("depotfromapp", out var depotfromapp))
            {
                DepotFromApp = uint.Parse(depotfromapp.Value);
            }
            if (map.TryGetValue("config", out var config))
            {
                var configMap = config.Children.ToDictionary(v => v.Name);
                DepotConfig obj = new();
                if (configMap.TryGetValue("oslist", out var val))
                {
                    switch (val.Value)
                    {
                        case "windows":
                            obj.Oslist = Platform.Windows;
                            break;
                        case "linux":
                            obj.Oslist = Platform.Linux;
                            break;
                        case "macos":
                            obj.Oslist = Platform.MacOS;
                            break;
                        default:
                            obj.Oslist = Platform.Unknow;
                            break;
                    }
                }
                Config = obj;
            }
            if (map.TryGetValue("manifests", out var manifests))
            {
                var tmp = manifests.Children.ToDictionary(v => v.Name);
                foreach (var item in tmp)
                {
                    if (ulong.TryParse(item.Value.Value, out ulong manifestsId))
                    {
                        Manifests.Add(item.Key, manifestsId);
                    }

                    foreach (var v in item.Value.Children)
                    {
                        if (v.Name == "gid" && ulong.TryParse(v.Value, out manifestsId))
                        {
                            Manifests.Add(item.Key, manifestsId);
                        }
                        else if (v.Name == "size")
                        {
                            MaxSize = long.Parse(v.Value);
                        }
                        else if (v.Name == "download")
                        {

                        }
                    }
                }
            }

        }
    }
}
