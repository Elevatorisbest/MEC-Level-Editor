using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LevelEditorPlugin.SubWorlds;

/// <summary>What kind of content a sub-world holds, guessed from its name (see SubWorldCatalog).</summary>
public enum SubWorldCategory
{
    StoryMissions,
    SideMissions,
    Gridnodes,
    SecurityHubs,
    CityAlerts,
    OpenWorldJobs,
    CharactersAndHideouts,
    Districts,
    CityGeometry,
    EnvironmentAndBackdrops,
    LogicAndSound,
    Other,
}

public sealed class SubWorldCategoryInfo
{
    public SubWorldCategory Category { get; }
    public string Title { get; }
    public string Description { get; }
    /// <summary>Mission and side content: left out by the "City only" preset.</summary>
    public bool IsOptionalContent { get; }

    public SubWorldCategoryInfo(SubWorldCategory category, string title, string description, bool optional)
    {
        Category = category;
        Title = title;
        Description = description;
        IsOptionalContent = optional;
    }
}

/// <summary>One sub-world asset of a level.</summary>
public sealed class SubWorldInfo
{
    /// <summary>Full asset name, e.g. Levels/SP/SP_MainCity/TheShardApproach/TheShardApproach.</summary>
    public string AssetName { get; set; }
    /// <summary>Last part of the name, e.g. TheShardApproach.</summary>
    public string ShortName { get; set; }
    public SubWorldCategory Category { get; set; }
    /// <summary>Group inside the category (a mission with its approach/briefing parts, a job phase); null for none.</summary>
    public string Group { get; set; }
    /// <summary>Sort key of the group inside its category.</summary>
    public int GroupOrder { get; set; }
}

/// <summary>
/// Lists the sub-worlds of a level from the asset list (no ebx is read, so this is instant) and
/// sorts them into categories by name.
///
/// Mirror's Edge Catalyst keeps every mission as its own sub-world of SP_MainCity, named after
/// the mission, with extra sub-worlds for its parts (…Approach, …Briefing, …BriefingApproach,
/// …Debriefing, …). Story and side mission names below are matched against the mission lists of
/// the released game; open-world jobs (OWPh&lt;phase&gt;&lt;district&gt;OppMiss/Delivery), gridnodes,
/// security hubs and city alerts follow naming patterns. Anything not recognised goes to Other
/// and is treated as part of the base city.
///
/// Zone-streamed pieces (names with _ZS_) are not listed: they are only reachable through their
/// root sub-world (CentralCityProps_ZS, CentralCityBuildings_ZS, Ground_ZS), which is.
/// </summary>
public static class SubWorldCatalog
{
    public static readonly SubWorldCategoryInfo[] Categories =
    {
        new SubWorldCategoryInfo(SubWorldCategory.StoryMissions, "Story missions",
            "Main story missions, each with its approach, briefing and debriefing parts.", true),
        new SubWorldCategoryInfo(SubWorldCategory.SideMissions, "Side missions",
            "Side missions (given by Plastic, Birdman, Nomad, Beatrix, …).", true),
        new SubWorldCategoryInfo(SubWorldCategory.Gridnodes, "Gridnodes",
            "Gridnode runs per district, with their approach parts.", true),
        new SubWorldCategoryInfo(SubWorldCategory.SecurityHubs, "Security hubs",
            "KrugerSec security hubs.", true),
        new SubWorldCategoryInfo(SubWorldCategory.CityAlerts, "City alerts",
            "City alert set-ups per district.", true),
        new SubWorldCategoryInfo(SubWorldCategory.OpenWorldJobs, "Open-world jobs",
            "OWPh… sub-worlds: opportunity missions and deliveries per story phase and district " +
            "(Dt = Downtown, Anc/A = Anchor, Ct = Construction/Rezoning, Vw = The View).", true),
        new SubWorldCategoryInfo(SubWorldCategory.CharactersAndHideouts, "Characters and hideouts",
            "Open-world characters (…_OM_Characters_…), mission givers and their lairs.", false),
        new SubWorldCategoryInfo(SubWorldCategory.Districts, "Districts",
            "District sub-worlds (Anchor, Downtown, The View, Construction, Trainstation) and their areas.", false),
        new SubWorldCategoryInfo(SubWorldCategory.CityGeometry, "City geometry (zone-streamed)",
            "The city's buildings, props and ground. The game streams their pieces by player position; " +
            "unticking a root here leaves out all of its pieces.", false),
        new SubWorldCategoryInfo(SubWorldCategory.EnvironmentAndBackdrops, "Environment and backdrops",
            "Distant city (VISTA), visual environments, backdrops, sky city, LMS… sub-worlds (purpose not confirmed).", false),
        new SubWorldCategoryInfo(SubWorldCategory.LogicAndSound, "Logic and sound",
            "Main game logic and scene sound.", false),
        new SubWorldCategoryInfo(SubWorldCategory.Other, "Other",
            "Sub-worlds whose name matches no known pattern.", false),
    };

