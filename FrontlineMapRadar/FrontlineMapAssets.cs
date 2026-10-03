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

    /// <summary>领地 → 资源文件名（不含扩展名）。</summary>
    private static readonly Dictionary<uint, string> MapKeys = new()
    {
        [GloryTerritoryId] = "554",
        [SealRockTerritoryId] = "633",
        [OnsalTerritoryId] = "888",
        [WorqorTerritoryId] = WorqorMapKey,
    };

    private static readonly HashSet<uint> DiscoveredWorqorIds = new();

    public static IReadOnlyCollection<uint> KnownMapTerritoryIds
    {
        get
        {
            var set = new HashSet<uint>(MapKeys.Keys);
            foreach (var id in DiscoveredWorqorIds)
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

        if (LooksLikeWorqorName(placeName) || DiscoveredWorqorIds.Contains(territoryId))
        {
            RegisterWorqorTerritory(territoryId);
            return WorqorMapKey;
        }

        // 未知前线：默认荣誉野底图，避免黑屏
        return "554";
    }

    public static string GetMapUrl(uint territoryId, string? placeName = null) =>
        $"/maps/{ResolveMapKey(territoryId, placeName)}.jpg";

    public static string GetDisplayName(uint territoryId, string? sheetName)
    {
        if (LooksLikeWorqorName(sheetName))
        {
            RegisterWorqorTerritory(territoryId);
            return "沃戈尔·奇尔特（凯旋战）";
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
        // 四张资源均为带装饰边框的正方形羊皮纸图，留白比例接近
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
