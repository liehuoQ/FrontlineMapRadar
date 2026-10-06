using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace FrontlineMapRadar;

/// <summary>各前线网页底图（嵌入资源 + Assets/maps）。</summary>
internal static class FrontlineMapAssets
{
    public const uint GloryTerritoryId = 554;
    public const uint SealRockTerritoryId = 633;
    public const uint OnsalTerritoryId = 888;
    public const uint BorderlandTerritoryId = 376;
    /// <summary>沃戈尔·奇尔特 / 沃克奇特（凯旋战）7.4。</summary>
    public const uint WorqorTerritoryId = 1313;

    /// <summary>沃戈尔·奇尔特（凯旋战）底图文件键。</summary>
    public const string WorqorMapKey = "worqor";

    private static readonly Dictionary<uint, string> Titles = new()
    {
        [BorderlandTerritoryId] = "周边遗迹群（阵地战）",
        [GloryTerritoryId] = "荣誉野（碎冰战）",
        [SealRockTerritoryId] = "尘封秘岩（争夺战）",
        [OnsalTerritoryId] = "昂萨哈凯尔（竞争战）",
        [WorqorTerritoryId] = "沃戈尔·奇尔特（凯旋战）",
    };

    public const string BorderlandMapKey = "376";

    /// <summary>领地 → 资源文件名（不含扩展名）。</summary>
    private static readonly Dictionary<uint, string> MapKeys = new()
    {
        [BorderlandTerritoryId] = BorderlandMapKey,
        [GloryTerritoryId] = "554",
        [SealRockTerritoryId] = "633",
        [OnsalTerritoryId] = "888",
        [WorqorTerritoryId] = WorqorMapKey,
    };

    private static readonly HashSet<uint> DiscoveredWorqorIds = new();
    private static readonly HashSet<uint> DiscoveredSealRockIds = new();
    private static readonly HashSet<uint> DiscoveredBorderlandIds = new();

    public static IReadOnlyCollection<uint> KnownMapTerritoryIds
    {
        get
        {
            var set = new HashSet<uint>(MapKeys.Keys);
            foreach (var id in DiscoveredWorqorIds)
                set.Add(id);
            foreach (var id in DiscoveredSealRockIds)
                set.Add(id);
            foreach (var id in DiscoveredBorderlandIds)
                set.Add(id);
            return set;
        }
    }

    public static void RegisterWorqorTerritory(uint territoryId)
    {
        if (territoryId == 0)
            return;
        DiscoveredWorqorIds.Add(territoryId);
        Titles[territoryId] = "沃戈尔·奇尔特（凯旋战）";
        MapKeys[territoryId] = WorqorMapKey;
    }

    public static void RegisterSealRockTerritory(uint territoryId)
    {
        if (territoryId == 0)
            return;
        DiscoveredSealRockIds.Add(territoryId);
        Titles[territoryId] = "尘封秘岩（争夺战）";
        MapKeys[territoryId] = "633";
    }

    public static void RegisterBorderlandTerritory(uint territoryId)
    {
        if (territoryId == 0)
            return;
        DiscoveredBorderlandIds.Add(territoryId);
        Titles[territoryId] = "周边遗迹群（阵地战）";
        MapKeys[territoryId] = BorderlandMapKey;
    }

    public static bool LooksLikeBorderlandName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("Borderland", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Carteneau", StringComparison.OrdinalIgnoreCase)
               || name.Contains("周边遗迹", StringComparison.Ordinal)
               || name.Contains("外缘遗迹", StringComparison.Ordinal)
               || name.Contains("阵地战", StringComparison.Ordinal);
    }