    public static SubWorldCategoryInfo Info(SubWorldCategory category) => Categories.First(c => c.Category == category);

    // Story missions in the order the game plays them; the data name first, then the in-game title.
    private static readonly (string Name, string Title)[] StoryMissions =
    {
        ("Release", "Release"),
        ("Reunion", "Reunion"),
        ("OldFriends", "Old Friends"),
        ("BeLikeWater", "Be Like Water"),
        ("BackInTheGame", "Back in the Game"),
        ("MischiefMaker", "Mischief Maker"),
        ("SavantExtraordinaire", "Savant Extraordinaire"),
        ("Benefactor", "Benefactor"),
        ("FlyTrap", "Fly Trap"),
        ("Sanctuary", "Sanctuary"),
        ("Encroachment", "Encroachment"),
        ("ViveLaResistance", "Vive la Resistance"),
        ("Payback", "Payback"),
        ("PrisonerX", "Prisoner X"),
        ("Kingdom", "Thy Kingdom Come (Kingdom)"),
        ("FamilyMatters", "Family Matters"),
        ("TicketsPlease", "Tickets, Please"),
        ("TheShard", "The Shard"),
    };

    private static readonly (string Name, string Title)[] SideMissions =
    {
        ("TwoPigeonsWithOneStone", "Two Pigeons With One Stone"),
        ("BreakAndEntry", "Break and Entry"),
        ("DroneWorks", "Drone Works"),
        ("TheMetaGrid", "The MetaGrid"),
        ("CaughtInTheWeb", "Caught in the Web"),
        ("TopOfTheWorld", "Top of the World"),
        ("AnEarToTheGround", "An Ear to the Ground"),
        ("FingerOnThePulse", "Finger on the Pulse"),
        ("CompleteCoverage", "Complete Coverage"),
        ("ExitStrategy", "Exit Strategy"),
        ("BirdmansDelivery", "Birdman's Delivery"),
        ("BirdmansRoute", "Birdman's Route"),
    };

    private static readonly string[] CharacterNames = { "Birdman", "Nomad", "Plastic", "Rebecca", "Beatrix", "Icarus", "DogenHenchMen" };
    private static readonly string[] DistrictNames = { "Anchor", "Downtown", "TheView", "Construction", "Trainstation", "Rezoning" };

