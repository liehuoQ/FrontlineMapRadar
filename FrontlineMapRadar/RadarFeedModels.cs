using System.Collections.Generic;

namespace FrontlineMapRadar;

/// <summary>推送到平板网页的雷达快照（JSON）。</summary>
internal sealed class RadarFeedState
{
    public long Ts { get; set; }
    public bool InFrontline { get; set; }
    public bool Active { get; set; }
    public uint TerritoryId { get; set; }
    public string TerritoryName { get; set; } = string.Empty;
    /// <summary>网页底图路径，如 /maps/554.jpg；无底图时为空。</summary>
    public string? MapImageUrl { get; set; }

    /// <summary>当前领地的坐标校准（网页实时应用）。</summary>
    public MapCalibrationDto? Calibration { get; set; }

    public string OwnGc { get; set; } = "unknown";
    public string OwnGcName { get; set; } = "未知";
    public RadarFeedPlayer? Self { get; set; }
    public List<RadarFeedPlayer> Players { get; set; } = [];
    /// <summary>游戏内场景标记 A/B/C/D/1/2/3/4（已放置的）。</summary>
    public List<RadarFeedMarker> Markers { get; set; } = [];
    /// <summary>三阵营出生台（徽章位置；含是否己方）。</summary>
    public List<RadarFeedBase> Bases { get; set; } = [];
    public int LoadedCount { get; set; }
    public int RosterCount { get; set; }
}

internal sealed class RadarFeedBase
{
    public string Gc { get; set; } = "unknown";
    public string GcName { get; set; } = "未知";
    public bool IsOwn { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
}

internal sealed class RadarFeedMarker
{
    public int Index { get; set; }
    public string Label { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
}

internal sealed class RadarFeedPlayer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Job { get; set; } = string.Empty;
    public string JobAbbr { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Gc { get; set; } = "unknown";
    public string GcName { get; set; } = "未知";
    public string RoleAction { get; set; } = string.Empty;
    public bool IsSelf { get; set; }

    /// <summary>本阵营（自己/小队/同盟姓名栏，或已识别为本阵营 GC）。</summary>
    public bool IsAlly { get; set; }

    /// <summary>地图归一化坐标 0–1（相对当前区域 Map 贴图）。</summary>
    public float X { get; set; }

    public float Y { get; set; }

    /// <summary>世界坐标（调试用）。</summary>
    public float Wx { get; set; }

    public float Wz { get; set; }

    /// <summary>角色朝向（弧度，仅自己有意义；游戏 Rotation）。</summary>
    public float Rot { get; set; }
}

/// <summary>校准参数 DTO（JSON camelCase）。</summary>
internal sealed class MapCalibrationDto
{
    public uint TerritoryId { get; set; }
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float ScaleX { get; set; } = 1f;
    public float ScaleY { get; set; } = 1f;
    public bool FlipY { get; set; }

    public static MapCalibrationDto From(uint territoryId, MapCalibration cal) =>
        new()
        {
            TerritoryId = territoryId == 0 ? FrontlineMapAssets.GloryTerritoryId : territoryId,
            OffsetX = cal.OffsetX,
            OffsetY = cal.OffsetY,
            ScaleX = cal.ScaleX,
            ScaleY = cal.ScaleY,
            FlipY = cal.FlipY,
        };

    public MapCalibration ToModel() =>
        new()
        {
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            ScaleX = ScaleX == 0 ? 1f : ScaleX,
            ScaleY = ScaleY == 0 ? 1f : ScaleY,
            FlipY = FlipY,
        };
}
