#if TOOLS
#nullable disable

using System;
using System.Collections.Generic;
using CellRect = Framedash.Editor.Logic.FramedashEditorLogic.CellRect;
using HeatmapBoundsData = Framedash.Editor.Logic.FramedashEditorLogic.HeatmapBoundsData;
using HeatmapCell = Framedash.Editor.Logic.FramedashEditorLogic.HeatmapCell;
using HeatmapGeometryData = Framedash.Editor.Logic.FramedashEditorLogic.HeatmapGeometryData;
using HeatmapRenderCell = Framedash.Editor.Logic.FramedashEditorLogic.HeatmapRenderCell;
using HeatmapRgba = Framedash.Editor.Logic.FramedashEditorLogic.HeatmapRgba;
using MapInfo = Framedash.Editor.Logic.FramedashEditorLogic.MapInfo;

namespace Framedash.Editor.Logic
{
    internal static class FramedashHeatmapGeometry
    {
        private static readonly int[] VoxelTriangleIndices =
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 6, 7, 4,
            0, 1, 5, 5, 4, 0,
            1, 2, 6, 6, 5, 1,
            2, 3, 7, 7, 6, 2,
            3, 0, 4, 4, 7, 3
        };

        private static readonly int[] FlatTriangleIndices = { 0, 1, 2, 2, 3, 0 };

        private static readonly HeatmapRgba[] HeatmapPalette =
        {
            new HeatmapRgba(0, 0.1f, 1, 1),
            new HeatmapRgba(0, 1, 1, 1),
            new HeatmapRgba(0, 1, 0.2f, 1),
            new HeatmapRgba(1, 1, 0, 1),
            new HeatmapRgba(1, 0.05f, 0, 1)
        };

        internal static CellRect BuildCellRect(HeatmapCell cell, MapInfo map, double cellSize)
        {
            return cell == null ? new CellRect() : BuildCellRect(cell.X, GroundY(cell, map), map, cellSize);
        }

        // The API returns engine world axes; a Y-up map's ground plane is X/Z and its
        // height is Y, so geometry runs on (X, Z, Y) and overlays map it back.
        private static double GroundY(HeatmapCell cell, MapInfo map)
        {
            return map != null && map.IsYUp && cell.Z.HasValue ? cell.Z.Value : cell.Y;
        }

        private static double? Height(HeatmapCell cell, MapInfo map)
        {
            return map != null && map.IsYUp ? (cell.Z.HasValue ? cell.Y : (double?)null) : cell.Z;
        }

        private static CellRect BuildCellRect(double x, double y, MapInfo map, double cellSize)
        {
            if (map == null || cellSize <= 0 || !IsFinite(cellSize))
            {
                return new CellRect();
            }

            double binX = Math.Floor((x - map.WorldMinX) / cellSize);
            double binY = Math.Floor((y - map.WorldMinY) / cellSize);
            double minX = map.WorldMinX + binX * cellSize;
            double minY = map.WorldMinY + binY * cellSize;
            return new CellRect(
                minX,
                minY,
                Math.Min(minX + cellSize, map.WorldMaxX),
                Math.Min(minY + cellSize, map.WorldMaxY));
        }

