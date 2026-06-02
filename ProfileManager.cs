using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LootOracle;

public class BuildProfile
{
    public string Description { get; set; } = "";
    public List<string> ComboMods { get; set; } = new();
}

public class ProfileManager
{
    private const string ProfilesFileName = "profiles.json";
    private const string BuildProfilesFileName = "build_profiles.json";
    private readonly string _configDirectory;
    private Dictionary<string, string> _profiles;
    private List<string> _cachedProfileNames;
    private Dictionary<string, BuildProfile> _buildProfiles;
    private List<string> _cachedBuildProfileNames;

    public List<string> ProfileNames => _cachedProfileNames ??= _profiles.Keys.OrderBy(x => x).ToList();
    public List<string> BuildProfileNames => _cachedBuildProfileNames ??= _buildProfiles.Keys.OrderBy(x => x).ToList();

    public ProfileManager(string configDirectory)
    {
        _configDirectory = configDirectory;
        Directory.CreateDirectory(_configDirectory);
        Load();
        LoadBuildProfiles();

        if (!_profiles.ContainsKey("Default"))
        {
            _profiles["Default"] = "";
            Save();
        }
    }

    public void Load()
    {
        var path = Path.Combine(_configDirectory, ProfilesFileName);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                _profiles = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
            }
            catch
            {
                _profiles = new() { { "Default", "" } };
            }
        }
        else
        {
            _profiles = new() { { "Default", "" } };
        }
        _cachedProfileNames = null;
    }

    public void Save()
    {
        var path = Path.Combine(_configDirectory, ProfilesFileName);
        var json = JsonConvert.SerializeObject(_profiles, Formatting.Indented);
        File.WriteAllText(path, json);
        _cachedProfileNames = null;
    }

    public string LoadProfile(string profileName)
    {
        return _profiles.TryGetValue(profileName, out var q) ? q : "";
    }

    public void SaveProfile(string profileName, string query)
    {
        _profiles[profileName] = query ?? "";
        Save();
    }

    public void DeleteProfile(string profileName)
    {
        if (profileName == "Default") return;
        if (_profiles.Remove(profileName))
            Save();
    }

    public void RenameProfile(string oldName, string newName)
    {
        if (oldName == "Default") return;
        if (_profiles.ContainsKey(newName)) return;
        if (_profiles.TryGetValue(oldName, out var query))
        {
            _profiles.Remove(oldName);
            _profiles[newName] = query;
            Save();
        }
    }

    public bool ProfileExists(string profileName) => _profiles.ContainsKey(profileName);

    public void LoadBuildProfiles()
    {
        var path = Path.Combine(_configDirectory, BuildProfilesFileName);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                _buildProfiles = JsonConvert.DeserializeObject<Dictionary<string, BuildProfile>>(json) ?? CreateDefaultBuildProfiles();
            }
            catch
            {
                _buildProfiles = CreateDefaultBuildProfiles();
            }
        }
        else
        {
            _buildProfiles = CreateDefaultBuildProfiles();
            SaveBuildProfiles();
        }
        _cachedBuildProfileNames = null;
    }

    public void SaveBuildProfiles()
    {
        var path = Path.Combine(_configDirectory, BuildProfilesFileName);
        var json = JsonConvert.SerializeObject(_buildProfiles, Formatting.Indented);
        File.WriteAllText(path, json);
        _cachedBuildProfileNames = null;
    }

    public BuildProfile GetBuildProfile(string profileName)
    {
        if (string.IsNullOrEmpty(profileName) || !_buildProfiles.TryGetValue(profileName, out var profile))
            return _buildProfiles.TryGetValue("Generic", out var generic) ? generic : new BuildProfile();
        return profile;
    }

    private static Dictionary<string, BuildProfile> CreateDefaultBuildProfiles()
    {
        return new Dictionary<string, BuildProfile>
        {
            ["Generic"] = new BuildProfile
            {
                Description = "Universal — sem combo bonus",
                ComboMods = new List<string>()
            },
            ["Ranger_IceShot"] = new BuildProfile
            {
                Description = "Ice Shot Deadeye — Obliterator Bow",
                ComboMods = new List<string> { "cold", "phys", "critical", "attackspeed", "gemlevel", "skilllevel" }
            },
            ["Monk_Quarterstaff"] = new BuildProfile
            {
                Description = "Monk — Melee Quarterstaff",
                ComboMods = new List<string> { "phys", "melee", "critical", "attackspeed", "gemlevel", "skilllevel" }
            },
            ["Stormweaver_Spark"] = new BuildProfile
            {
                Description = "Stormweaver — Spark Caster",
                ComboMods = new List<string> { "spell", "lightning", "castspeed", "critical", "gemlevel", "skilllevel", "mana" }
            },
            ["Tactician_Crossbow"] = new BuildProfile
            {
                Description = "Tactician — Elemental Crossbow",
                ComboMods = new List<string> { "fire", "cold", "lightning", "elemental", "attackspeed", "gemlevel", "skilllevel" }
            },
            ["Titan_Hammer"] = new BuildProfile
            {
                Description = "Titan — Hammer of the Gods",
                ComboMods = new List<string> { "phys", "melee", "critical", "attackspeed", "gemlevel", "skilllevel" }
            },
            ["SpiritWalker_Twister"] = new BuildProfile
            {
                Description = "Spirit Walker — Twister Huntress",
                ComboMods = new List<string> { "cold", "lightning", "attackspeed", "critical", "gemlevel", "skilllevel" }
            }
        };
    }
}
