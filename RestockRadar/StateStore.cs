using System.Text.Json;

namespace RestockRadar
{
    class SiteState
    {
        public string? LastText { get; set; }
        public bool? InStock { get; set; }
        public DateTime? LastChecked { get; set; }
        public DateTime? LastChanged { get; set; }
        public int ConsecutiveFailures { get; set; }
        public bool FailureAlertSent { get; set; }
        public DateTime? PurchasedAt { get; set; }
    }

    // Remembers what each site looked like last time, so restarts don't trigger false alerts.
    class StateStore
    {
        private readonly string _path;
        private readonly Dictionary<string, SiteState> _sites;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public StateStore(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "state.json");
            _sites = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, SiteState>>(File.ReadAllText(_path)) ?? new()
                : new();
        }

        public SiteState Get(string site)
        {
            if (!_sites.TryGetValue(site, out var state))
                _sites[site] = state = new SiteState();
            return state;
        }

        public void Save()
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_sites, JsonOptions));
            File.Move(tmp, _path, true);
        }
    }
}