        internal static double FindMaxWeight(IReadOnlyList<HeatmapCell> cells)
        {
            if (cells == null)
            {
                return 0;
            }
            double maxWeight = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                maxWeight = Math.Max(maxWeight, cells[i].Weight);
            }
            return maxWeight;
        }

        internal static double NormalizeWeight(double weight, double maxWeight)
        {
            if (!IsFinite(weight) || !IsFinite(maxWeight) || maxWeight <= 0)
            {
                return 0;
            }
            return Clamp(weight / maxWeight, 0, 1);
        }

        internal static HeatmapRenderCell BuildHeatmapRenderCell(
            HeatmapCell cell,
            MapInfo map,
            double cellSize,
            double normalizedWeight)
        {
            if (cell == null || map == null)
            {
                return new HeatmapRenderCell(new CellRect(), 0, 0, normalizedWeight);
            }

            double? height = Height(cell, map);
            double measuredZ = height.GetValueOrDefault();
            bool hasMeasuredZ = height.HasValue
                && IsFinite(measuredZ)
                && IsFinite(cellSize)
                && cellSize > 0;
            double mapFloor = map.WorldMinZ.HasValue && IsFinite(map.WorldMinZ.Value)
                ? map.WorldMinZ.Value
                : 0;
            return new HeatmapRenderCell(
                BuildCellRect(cell, map, cellSize),
                hasMeasuredZ ? measuredZ : mapFloor,
                hasMeasuredZ ? cellSize : 0,
                normalizedWeight);
        }

        internal static HeatmapRgba HeatmapColor(double normalizedWeight, float opacity)
        {
            float alpha = Clamp(opacity, 0, 1);
            float weight = (float)Clamp(normalizedWeight, 0, 1);
            float scaledWeight = weight * 4;
            int stopIndex = Math.Min((int)Math.Floor(scaledWeight), 3);
            float amount = scaledWeight - stopIndex;
            HeatmapRgba color = Lerp(
                HeatmapPalette[stopIndex],
                HeatmapPalette[stopIndex + 1],
                amount);
            return new HeatmapRgba(color.R, color.G, color.B, alpha);
        }

        internal static HeatmapGeometryData BuildHeatmapGeometry(
            IReadOnlyList<HeatmapRenderCell> cells,
            float opacity)
        {
            if (cells == null || cells.Count == 0)
            {
                return new HeatmapGeometryData(
                    Array.Empty<double>(),
                    Array.Empty<double>(),
                    Array.Empty<double>(),
                    Array.Empty<int>(),
                    Array.Empty<HeatmapRgba>());
            }

            int vertexCount = 0;
            int triangleIndexCount = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                vertexCount += cells[i].IsVolumetric ? 8 : 4;
                triangleIndexCount += cells[i].IsVolumetric ? 36 : 6;
            }

            var x = new double[vertexCount];
            var y = new double[vertexCount];
            var z = new double[vertexCount];
            var triangleIndices = new int[triangleIndexCount];
            int vertexOffset = 0;
            int triangleOffset = 0;
            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                HeatmapRenderCell cell = cells[cellIndex];
                double centerX = (cell.Rect.MinX + cell.Rect.MaxX) * 0.5;
                double centerY = (cell.Rect.MinY + cell.Rect.MaxY) * 0.5;
                double halfWidth = (cell.Rect.MaxX - cell.Rect.MinX) * 0.45;
                double halfDepth = (cell.Rect.MaxY - cell.Rect.MinY) * 0.45;
                double halfHeight = cell.IsVolumetric ? cell.VoxelHeight * 0.45 : 0;
                double minZ = cell.CenterZ - halfHeight;
                double maxZ = cell.CenterZ + halfHeight;

                WriteCorner(x, y, z, vertexOffset, centerX - halfWidth, centerY - halfDepth, minZ);
                WriteCorner(x, y, z, vertexOffset + 1, centerX + halfWidth, centerY - halfDepth, minZ);
                WriteCorner(x, y, z, vertexOffset + 2, centerX + halfWidth, centerY + halfDepth, minZ);
                WriteCorner(x, y, z, vertexOffset + 3, centerX - halfWidth, centerY + halfDepth, minZ);

                if (cell.IsVolumetric)
                {
                    WriteCorner(x, y, z, vertexOffset + 4, centerX - halfWidth, centerY - halfDepth, maxZ);
                    WriteCorner(x, y, z, vertexOffset + 5, centerX + halfWidth, centerY - halfDepth, maxZ);
                    WriteCorner(x, y, z, vertexOffset + 6, centerX + halfWidth, centerY + halfDepth, maxZ);
                    WriteCorner(x, y, z, vertexOffset + 7, centerX - halfWidth, centerY + halfDepth, maxZ);
                    CopyTriangleIndices(
                        VoxelTriangleIndices,
                        triangleIndices,
                        triangleOffset,
                        vertexOffset);
                    vertexOffset += 8;
                    triangleOffset += VoxelTriangleIndices.Length;
                }
                else
                {
                    CopyTriangleIndices(
                        FlatTriangleIndices,
                        triangleIndices,
                        triangleOffset,
                        vertexOffset);
                    vertexOffset += 4;
                    triangleOffset += FlatTriangleIndices.Length;
                }
            }
            return new HeatmapGeometryData(
                x,
                y,
                z,
                triangleIndices,
                BuildHeatmapColors(cells, opacity));
        }

        internal static bool TryBuildHeatmapBounds(
            IReadOnlyList<HeatmapRenderCell> cells,
            double zOffset,
            out HeatmapBoundsData bounds)
        {
            bounds = default;
            if (cells == null || cells.Count == 0 || !IsFinite(zOffset))
            {
                return false;
            }

            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            double maxZ = double.NegativeInfinity;
            bool hasValidCell = false;
            for (int i = 0; i < cells.Count; i++)
            {
                HeatmapRenderCell cell = cells[i];
                if (!IsFinite(cell.Rect.MinX)
                    || !IsFinite(cell.Rect.MinY)
                    || !IsFinite(cell.Rect.MaxX)
                    || !IsFinite(cell.Rect.MaxY)
                    || !IsFinite(cell.CenterZ))
                {
                    continue;
                }

                double halfHeight = cell.IsVolumetric ? cell.VoxelHeight * 0.5 : 0;
                minX = Math.Min(minX, Math.Min(cell.Rect.MinX, cell.Rect.MaxX));
                minY = Math.Min(minY, Math.Min(cell.Rect.MinY, cell.Rect.MaxY));
                minZ = Math.Min(minZ, cell.CenterZ - halfHeight + zOffset);
                maxX = Math.Max(maxX, Math.Max(cell.Rect.MinX, cell.Rect.MaxX));
                maxY = Math.Max(maxY, Math.Max(cell.Rect.MinY, cell.Rect.MaxY));
                maxZ = Math.Max(maxZ, cell.CenterZ + halfHeight + zOffset);
                hasValidCell = true;
            }

            if (!hasValidCell)
            {
                return false;
            }
            bounds = new HeatmapBoundsData(minX, minY, minZ, maxX, maxY, maxZ);
            return true;
        }

        internal static HeatmapRgba[] BuildHeatmapColors(
            IReadOnlyList<HeatmapRenderCell> cells,
            float opacity)
        {
            if (cells == null)
            {
                return Array.Empty<HeatmapRgba>();
            }
            int vertexCount = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                vertexCount += cells[i].IsVolumetric ? 8 : 4;
            }
            var colors = new HeatmapRgba[vertexCount];
            int vertexOffset = 0;
            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                HeatmapRgba color = HeatmapColor(cells[cellIndex].NormalizedWeight, opacity);
                int cellVertexCount = cells[cellIndex].IsVolumetric ? 8 : 4;
                for (int vertex = 0; vertex < cellVertexCount; vertex++)
                {
                    colors[vertexOffset + vertex] = color;
                }
                vertexOffset += cellVertexCount;
            }
            return colors;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private static HeatmapRgba Lerp(HeatmapRgba start, HeatmapRgba end, float amount)
        {
            return new HeatmapRgba(
                start.R + (end.R - start.R) * amount,
                start.G + (end.G - start.G) * amount,
                start.B + (end.B - start.B) * amount,
                start.A + (end.A - start.A) * amount);
        }

        private static void WriteCorner(
            double[] x,
            double[] y,
            double[] z,
            int index,
            double valueX,
            double valueY,
            double valueZ)
        {
            x[index] = valueX;
            y[index] = valueY;
            z[index] = valueZ;
        }

        private static void CopyTriangleIndices(
            int[] source,
            int[] destination,
            int destinationOffset,
            int vertexOffset)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[destinationOffset + i] = vertexOffset + source[i];
            }
        }
    }
}
#endif