    private static readonly Regex OpenWorldJob = new Regex(@"^OWPh(\d+)([A-Za-z]+?)(OppMiss|Delivery)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DistrictArea = new Regex(@"^(" + string.Join("|", DistrictNames) + @")(Area\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Folder of a level asset ("Levels/SP/SP_MainCity/SP_MainCity" → "Levels/SP/SP_MainCity/").</summary>
    public static string LevelFolder(string levelAssetName)
    {
        int slash = levelAssetName.LastIndexOf('/');
        return slash < 0 ? "" : levelAssetName.Substring(0, slash + 1);
    }

    /// <summary>
    /// Sub-worlds below the level's folder, sorted by category and group. <paramref name="subWorldAssetNames"/>
    /// are the names of all SubWorldData assets.
    /// </summary>
    public static List<SubWorldInfo> ForLevel(string levelAssetName, IEnumerable<string> subWorldAssetNames)
    {
        string folder = LevelFolder(levelAssetName);
        List<SubWorldInfo> result = new List<SubWorldInfo>();
        foreach (string name in subWorldAssetNames)
        {
            if (folder.Length == 0 || !name.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(name, levelAssetName, StringComparison.OrdinalIgnoreCase))
                continue;
            string shortName = ShortNameOf(name);
            if (shortName.IndexOf("_ZS_", StringComparison.OrdinalIgnoreCase) >= 0)
                continue; // zone-streamed piece, reached through its _ZS root
            result.Add(Classify(name));
        }

        return result
            .OrderBy(s => (int)s.Category)
            .ThenBy(s => s.GroupOrder)
            .ThenBy(s => s.Group ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ShortName.Length)
            .ThenBy(s => s.ShortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ShortNameOf(string assetName)
    {
        int slash = assetName.LastIndexOf('/');
        return slash < 0 ? assetName : assetName.Substring(slash + 1);
    }

    public static SubWorldInfo Classify(string assetName)
    {
        string n = ShortNameOf(assetName);
        SubWorldInfo info = new SubWorldInfo { AssetName = assetName, ShortName = n, Category = SubWorldCategory.Other };

        // order matters: specific patterns before mission and district prefixes
        Match job = OpenWorldJob.Match(n);
        if (job.Success)
        {
            int phase = int.Parse(job.Groups[1].Value);
            string district = DistrictCode(job.Groups[2].Value);
            info.Category = SubWorldCategory.OpenWorldJobs;
            info.Group = $"Phase {phase}, {district}";
            info.GroupOrder = phase;
            return info;
        }
        if (Has(n, "GridNode"))
        {
            info.Category = SubWorldCategory.Gridnodes;
            string district = n.Substring(0, n.IndexOf("GridNode", StringComparison.OrdinalIgnoreCase));
            info.Group = SplitWords(district.Length > 0 ? district : n) + " gridnode";
            return info;
        }
        if (n.StartsWith("SecurityHub", StringComparison.OrdinalIgnoreCase))
        {
            info.Category = SubWorldCategory.SecurityHubs;
            return info;
        }
        if (Has(n, "CityAlert"))
        {
            info.Category = SubWorldCategory.CityAlerts;
            return info;
        }
        if (n.EndsWith("_ZS", StringComparison.OrdinalIgnoreCase))
        {
            info.Category = SubWorldCategory.CityGeometry;
            return info;
        }
        if (n.StartsWith("VISTA", StringComparison.OrdinalIgnoreCase) || n.StartsWith("VisualEnvironment", StringComparison.OrdinalIgnoreCase)
            || Has(n, "Backdrop") || n.StartsWith("SkyCity", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("LMS", StringComparison.Ordinal) || n.StartsWith("TrainstationProps", StringComparison.OrdinalIgnoreCase))
        {
            info.Category = SubWorldCategory.EnvironmentAndBackdrops;
            return info;
        }
        if (Has(n, "Logic") || Has(n, "SceneSound"))
        {
            info.Category = SubWorldCategory.LogicAndSound;
            return info;
        }
        if (Has(n, "_OM_Characters") || n.EndsWith("Characters", StringComparison.OrdinalIgnoreCase)
            || CharacterNames.Any(c => n.StartsWith(c, StringComparison.OrdinalIgnoreCase) && !SideMissions.Any(s => n.StartsWith(s.Name, StringComparison.OrdinalIgnoreCase))))
        {
            info.Category = SubWorldCategory.CharactersAndHideouts;
            return info;
        }
        if (TryMission(n, StoryMissions, out string title, out int order))
        {
            info.Category = SubWorldCategory.StoryMissions;
            info.Group = title;
            info.GroupOrder = order;
            return info;
        }
        if (TryMission(n, SideMissions, out title, out order))
        {
            info.Category = SubWorldCategory.SideMissions;
            info.Group = title;
            info.GroupOrder = order;
            return info;
        }
        if (DistrictArea.IsMatch(n))
        {
            info.Category = SubWorldCategory.Districts;
            info.Group = SplitWords(DistrictArea.Match(n).Groups[1].Value);
            return info;
        }
        return info;
    }

    /// <summary>Longest known mission name that starts the sub-world name (TheShardBriefingApproach → TheShard).</summary>
    private static bool TryMission(string name, (string Name, string Title)[] missions, out string title, out int order)
    {
        title = null;
        order = 0;
        int best = -1;
        for (int i = 0; i < missions.Length; i++)
        {
            if (name.StartsWith(missions[i].Name, StringComparison.OrdinalIgnoreCase)
                && (best < 0 || missions[i].Name.Length > missions[best].Name.Length))
                best = i;
        }
        if (best < 0)
            return false;
        title = missions[best].Title;
        order = best;
        return true;
    }

    private static string DistrictCode(string code)
    {
        switch (code.ToLowerInvariant())
        {
            case "dt": return "Downtown";
            case "anc":
            case "a": return "Anchor";
            case "ct": return "Construction (Ct)";
            case "vw": return "The View";
            default: return code;
        }
    }

    private static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>"TheViewArea3" → "The View Area 3".</summary>
    public static string SplitWords(string s)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && c != '_' && s[i - 1] != '_'
                && ((char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
                    || (char.IsDigit(c) && !char.IsDigit(s[i - 1]))))
                sb.Append(' ');
            sb.Append(c == '_' ? ' ' : c);
        }
        return sb.ToString();
    }
}
