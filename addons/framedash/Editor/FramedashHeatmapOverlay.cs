#if TOOLS
#nullable enable

using System;
using System.Collections.Generic;
using Framedash.Editor.Logic;
using Godot;

namespace Framedash.Editor
{
    internal sealed class FramedashHeatmapOverlay
    {
        private const int MaxCells = 10000;

        private readonly List<FramedashEditorLogic.HeatmapRenderCell> _renderCells =
            new List<FramedashEditorLogic.HeatmapRenderCell>();
        private ArrayMesh? _mesh;
        private StandardMaterial3D? _material;
        private MeshInstance3D? _instance;
        private Node? _sceneRoot;
        private FramedashEditorLogic.HeatmapBoundsData _baseBounds;
        private bool _hasBounds;
        private bool _enabled;
        private bool _playing;
        private float _zOffset;

        public int CellCount => _renderCells.Count;
        public double MaxWeight { get; private set; }
        public bool HasData => _renderCells.Count > 0;
        public string StatsText => HasData
            ? CellCount + " cells · max weight " + MaxWeight.ToString("0.##")
            : "No heatmap data loaded.";

        public void SetData(
            FramedashEditorLogic.MapInfo map,
            IReadOnlyList<FramedashEditorLogic.HeatmapCell> cells,
            double cellSize,
            float opacity)
        {
            ClearData();
            if (map == null || cells == null)
            {
                return;
            }

            int count = Math.Min(cells.Count, MaxCells);
            double maxWeight = FramedashEditorLogic.FindMaxWeight(cells);
            MaxWeight = maxWeight;
            for (int i = 0; i < count; i++)
            {
                FramedashEditorLogic.HeatmapCell cell = cells[i];
                _renderCells.Add(FramedashEditorLogic.BuildHeatmapRenderCell(
                    cell,
                    map,
                    cellSize,
                    FramedashEditorLogic.NormalizeWeight(
                        cell.Weight,
                        maxWeight)));
            }

            _hasBounds = FramedashEditorLogic.TryBuildHeatmapBounds(
                _renderCells,
                0,
                out _baseBounds);
            BuildMesh(opacity);
            ApplyInstanceState();
        }

        public void ClearData()
        {
            _renderCells.Clear();
            MaxWeight = 0;
            _hasBounds = false;
            _mesh = null;
            if (IsValid(_instance))
            {
                _instance!.Mesh = null;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            ApplyInstanceState();
        }

        public void SetPlaying(bool playing)
        {
            if (_playing == playing)
            {
                return;
            }
            _playing = playing;
            ApplyInstanceState();
        }

        public void SetZOffset(float zOffset)
        {
            _zOffset = zOffset;
            ApplyInstanceState();
        }

        public void RefreshColors(float opacity)
        {
            if (!HasData)
            {
                return;
            }
            BuildMesh(opacity);
            ApplyInstanceState();
        }

        public void SynchronizeScene(Node? sceneRoot)
        {
            if (ReferenceEquals(sceneRoot, _sceneRoot) && IsValid(_instance))
            {
                return;
            }

            ReleaseInstance();
            _sceneRoot = sceneRoot;
            if (!IsValid(sceneRoot))
            {
                return;
            }

            _instance = new MeshInstance3D
            {
                Name = "FramedashHeatmapOverlay",
                TopLevel = true,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                IgnoreOcclusionCulling = true,
                Mesh = _mesh,
                MaterialOverride = EnsureMaterial()
            };
            sceneRoot!.AddChild(
                _instance,
                false,
                Node.InternalMode.Back);
            _instance.Owner = null;
            ApplyInstanceState();
        }

        public bool TryGetWorldBounds(
            out FramedashEditorLogic.HeatmapBoundsData bounds)
        {
            bounds = default;
            if (!_hasBounds)
            {
                return false;
            }
            bounds = new FramedashEditorLogic.HeatmapBoundsData(
                _baseBounds.MinX,
                _baseBounds.MinY,
                _baseBounds.MinZ + _zOffset,
                _baseBounds.MaxX,
                _baseBounds.MaxY,
                _baseBounds.MaxZ + _zOffset);
            return true;
        }

        public void Shutdown()
        {
            ReleaseInstance();
            _sceneRoot = null;
            _renderCells.Clear();
            MaxWeight = 0;
            _hasBounds = false;
            _mesh = null;
            _material = null;
        }

        private void BuildMesh(float opacity)
        {
            FramedashEditorLogic.HeatmapGeometryData geometry =
                FramedashEditorLogic.BuildHeatmapGeometry(
                    _renderCells,
                    opacity);
            if (geometry.X.Length == 0)
            {
                _mesh = null;
                if (IsValid(_instance))
                {
                    _instance!.Mesh = null;
                }
                return;
            }

            var vertices = new Vector3[geometry.X.Length];
            var colors = new Color[geometry.Colors.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                // Track() records GlobalPosition.X/Y/Z without an axis transform.
                // Rendering those same coordinates prevents editor-only drift.
                vertices[i] = new Vector3(
                    (float)geometry.X[i],
                    (float)geometry.Y[i],
                    (float)geometry.Z[i]);
                FramedashEditorLogic.HeatmapRgba rgba = geometry.Colors[i];
                colors[i] = new Color(rgba.R, rgba.G, rgba.B, rgba.A);
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.Index] = geometry.TriangleIndices;

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, EnsureMaterial());
            _mesh = mesh;
            if (IsValid(_instance))
            {
                _instance!.Mesh = _mesh;
            }
        }

        private StandardMaterial3D EnsureMaterial()
        {
            if (_material != null)
            {
                return _material;
            }

            _material = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Mix,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                VertexColorIsSrgb = true,
                AlbedoColor = Colors.White
            };
            return _material;
        }

        private void ApplyInstanceState()
        {
            if (!IsValid(_instance))
            {
                return;
            }

            _instance!.Position = new Vector3(0, 0, _zOffset);
            _instance.Visible = _enabled
                && !_playing
                && _mesh != null
                && HasData;
        }

        private void ReleaseInstance()
        {
            if (!IsValid(_instance))
            {
                _instance = null;
                return;
            }

            _instance!.Mesh = null;
            _instance.MaterialOverride = null;
            _instance.QueueFree();
            _instance = null;
        }

        private static bool IsValid(GodotObject? value)
        {
            return value != null && GodotObject.IsInstanceValid(value);
        }
    }
}
#endif