    public static bool LooksLikeSealRockName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("Seal Rock", StringComparison.OrdinalIgnoreCase)
               || name.Contains("尘封秘岩", StringComparison.Ordinal)
               || name.Contains("尘封密岩", StringComparison.Ordinal);
    }

    public static bool LooksLikeWorqorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("Worqor", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Chirteh", StringComparison.OrdinalIgnoreCase)
               || name.Contains("沃戈尔", StringComparison.Ordinal)
               || name.Contains("奇尔特", StringComparison.Ordinal)
               || name.Contains("沃克奇特", StringComparison.Ordinal)
               || name.Contains("凯旋战", StringComparison.Ordinal);
    }

    public static string ResolveMapKey(uint territoryId, string? placeName = null)
    {
        if (MapKeys.TryGetValue(territoryId, out var key))
            return key;

        if (LooksLikeBorderlandName(placeName) || DiscoveredBorderlandIds.Contains(territoryId))
        {
            RegisterBorderlandTerritory(territoryId);
            return BorderlandMapKey;
        }

        if (LooksLikeWorqorName(placeName) || DiscoveredWorqorIds.Contains(territoryId))
        {
            RegisterWorqorTerritory(territoryId);
            return WorqorMapKey;
        }

        if (LooksLikeSealRockName(placeName) || DiscoveredSealRockIds.Contains(territoryId))
        {
            RegisterSealRockTerritory(territoryId);
            return "633";
        }

        // 未知前线：默认荣誉野底图，避免黑屏
        return "554";
    }

    public static string GetMapUrl(uint territoryId, string? placeName = null) =>
        $"/maps/{ResolveMapKey(territoryId, placeName)}.jpg";

    public static string GetDisplayName(uint territoryId, string? sheetName)
    {
        if (LooksLikeBorderlandName(sheetName))
        {
            RegisterBorderlandTerritory(territoryId);
            return "周边遗迹群（阵地战）";
        }

        if (LooksLikeWorqorName(sheetName))
        {
            RegisterWorqorTerritory(territoryId);
            return "沃戈尔·奇尔特（凯旋战）";
        }

        if (LooksLikeSealRockName(sheetName))
        {
            RegisterSealRockTerritory(territoryId);
            return "尘封秘岩（争夺战）";
        }

        if (Titles.TryGetValue(territoryId, out var title))
            return title;

        return string.IsNullOrWhiteSpace(sheetName) ? $"Territory {territoryId}" : sheetName;
    }

    public static string MapsDirectory(string assemblyDir) =>
        Path.Combine(assemblyDir, "Assets", "maps");

    /// <summary>
    /// 羊皮纸画框底图的默认校准：把游戏 0–1 UV 映射到装饰边框内侧。
    /// </summary>
    public static MapCalibration CreateDefaultCalibration(uint territoryId)
    {
        // 周边遗迹网页底图是游戏原版地图（无羊皮纸边框）
        var key = MapKeys.TryGetValue(territoryId, out var k) ? k : null;
        if (key == BorderlandMapKey || territoryId == BorderlandTerritoryId || DiscoveredBorderlandIds.Contains(territoryId))
        {
            return new MapCalibration
            {
                OffsetX = 0,
                OffsetY = 0,
                ScaleX = 1f,
                ScaleY = 1f,
                FlipY = false,
            };
        }

        return new MapCalibration
        {
            OffsetX = 0.048f,
            OffsetY = 0.048f,
            ScaleX = 0.904f,
            ScaleY = 0.904f,
            FlipY = false,
        };
    }

    public static byte[]? TryReadJpeg(uint territoryId, params string?[] extraSearchDirs) =>
        TryReadJpegByKey(ResolveMapKey(territoryId), extraSearchDirs);

    public static byte[]? TryReadJpegByKey(string mapKey, params string?[] extraSearchDirs)
    {
        if (string.IsNullOrWhiteSpace(mapKey))
            return null;

        var fileName = $"{mapKey}.jpg";
        var resourceName = $"FrontlineMapRadar.maps.{fileName}";
        try
        {
            using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (embedded != null)
            {
                using var ms = new MemoryStream();
                embedded.CopyTo(ms);
                if (ms.Length > 0)
                    return ms.ToArray();
            }
        }
        catch
        {
            // ignore
        }

        foreach (var dir in EnumerateSearchDirs(extraSearchDirs))
        {
            try
            {
                var path = Path.Combine(dir, "Assets", "maps", fileName);
                if (File.Exists(path))
                    return File.ReadAllBytes(path);

                path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    public static string? TryGetDataUri(uint territoryId, params string?[] extraSearchDirs)
    {
        var bytes = TryReadJpeg(territoryId, extraSearchDirs);
        return bytes is { Length: > 0 }
            ? "data:image/jpeg;base64," + Convert.ToBase64String(bytes)
            : null;
    }

    private static IEnumerable<string> EnumerateSearchDirs(string?[] extraSearchDirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string?> candidates =
        [
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location),
            AppContext.BaseDirectory,
            .. extraSearchDirs,
        ];

        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch
            {
                continue;
            }

            if (seen.Add(full))
                yield return full;
        }
    }
}
