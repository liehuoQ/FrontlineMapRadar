using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FrontlineMapRadar;

internal sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("Frontline Map Radar 设置###FrontlineMapRadarConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 420),
            MaximumSize = new Vector2(900, 1000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Config;
        var changed = false;

        var enabled = cfg.Enabled;
        if (ImGui.Checkbox("启用", ref enabled))
        {
            cfg.Enabled = enabled;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("仅在纷争前线 + 打开大地图(M) 时绘制周围玩家点。");
        ImGui.TextDisabled("红 = 黑涡 / 蓝 = 恒辉 / 黄 = 双蛇");
        ImGui.Spacing();

        if (ImGui.Button("打开本场玩家名册"))
            plugin.ToggleRoster();
        ImGui.SameLine();
        ImGui.TextDisabled("/flroster 或 /flradar roster");
        ImGui.TextUnformatted($"已记录玩家: {plugin.Roster.Count}");
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.TextUnformatted("远程雷达（平板浏览器）");
        var remote = cfg.RemoteRadarEnabled;
        if (ImGui.Checkbox("启用局域网网页同步", ref remote))
        {
            cfg.RemoteRadarEnabled = remote;
            changed = true;
            plugin.RadarHttp.ApplyConfig();
        }

        var port = cfg.RemoteRadarPort;
        if (ImGui.InputInt("端口", ref port))
        {
            cfg.RemoteRadarPort = Math.Clamp(port, 1024, 65535);
            changed = true;
            if (cfg.RemoteRadarEnabled)
                plugin.RadarHttp.ApplyConfig();
        }

        if (plugin.RadarHttp.IsRunning)
        {
            ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.45f, 1f), $"服务运行中 · 端口 {plugin.RadarHttp.BoundPort}");
            ImGui.TextWrapped("同一 WiFi 下，用平板浏览器打开下面任一地址：");
            foreach (var url in plugin.RadarHttp.GetLanUrls())
            {
                ImGui.BulletText(url);
                ImGui.SameLine();
                if (ImGui.SmallButton($"复制##{url}"))
                    ImGui.SetClipboardText(url);
            }

            ImGui.TextDisabled("若平板打不开：检查 Windows 防火墙是否放行该端口。");
        }
        else if (cfg.RemoteRadarEnabled)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.35f, 1f), "服务未启动");
            if (!string.IsNullOrEmpty(plugin.RadarHttp.LastError))
                ImGui.TextWrapped(plugin.RadarHttp.LastError);
        }
        else
        {
            ImGui.TextDisabled("开启后，平板无需装 App，用浏览器访问即可。");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("网页坐标校准（按领地）");
        ImGui.TextDisabled("也可在平板网页勾选「校准」实时微调后点「保存到插件」。");
        {
            var tid = plugin.ClientState.TerritoryType;
            if (tid == 0)
                tid = FrontlineMapAssets.GloryTerritoryId;
            var cal = plugin.Config.GetCalibration(tid);
            ImGui.TextUnformatted($"当前领地: {tid} · {FrontlineMapAssets.GetDisplayName(tid, null)}");

            var ox = cal.OffsetX;
            var oy = cal.OffsetY;
            var sx = cal.ScaleX;
            var sy = cal.ScaleY;
            var flip = cal.FlipY;
            var calChanged = false;

            if (ImGui.SliderFloat("偏移 X##cal", ref ox, -0.5f, 0.5f, "%.3f"))
            {
                cal.OffsetX = ox;
                calChanged = true;
            }

            if (ImGui.SliderFloat("偏移 Y##cal", ref oy, -0.5f, 0.5f, "%.3f"))
            {
                cal.OffsetY = oy;
                calChanged = true;
            }

            if (ImGui.SliderFloat("缩放 X##cal", ref sx, 0.5f, 1.5f, "%.3f"))
            {
                cal.ScaleX = sx;
                calChanged = true;
            }

            if (ImGui.SliderFloat("缩放 Y##cal", ref sy, 0.5f, 1.5f, "%.3f"))
            {
                cal.ScaleY = sy;
                calChanged = true;
            }

            if (ImGui.Checkbox("翻转 Y##cal", ref flip))
            {
                cal.FlipY = flip;
                calChanged = true;
            }

            if (ImGui.Button("恢复推荐校准"))
            {
                var recommended = FrontlineMapAssets.CreateDefaultCalibration(tid);
                cal.OffsetX = recommended.OffsetX;
                cal.OffsetY = recommended.OffsetY;
                cal.ScaleX = recommended.ScaleX;
                cal.ScaleY = recommended.ScaleY;
                cal.FlipY = recommended.FlipY;
                calChanged = true;
            }

            ImGui.SameLine();
            if (ImGui.Button("清零校准"))
            {
                cal.Reset();
                calChanged = true;
            }

            if (calChanged)
            {
                plugin.Config.SetCalibration(tid, cal);
                changed = true;
            }
        }

        ImGui.Spacing();
        var hideOwn = cfg.HideOwnTeam;
        if (ImGui.Checkbox("隐藏本阵营", ref hideOwn))
        {
            cfg.HideOwnTeam = hideOwn;
            changed = true;
        }

        var radius = cfg.DotRadius;
        if (ImGui.SliderFloat("点半径", ref radius, 2f, 12f, "%.1f"))
        {
            cfg.DotRadius = radius;
            changed = true;
        }

        var outline = cfg.OutlineThickness;
        if (ImGui.SliderFloat("描边厚度", ref outline, 0f, 4f, "%.1f"))
        {
            cfg.OutlineThickness = outline;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("阵营颜色");

        var maelstrom = cfg.MaelstromColor;
        if (ImGui.ColorEdit4("黑涡团", ref maelstrom, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            cfg.MaelstromColor = maelstrom;
            changed = true;
        }

        var flames = cfg.FlamesColor;
        if (ImGui.ColorEdit4("恒辉队", ref flames, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            cfg.FlamesColor = flames;
            changed = true;
        }

        var twinAdder = cfg.TwinAdderColor;
        if (ImGui.ColorEdit4("双蛇党", ref twinAdder, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            cfg.TwinAdderColor = twinAdder;
            changed = true;
        }

        var unknown = cfg.UnknownColor;
        if (ImGui.ColorEdit4("未知", ref unknown, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            cfg.UnknownColor = unknown;
            changed = true;
        }

        var outlineColor = cfg.OutlineColor;
        if (ImGui.ColorEdit4("描边", ref outlineColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            cfg.OutlineColor = outlineColor;
            changed = true;
        }

        if (changed)
            cfg.Save();

        ImGui.Separator();
        var inFrontline = FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager);
        var ownGc = FrontlineGcHelper.InferOwnGc(plugin.ObjectTable);
        ImGui.TextUnformatted($"当前区域: Territory={plugin.ClientState.TerritoryType}");
        ImGui.TextUnformatted($"纷争前线判定: {(inFrontline ? "是" : "否")}");
        ImGui.TextUnformatted($"推断本阵营: {FrontlineGcHelper.ToDisplayName(ownGc)}");
    }
}
