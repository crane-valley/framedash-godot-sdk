#if TOOLS
#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Framedash.Editor.Logic
{
    public static class FramedashEditorLogic
    {
        public static string ResolveReadApiKey(string configuredValue, string environmentValue)
        {
            string configured = (configuredValue ?? "").Trim();
            return configured.Length > 0 ? configured : (environmentValue ?? "").Trim();
        }

        public static string[] BuildMapNames(IReadOnlyList<MapInfo> maps)
        {
            int mapCount = maps?.Count ?? 0;
            var names = new string[mapCount + 1];
            names[0] = "Select a map";
            for (int i = 0; i < mapCount; i++)
            {
                names[i + 1] = maps[i]?.Name ?? "";
            }
            return names;
        }

        public static int FindMapIndexById(IReadOnlyList<MapInfo> maps, string mapId)
        {
            if (maps == null || string.IsNullOrEmpty(mapId))
            {
                return -1;
            }
            for (int i = 0; i < maps.Count; i++)
            {
                if (string.Equals(maps[i]?.MapId, mapId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        public static int ResolveMapSelectionIndex(
            IReadOnlyList<MapInfo> maps,
            string preferredMapId,
            bool allowFirstMapFallback)
        {
            int selectedIndex = FindMapIndexById(maps, preferredMapId);
            if (selectedIndex >= 0 || !allowFirstMapFallback || maps == null || maps.Count == 0)
            {
                return selectedIndex;
            }
            return 0;
        }

        public static bool ShouldRestoreOverlayData(
            bool overlayEnabled,
            string projectId,
            string selectedMapId,
            string resolvedReadApiKey)
        {
            return overlayEnabled
                && !string.IsNullOrWhiteSpace(projectId)
                && !string.IsNullOrWhiteSpace(selectedMapId)
                && !string.IsNullOrWhiteSpace(resolvedReadApiKey);
        }

        public sealed class MapInfo
        {
            public string Id;
            public string Name;
            public string MapId;
            public string ImageUrl;
            public double WorldMinX;
            public double WorldMinY;
            public double WorldMaxX;
            public double WorldMaxY;
            public double? WorldMinZ;
            public double? WorldMaxZ;
            public string UpAxis = "z";
            public double ImageWidth;
            public double ImageHeight;
            public string CreatedAt;
            public string UpdatedAt;

            public bool IsYUp => UpAxis == "y";
        }

        public sealed class HeatmapCell
        {
            public double X;
            public double Y;
            public double? Z;
            public double Weight;
            public double EventCount;
            public double AverageFps;
            public double AverageFrameTime;
            public double AverageMemory;
            public double? AverageGpuTime;
            public double? AverageVramMemory;
        }

        public readonly struct CellRect
        {
            public CellRect(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
        }

        public readonly struct HeatmapRgba
        {
            public HeatmapRgba(float r, float g, float b, float a)
            {
                R = r;
                G = g;
                B = b;
                A = a;
            }

            public float R { get; }
            public float G { get; }
            public float B { get; }
            public float A { get; }
        }

        public readonly struct HeatmapBoundsData
        {
            public HeatmapBoundsData(
                double minX,
                double minY,
                double minZ,
                double maxX,
                double maxY,
                double maxZ)
            {
                MinX = minX;
                MinY = minY;
                MinZ = minZ;
                MaxX = maxX;
                MaxY = maxY;
                MaxZ = maxZ;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MinZ { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public double MaxZ { get; }
        }

        public readonly struct HeatmapRenderCell
        {
            public HeatmapRenderCell(
                CellRect rect,
                double centerZ,
                double voxelHeight,
                double normalizedWeight)
            {
                Rect = rect;
                CenterZ = centerZ;
                VoxelHeight = voxelHeight;
                NormalizedWeight = normalizedWeight;
            }

            public CellRect Rect { get; }
            public double CenterZ { get; }
            public double VoxelHeight { get; }
            public double NormalizedWeight { get; }
            public bool IsVolumetric => IsFinite(VoxelHeight) && VoxelHeight > 0;
        }

        public sealed class HeatmapGeometryData
        {
            public HeatmapGeometryData(
                double[] x,
                double[] y,
                double[] z,
                int[] triangleIndices,
                HeatmapRgba[] colors)
            {
                X = x;
                Y = y;
                Z = z;
                TriangleIndices = triangleIndices;
                Colors = colors;
            }

            public double[] X { get; }
            public double[] Y { get; }
            public double[] Z { get; }
            public int[] TriangleIndices { get; }
            public HeatmapRgba[] Colors { get; }
        }

        public static bool ParseMapsResponse(string json, out List<MapInfo> maps, out string error)
        {
            maps = new List<MapInfo>();
            error = string.Empty;
            if (!TryReadSuccessfulDataArray(json, out List<object> data))
            {
                error = "Malformed maps response.";
                return false;
            }

            foreach (object value in data)
            {
                if (!(value is Dictionary<string, object> entry))
                {
                    maps.Clear();
                    error = "Malformed map entry in maps response.";
                    return false;
                }

                var map = new MapInfo();
                if (!TryReadString(entry, "id", out map.Id)
                    || !TryReadString(entry, "name", out map.Name)
                    || !TryReadString(entry, "mapId", out map.MapId)
                    || !TryReadString(entry, "imageUrl", out map.ImageUrl)
                    || !TryReadNumber(entry, "worldMinX", out map.WorldMinX)
                    || !TryReadNumber(entry, "worldMinY", out map.WorldMinY)
                    || !TryReadNumber(entry, "worldMaxX", out map.WorldMaxX)
                    || !TryReadNumber(entry, "worldMaxY", out map.WorldMaxY)
                    || !TryReadOptionalNumber(entry, "worldMinZ", out map.WorldMinZ)
                    || !TryReadOptionalNumber(entry, "worldMaxZ", out map.WorldMaxZ)
                    || !TryReadNumber(entry, "imageWidth", out map.ImageWidth)
                    || !TryReadNumber(entry, "imageHeight", out map.ImageHeight)
                    || !TryReadString(entry, "createdAt", out map.CreatedAt)
                    || !TryReadString(entry, "updatedAt", out map.UpdatedAt)
                    || !TryReadOptionalString(entry, "upAxis", "z", out map.UpAxis))
                {
                    maps.Clear();
                    error = "Malformed map entry in maps response.";
                    return false;
                }
                maps.Add(map);
            }
            return true;
        }

        public static bool ParseHeatmapResponse(
            string json,
            out List<HeatmapCell> cells,
            out string error)
        {
            cells = new List<HeatmapCell>();
            error = string.Empty;
            if (!TryReadSuccessfulDataArray(json, out List<object> data))
            {
                error = "Malformed heatmap response.";
                return false;
            }

            foreach (object value in data)
            {
                if (!(value is Dictionary<string, object> entry))
                {
                    cells.Clear();
                    error = "Malformed cell entry in heatmap response.";
                    return false;
                }

                var cell = new HeatmapCell();
                if (!TryReadNumber(entry, "x", out cell.X)
                    || !TryReadNumber(entry, "y", out cell.Y)
                    || !TryReadOptionalNumber(entry, "z", out cell.Z)
                    || !TryReadNumberOrNumericString(entry, "weight", out cell.Weight)
                    || !TryReadNumberOrNumericString(entry, "event_count", out cell.EventCount)
                    || !TryReadNumber(entry, "avg_fps", out cell.AverageFps)
                    || !TryReadNumber(entry, "avg_frame_time", out cell.AverageFrameTime)
                    || !TryReadNumber(entry, "avg_memory", out cell.AverageMemory)
                    || !TryReadOptionalNumber(entry, "avg_gpu_time", out cell.AverageGpuTime)
                    || !TryReadOptionalNumber(entry, "avg_mem_vram", out cell.AverageVramMemory))
                {
                    cells.Clear();
                    error = "Malformed cell entry in heatmap response.";
                    return false;
                }
                cells.Add(cell);
            }
            return true;
        }

        public static string ParseProblemMessage(string json, string fallback)
        {
            if (!MiniJsonReader.TryParse(json, out var parsed)
                || !(parsed is Dictionary<string, object> root))
            {
                return fallback;
            }
            if (TryReadString(root, "detail", out string detail) && !string.IsNullOrEmpty(detail))
            {
                return detail;
            }
            if (TryReadString(root, "title", out string title) && !string.IsNullOrEmpty(title))
            {
                return title;
            }
            return fallback;
        }

        internal static bool ValidateHttpResponse(
            bool connected,
            long statusCode,
            string body,
            string requestError,
            out string error)
        {
            if (!connected)
            {
                error = string.IsNullOrEmpty(requestError)
                    ? "Unable to reach the Framedash API."
                    : requestError;
                return false;
            }
            if (statusCode < 200 || statusCode > 299)
            {
                string fallback = statusCode > 0
                    ? "Framedash request failed (HTTP " + statusCode + ")."
                    : "Framedash request failed.";
                error = ParseProblemMessage(body, fallback);
                return false;
            }

            error = string.Empty;
            return true;
        }

        public static CellRect BuildCellRect(HeatmapCell cell, MapInfo map, double cellSize)
        {
            return FramedashHeatmapGeometry.BuildCellRect(cell, map, cellSize);
        }

        public static double FindMaxWeight(IReadOnlyList<HeatmapCell> cells)
        {
            return FramedashHeatmapGeometry.FindMaxWeight(cells);
        }

        public static double NormalizeWeight(double weight, double maxWeight)
        {
            return FramedashHeatmapGeometry.NormalizeWeight(weight, maxWeight);
        }

        public static HeatmapRenderCell BuildHeatmapRenderCell(
            HeatmapCell cell,
            MapInfo map,
            double cellSize,
            double normalizedWeight)
        {
            return FramedashHeatmapGeometry.BuildHeatmapRenderCell(
                cell,
                map,
                cellSize,
                normalizedWeight);
        }

        public static HeatmapRgba HeatmapColor(double normalizedWeight, float opacity)
        {
            return FramedashHeatmapGeometry.HeatmapColor(normalizedWeight, opacity);
        }

        public static HeatmapGeometryData BuildHeatmapGeometry(
            IReadOnlyList<HeatmapRenderCell> cells,
            float opacity)
        {
            return FramedashHeatmapGeometry.BuildHeatmapGeometry(cells, opacity);
        }

        public static bool TryBuildHeatmapBounds(
            IReadOnlyList<HeatmapRenderCell> cells,
            double zOffset,
            out HeatmapBoundsData bounds)
        {
            return FramedashHeatmapGeometry.TryBuildHeatmapBounds(
                cells,
                zOffset,
                out bounds);
        }

        public static HeatmapRgba[] BuildHeatmapColors(
            IReadOnlyList<HeatmapRenderCell> cells,
            float opacity)
        {
            return FramedashHeatmapGeometry.BuildHeatmapColors(cells, opacity);
        }

        public static void MapPlaneToEngine(
            MapInfo map,
            double groundX,
            double groundY,
            double height,
            out double x,
            out double y,
            out double z)
        {
            x = groundX;
            if (map != null && map.IsYUp)
            {
                y = height;
                z = groundY;
            }
            else
            {
                y = groundY;
                z = height;
            }
        }

        public static string BuildHeatmapQueryPath(
            string projectId,
            string mapSlug,
            int cellSize,
            int days,
            string eventNameFilter)
        {
            string path = "/api/v1/projects/" + Uri.EscapeDataString(projectId ?? "")
                + "/heatmap?mapId=" + Uri.EscapeDataString(mapSlug ?? "")
                + "&cellSize=" + cellSize.ToString(CultureInfo.InvariantCulture)
                + "&days=" + days.ToString(CultureInfo.InvariantCulture)
                + "&includeZ=true&coordinates=world";
            if (!string.IsNullOrEmpty(eventNameFilter))
            {
                path += "&eventName=" + Uri.EscapeDataString(eventNameFilter);
            }
            return path;
        }

        public static bool IsAllowedDays(int days)
        {
            return days == 1 || days == 7 || days == 14 || days == 30;
        }

        public static bool IsAllowedCellSize(int cellSize)
        {
            return cellSize == 5 || cellSize == 10 || cellSize == 25 || cellSize == 50;
        }

        private static bool TryReadSuccessfulDataArray(string json, out List<object> data)
        {
            data = null;
            if (!MiniJsonReader.TryParse(json, out var parsed)
                || !(parsed is Dictionary<string, object> root)
                || !root.TryGetValue("success", out object success)
                || !(success is bool successValue)
                || !successValue
                || !root.TryGetValue("data", out object rawData)
                || !(rawData is List<object> array))
            {
                return false;
            }
            data = array;
            return true;
        }

        private static bool TryReadString(
            Dictionary<string, object> values,
            string name,
            out string result)
        {
            result = null;
            if (!values.TryGetValue(name, out object value) || !(value is string text))
            {
                return false;
            }
            result = text;
            return true;
        }

        private static bool TryReadOptionalString(
            Dictionary<string, object> values,
            string name,
            string fallback,
            out string result)
        {
            result = fallback;
            if (!values.TryGetValue(name, out object value) || value == null)
            {
                return true;
            }
            if (!(value is string text))
            {
                return false;
            }
            result = text;
            return true;
        }

        private static bool TryReadNumber(
            Dictionary<string, object> values,
            string name,
            out double result)
        {
            result = 0;
            if (!values.TryGetValue(name, out object value) || !(value is double number))
            {
                return false;
            }
            result = number;
            return IsFinite(result);
        }

        private static bool TryReadNumberOrNumericString(
            Dictionary<string, object> values,
            string name,
            out double result)
        {
            if (TryReadNumber(values, name, out result))
            {
                return true;
            }
            result = 0;
            if (!values.TryGetValue(name, out object value) || !(value is string text))
            {
                return false;
            }
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result) && IsFinite(result);
        }

        private static bool TryReadOptionalNumber(
            Dictionary<string, object> values,
            string name,
            out double? result)
        {
            result = null;
            if (!values.TryGetValue(name, out object value) || value == null)
            {
                return true;
            }
            if (!(value is double number) || !IsFinite(number))
            {
                return false;
            }
            result = number;
            return true;
        }

        private static bool IsFinite(double value)
        {
            return FramedashHeatmapGeometry.IsFinite(value);
        }

    }
}
#endif
