using System.Text.Json.Serialization;

namespace DepotDownloader
{
    public class SteamCDN
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }
        [JsonPropertyName("source_id")]
        public int SourceId { get; set; }
        [JsonPropertyName("cell_id")]
        public int CellId { get; set; }
        [JsonPropertyName("load")]
        public int Load { get; set; }
        [JsonPropertyName("weighted_load")]
        public float WeightedLoad { get; set; }
        [JsonPropertyName("num_entries_in_client_list")]
        public int NumEntriesInClientList { get; set; }
        [JsonPropertyName("host")]
        public string Host { get; set; }
        [JsonPropertyName("vhost")]
        public string VHost { get; set; }
        [JsonPropertyName("https_support")]
        public string HttpsSupport { get; set; }
        [JsonPropertyName("preferred_server")]
        public bool PreferredServer { get; set; }
    }
}
