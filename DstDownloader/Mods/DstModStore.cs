using SteamKit2;
using System.Text.Json.Serialization;
using SteamDownloader.Helpers.JsonConverters;

namespace DstDownloaders.Mods;

public class DstModStore
{
    public SteamModInfo? SteamModInfo { get; set; }

    /// <summary>
    /// 为null时, 则是非 UGC Mod
    /// </summary>
    public string? ManifestSHA1 { get; set; }

    [JsonConverter(typeof(DateTimeOffsetSecondConverter))]
    public DateTimeOffset? UpdatedTime { get; set; }

    public ModInfoLua? ModInfoLua { get; set; }

    public string? ModInfoLuaSHA1 { get; set; }
    public string? ModMainLuaSHA1 { get; set; }
    public ulong WorkshopId { get; set; }

    public DepotManifest? Manifest;
}

