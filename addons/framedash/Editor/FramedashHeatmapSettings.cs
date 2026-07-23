#if TOOLS
#nullable enable

using Godot;

namespace Framedash.Editor
{
    internal sealed class FramedashHeatmapSettings
    {
        private const string Section = "FramedashHeatmap";

        public string ReadApiKey { get; set; } = "";
        public string ApiBaseUrl { get; set; } = "https://app.framedash.dev";
        public string ProjectId { get; set; } = "";
        public int Days { get; set; } = 7;
        public int CellSize { get; set; } = 25;
        public string EventNameFilter { get; set; } = "";
        public float OverlayOpacity { get; set; } = 0.6f;
        public float ZOffset { get; set; }
        public string SelectedMapId { get; set; } = "";
        public bool OverlayEnabled { get; set; }

        public void Save(ConfigFile configuration)
        {
            configuration.SetValue(Section, "read_api_key", ReadApiKey);
            configuration.SetValue(Section, "api_base_url", ApiBaseUrl);
            configuration.SetValue(Section, "project_id", ProjectId);
            configuration.SetValue(Section, "days", Days);
            configuration.SetValue(Section, "cell_size", CellSize);
            configuration.SetValue(Section, "event_name_filter", EventNameFilter);
            configuration.SetValue(Section, "overlay_opacity", OverlayOpacity);
            configuration.SetValue(Section, "z_offset", ZOffset);
            configuration.SetValue(Section, "selected_map_id", SelectedMapId);
            configuration.SetValue(Section, "overlay_enabled", OverlayEnabled);
        }

        public void Load(ConfigFile configuration)
        {
            ReadApiKey = ReadString(configuration, "read_api_key", "");
            ApiBaseUrl = ReadString(
                configuration,
                "api_base_url",
                "https://app.framedash.dev");
            ProjectId = ReadString(configuration, "project_id", "");
            Days = ReadInt(configuration, "days", 7);
            CellSize = ReadInt(configuration, "cell_size", 25);
            EventNameFilter = ReadString(configuration, "event_name_filter", "");
            OverlayOpacity = Mathf.Clamp(
                (float)ReadDouble(configuration, "overlay_opacity", 0.6),
                0,
                1);
            ZOffset = (float)ReadDouble(configuration, "z_offset", 0);
            SelectedMapId = ReadString(configuration, "selected_map_id", "");
            OverlayEnabled = ReadBool(configuration, "overlay_enabled", false);

            if (!Logic.FramedashEditorLogic.IsAllowedDays(Days))
            {
                Days = 7;
            }
            if (!Logic.FramedashEditorLogic.IsAllowedCellSize(CellSize))
            {
                CellSize = 25;
            }
        }

        private static string ReadString(
            ConfigFile configuration,
            string key,
            string fallback)
        {
            return configuration.GetValue(Section, key, fallback).AsString();
        }

        private static int ReadInt(
            ConfigFile configuration,
            string key,
            int fallback)
        {
            return configuration.GetValue(Section, key, fallback).AsInt32();
        }

        private static double ReadDouble(
            ConfigFile configuration,
            string key,
            double fallback)
        {
            return configuration.GetValue(Section, key, fallback).AsDouble();
        }

        private static bool ReadBool(
            ConfigFile configuration,
            string key,
            bool fallback)
        {
            return configuration.GetValue(Section, key, fallback).AsBool();
        }
    }
}
#endif
