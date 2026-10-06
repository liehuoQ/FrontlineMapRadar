using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace FrontlineMapRadar;

[Serializable]
public sealed class MapCalibration
{
    /// <summary>归一化坐标 X 偏移（加在缩放之后）。</summary>
    public float OffsetX { get; set; }

    /// <summary>归一化坐标 Y 偏移。</summary>
    public float OffsetY { get; set; }

    /// <summary>X 缩放，默认 1。</summary>
    public float ScaleX { get; set; } = 1f;

    /// <summary>Y 缩放，默认 1。</summary>
    public float ScaleY { get; set; } = 1f;

    /// <summary>是否上下翻转（1 - y）。</summary>
    public bool FlipY { get; set; }

    public MapCalibration Clone() =>
        new()
        {
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            FlipY = FlipY,
        };

    public void Reset()
    {
        OffsetX = 0;
        OffsetY = 0;
        ScaleX = 1;
        ScaleY = 1;
        FlipY = false;
    }

    public (float X, float Y) Apply(float x, float y)
    {
        var tx = x;
        var ty = FlipY ? 1f - y : y;
        var sx = ScaleX == 0 ? 1f : ScaleX;
        var sy = ScaleY == 0 ? 1f : ScaleY;
        return (tx * sx + OffsetX, ty * sy + OffsetY);
    }
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>游戏内焦点目标极限技积蓄预测 HUD</summary>
    public bool FocusLbPredictEnabled { get; set; } = true;

    /// <summary>向局域网推送雷达网页（平板浏览器打开）</summary>
    public bool RemoteRadarEnabled { get; set; }

    /// <summary>远程雷达 HTTP 端口</summary>
    public int RemoteRadarPort { get; set; } = 17890;

    /// <summary>点半径（屏幕像素）</summary>
    public float DotRadius { get; set; } = 5f;

    /// <summary>隐藏本阵营玩家</summary>
    public bool HideOwnTeam { get; set; }

    /// <summary>黑涡团（利姆萨·罗敏萨）</summary>
    public Vector4 MaelstromColor { get; set; } = new(0.95f, 0.22f, 0.22f, 0.95f);

    /// <summary>恒辉队（乌尔达哈）</summary>
    public Vector4 FlamesColor { get; set; } = new(0.25f, 0.55f, 1f, 0.95f);

    /// <summary>双蛇党（格里达尼亚）</summary>
    public Vector4 TwinAdderColor { get; set; } = new(0.95f, 0.85f, 0.2f, 0.95f);

    /// <summary>未能识别阵营时的颜色</summary>
    public Vector4 UnknownColor { get; set; } = new(0.85f, 0.85f, 0.85f, 0.85f);

    /// <summary>点描边颜色</summary>
    public Vector4 OutlineColor { get; set; } = new(0f, 0f, 0f, 0.9f);

    /// <summary>描边厚度</summary>
    public float OutlineThickness { get; set; } = 1.5f;

    /// <summary>按领地 ID 保存的网页雷达坐标校准。</summary>
    public Dictionary<uint, MapCalibration> MapCalibrations { get; set; } = new();

    [NonSerialized]
    private Plugin? plugin;

    public void Initialize(Plugin plugin) => this.plugin = plugin;

    public void Save() => plugin?.PluginInterface.SavePluginConfig(this);

    public MapCalibration GetCalibration(uint territoryId)
    {
        if (territoryId == 0)
            territoryId = FrontlineMapAssets.GloryTerritoryId;

        if (!MapCalibrations.TryGetValue(territoryId, out var cal) || cal == null)
        {
            cal = FrontlineMapAssets.CreateDefaultCalibration(territoryId);
            MapCalibrations[territoryId] = cal;
        }

        return cal;
    }

    public void SetCalibration(uint territoryId, MapCalibration cal)
    {
        if (territoryId == 0)
            territoryId = FrontlineMapAssets.GloryTerritoryId;

        MapCalibrations[territoryId] = cal.Clone();
        Save();
    }
}
