#if TOOLS
#nullable enable

using System;
using System.Collections.Generic;
using Framedash.Editor.Logic;
using Godot;

namespace Framedash.Editor
{
    internal sealed class FramedashHeatmapController
    {
        private static readonly int[] AllowedDays = { 1, 7, 14, 30 };
        private static readonly int[] AllowedCellSizes = { 5, 10, 25, 50 };

        private readonly EditorPlugin _plugin;
        private readonly FramedashHeatmapSettings _settings;
        private readonly FramedashHeatmapOverlay _overlay =
            new FramedashHeatmapOverlay();
        private readonly List<FramedashEditorLogic.MapInfo> _maps =
            new List<FramedashEditorLogic.MapInfo>();

        private FramedashEditorHttpClient? _httpClient;
        private ScrollContainer? _dock;
        private LineEdit? _readApiKey;
        private LineEdit? _apiBaseUrl;
        private LineEdit? _projectId;
        private OptionButton? _days;
        private OptionButton? _cellSize;
        private LineEdit? _eventName;
        private OptionButton? _map;
        private Button? _refreshMaps;
        private Button? _fetch;
        private Button? _frame;
        private CheckButton? _show;
        private SpinBox? _opacity;
        private SpinBox? _zOffset;
        private Label? _environmentKeyNotice;
        private Label? _stats;
        private Label? _status;

        private int _selectedMapIndex = -1;
        private int _queryRevision;
        private bool _busy;
        private bool _live;
        private bool _playing;
        private bool _restorePending;
        private bool _restoreHeatmap;
        private bool _handlingProgrammaticUi;
        private bool _synchronizationFailed;
        private string _statusText =
            "Configure a read key and project, then refresh maps.";

        public FramedashHeatmapController(
            EditorPlugin plugin,
            FramedashHeatmapSettings settings)
        {
            _plugin = plugin;
            _settings = settings;
        }

        public void Initialize()
        {
            _live = true;
            _httpClient = new FramedashEditorHttpClient(_plugin);
            BuildDock();
            ApplySettings(false);
            ProcessEditorFrame();
        }

        public void ApplySettings(bool scheduleRestore)
        {
            if (!_live || _dock == null)
            {
                return;
            }

            _handlingProgrammaticUi = true;
            try
            {
                _readApiKey!.Text = _settings.ReadApiKey ?? "";
                _apiBaseUrl!.Text = _settings.ApiBaseUrl ?? "";
                _projectId!.Text = _settings.ProjectId ?? "";
                _eventName!.Text = _settings.EventNameFilter ?? "";
                _days!.Selected = Math.Max(
                    0,
                    Array.IndexOf(AllowedDays, _settings.Days));
                _cellSize!.Selected = Math.Max(
                    0,
                    Array.IndexOf(AllowedCellSizes, _settings.CellSize));
                _show!.ButtonPressed = _settings.OverlayEnabled;
                _opacity!.Value = _settings.OverlayOpacity;
                _zOffset!.Value = _settings.ZOffset;
            }
            finally
            {
                _handlingProgrammaticUi = false;
            }

            _overlay.SetEnabled(_settings.OverlayEnabled);
            _overlay.SetZOffset(_settings.ZOffset);
            _synchronizationFailed = false;
            UpdateEnvironmentKeyNotice();
            UpdateMapOptions();
            UpdateUiState();

            if (scheduleRestore && CanRestoreMapSelection())
            {
                _restorePending = true;
                _restoreHeatmap = FramedashEditorLogic.ShouldRestoreOverlayData(
                    _settings.OverlayEnabled,
                    _settings.ProjectId,
                    _settings.SelectedMapId,
                    ResolveReadApiKey());
            }
        }

        public void ProcessEditorFrame()
        {
            if (!_live || _synchronizationFailed)
            {
                return;
            }

            try
            {
                bool playing = EditorInterface.Singleton.IsPlayingScene();
                Node? sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
                _overlay.SynchronizeScene(sceneRoot);
                _overlay.SetPlaying(playing);
                if (_playing != playing)
                {
                    _playing = playing;
                    UpdateUiState();
                }

                if (_restorePending && !_busy && !playing)
                {
                    _restorePending = false;
                    RefreshMaps(
                        restoreHeatmap: _restoreHeatmap,
                        preserveOverlay: _overlay.HasData,
                        allowFirstMapFallback: false);
                }
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Heatmap editor synchronization failed: "
                    + exception);
                _synchronizationFailed = true;
                _overlay.SetEnabled(false);
                SetStatus("The heatmap overlay was hidden after an editor error.");
            }
        }

        public void OnSceneChanged(Node sceneRoot)
        {
            _synchronizationFailed = false;
            RunSafely(() =>
            {
                _overlay.SynchronizeScene(sceneRoot);
                _overlay.SetPlaying(
                    EditorInterface.Singleton.IsPlayingScene());
            });
        }

        public void Shutdown()
        {
            if (!_live)
            {
                return;
            }

            _live = false;
            _restorePending = false;
            _queryRevision++;
            _busy = false;
            _httpClient?.Shutdown();
            _httpClient = null;
            _overlay.Shutdown();

            if (_dock != null && GodotObject.IsInstanceValid(_dock))
            {
#pragma warning disable CS0618
                // Godot 4.3 lacks EditorDock; the legacy API is the compatibility floor.
                _plugin.RemoveControlFromDocks(_dock);
#pragma warning restore CS0618
                _dock.QueueFree();
            }
            _dock = null;
        }

        private void BuildDock()
        {
            _dock = new ScrollContainer
            {
                Name = "Framedash Heatmap",
                CustomMinimumSize = new Vector2(320, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };

            var content = new VBoxContainer
            {
                Name = "FramedashHeatmapControls",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            content.AddThemeConstantOverride("separation", 8);
            _dock.AddChild(content);

            content.AddChild(new Label
            {
                Text = "Cloud Voxel Heatmap",
                ThemeTypeVariation = "HeaderMedium"
            });
            content.AddChild(new Label
            {
                Text = "Visualize aggregated Framedash cells at their recorded XYZ coordinates. The overlay is hidden while a game is running.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
            content.AddChild(new HSeparator());

            var connectionGrid = CreateFieldGrid();
            content.AddChild(connectionGrid);
            _readApiKey = AddLineField(
                connectionGrid,
                "Read API Key",
                "analytics:read key stored only in Godot's per-project editor metadata.",
                secret: true);
            _apiBaseUrl = AddLineField(
                connectionGrid,
                "API Base URL",
                "Framedash app origin. HTTPS is required except for canonical localhost.",
                secret: false);
            _projectId = AddLineField(
                connectionGrid,
                "Project ID",
                "Framedash project UUID.",
                secret: false);

            _environmentKeyNotice = new Label
            {
                Text = "Using FRAMEDASH_ANALYTICS_API_KEY from the Godot process. The value is not saved.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            content.AddChild(_environmentKeyNotice);

            var queryGrid = CreateFieldGrid();
            content.AddChild(queryGrid);
            _days = AddOptionField(queryGrid, "Days");
            for (int i = 0; i < AllowedDays.Length; i++)
            {
                _days.AddItem(AllowedDays[i] + (AllowedDays[i] == 1 ? " day" : " days"));
            }
            _cellSize = AddOptionField(queryGrid, "Cell Size");
            for (int i = 0; i < AllowedCellSizes.Length; i++)
            {
                _cellSize.AddItem(AllowedCellSizes[i].ToString());
            }
            _eventName = AddLineField(
                queryGrid,
                "Event Filter",
                "Optional exact event name.",
                secret: false);

            content.AddChild(new HSeparator());
            content.AddChild(new Label
            {
                Text = "Map",
                ThemeTypeVariation = "HeaderSmall"
            });
            _map = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            content.AddChild(_map);

            var mapButtons = new HBoxContainer();
            _refreshMaps = new Button
            {
                Text = "Refresh Maps",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _fetch = new Button
            {
                Text = "Fetch Heatmap",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            mapButtons.AddChild(_refreshMaps);
            mapButtons.AddChild(_fetch);
            content.AddChild(mapButtons);

            var overlayButtons = new HBoxContainer();
            _show = new CheckButton
            {
                Text = "Show",
                TooltipText = "Show the loaded heatmap in the 3D editor. It remains hidden while a game is running."
            };
            _frame = new Button
            {
                Text = "Frame Heatmap",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            overlayButtons.AddChild(_show);
            overlayButtons.AddChild(_frame);
            content.AddChild(overlayButtons);

            var displayGrid = CreateFieldGrid();
            content.AddChild(displayGrid);
            _opacity = AddSpinField(
                displayGrid,
                "Opacity",
                0,
                1,
                0.05);
            _zOffset = AddSpinField(
                displayGrid,
                "Z Offset",
                -1000000,
                1000000,
                0.1);

            AddLegend(content);
            _stats = new Label
            {
                Text = "No heatmap data loaded."
            };
            content.AddChild(_stats);

            content.AddChild(new HSeparator());
            _status = new Label
            {
                Text = _statusText,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            content.AddChild(_status);

            WireUiEvents();
#pragma warning disable CS0618
            // Godot 4.3 lacks EditorDock; the legacy API is the compatibility floor.
            _plugin.AddControlToDock(EditorPlugin.DockSlot.RightUl, _dock);
#pragma warning restore CS0618
        }

        private void WireUiEvents()
        {
            _readApiKey!.TextChanged += value => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.ReadApiKey = value;
                HandleConnectionSettingsChanged();
                UpdateEnvironmentKeyNotice();
            });
            _apiBaseUrl!.TextChanged += value => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.ApiBaseUrl = value;
                HandleConnectionSettingsChanged();
            });
            _projectId!.TextChanged += value => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.ProjectId = value;
                HandleConnectionSettingsChanged();
            });
            _days!.ItemSelected += index => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.Days = AllowedDays[(int)index];
                HandleQuerySettingsChanged(
                    "Days changed. Fetch heatmap data for the new selection.");
            });
            _cellSize!.ItemSelected += index => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.CellSize = AllowedCellSizes[(int)index];
                HandleQuerySettingsChanged(
                    "Cell size changed. Fetch heatmap data for the new selection.");
            });
            _eventName!.TextChanged += value => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.EventNameFilter = value;
                HandleQuerySettingsChanged(
                    "Event name changed. Fetch heatmap data for the new selection.");
            });
            _map!.ItemSelected += index => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _selectedMapIndex = (int)index - 1;
                _settings.SelectedMapId = HasSelectedMap()
                    ? _maps[_selectedMapIndex].MapId
                    : "";
                _queryRevision++;
                _overlay.ClearData();
                Persist();
                SetStatus(
                    HasSelectedMap()
                        ? "Map changed. Fetch heatmap data for the new selection."
                        : "Select a map.");
                UpdateUiState();
            });
            _refreshMaps!.Pressed += () => RunSafely(() => RefreshMaps());
            _fetch!.Pressed += () => RunSafely(FetchHeatmap);
            _frame!.Pressed += () => RunSafely(() =>
            {
                if (!FrameHeatmap())
                {
                    SetStatus("Open a 3D editor viewport before framing the heatmap.");
                }
            });
            _show!.Toggled += enabled => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.OverlayEnabled = enabled;
                _overlay.SetEnabled(enabled);
                Persist();
                UpdateUiState();
            });
            _opacity!.ValueChanged += value => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.OverlayOpacity = (float)value;
                _overlay.RefreshColors(_settings.OverlayOpacity);
                Persist();
            });
            _zOffset!.ValueChanged += value => RunSafely(() =>
            {
                if (_handlingProgrammaticUi)
                {
                    return;
                }
                _settings.ZOffset = (float)value;
                _overlay.SetZOffset(_settings.ZOffset);
                Persist();
            });
        }

        private void RefreshMaps(
            bool restoreHeatmap = false,
            bool preserveOverlay = false,
            bool allowFirstMapFallback = true)
        {
            if (_busy || _playing || _httpClient == null)
            {
                return;
            }

            _busy = true;
            string preferredMapId = _settings.SelectedMapId;
            _maps.Clear();
            _selectedMapIndex = -1;
            UpdateMapOptions();
            if (!preserveOverlay)
            {
                _overlay.ClearData();
            }
            SetStatus("Loading maps...");
            UpdateUiState();

            int requestRevision = _queryRevision;
            _httpClient.FetchMaps(_settings, (success, maps, error) =>
            {
                RunSafely(() =>
                {
                    _busy = false;
                    if (requestRevision != _queryRevision)
                    {
                        SetStatus("Settings changed while fetching maps. Refresh maps for the current project.");
                        UpdateUiState();
                        return;
                    }
                    if (!success)
                    {
                        SetStatus(error ?? "Unable to load maps.");
                        UpdateUiState();
                        return;
                    }

                    _maps.AddRange(
                        maps ?? new List<FramedashEditorLogic.MapInfo>());
                    _selectedMapIndex =
                        FramedashEditorLogic.ResolveMapSelectionIndex(
                            _maps,
                            _settings.SelectedMapId,
                            allowFirstMapFallback);
                    _settings.SelectedMapId = HasSelectedMap()
                        ? _maps[_selectedMapIndex].MapId
                        : "";
                    if (preserveOverlay
                        && !string.Equals(
                            preferredMapId,
                            _settings.SelectedMapId,
                            StringComparison.Ordinal))
                    {
                        _overlay.ClearData();
                    }
                    Persist();
                    UpdateMapOptions();
                    SetStatus(
                        _maps.Count == 0
                            ? "No maps were returned for this project."
                            : "Loaded "
                                + _maps.Count
                                + " map(s). Select a map and fetch its heatmap.");
                    UpdateUiState();
                    if (restoreHeatmap && HasSelectedMap())
                    {
                        FetchHeatmap();
                    }
                });
            });
        }

        private void FetchHeatmap()
        {
            if (_busy || _playing || _httpClient == null || !HasSelectedMap())
            {
                return;
            }

            _busy = true;
            SetStatus("Fetching cloud heatmap cells...");
            _overlay.ClearData();
            UpdateUiState();

            FramedashEditorLogic.MapInfo selectedMap =
                _maps[_selectedMapIndex];
            int cellSize = _settings.CellSize;
            int requestRevision = _queryRevision;
            _httpClient.FetchHeatmap(
                _settings,
                selectedMap.MapId,
                (success, cells, error) =>
                {
                    RunSafely(() =>
                    {
                        _busy = false;
                        if (requestRevision != _queryRevision)
                        {
                            SetStatus("Query settings changed while fetching. Fetch heatmap data for the new selection.");
                            UpdateUiState();
                            return;
                        }
                        if (!success)
                        {
                            SetStatus(error ?? "Unable to load heatmap data.");
                            UpdateUiState();
                            return;
                        }

                        List<FramedashEditorLogic.HeatmapCell> loadedCells =
                            cells ?? new List<FramedashEditorLogic.HeatmapCell>();
                        _overlay.SetData(
                            selectedMap,
                            loadedCells,
                            cellSize,
                            _settings.OverlayOpacity);
                        SetStatus(
                            loadedCells.Count == 10000
                                ? "Loaded 10000 cells. Results may be truncated at the API limit of 10,000 cells."
                                : "Loaded "
                                    + loadedCells.Count
                                    + " heatmap cell(s).");
                        UpdateUiState();
                    });
                });
        }

        private bool FrameHeatmap()
        {
            if (!_overlay.TryGetWorldBounds(
                    out FramedashEditorLogic.HeatmapBoundsData bounds))
            {
                return false;
            }

            SubViewport viewport =
                EditorInterface.Singleton.GetEditorViewport3D(0);
            Camera3D? camera = viewport.GetCamera3D();
            if (camera == null || !GodotObject.IsInstanceValid(camera))
            {
                return false;
            }

            var minimum = new Vector3(
                (float)bounds.MinX,
                (float)bounds.MinY,
                (float)bounds.MinZ);
            var maximum = new Vector3(
                (float)bounds.MaxX,
                (float)bounds.MaxY,
                (float)bounds.MaxZ);
            Vector3 center = (minimum + maximum) * 0.5f;
            Vector3 extents = (maximum - minimum) * 0.5f;
            float radius = Math.Max(extents.Length(), 1);
            Vector3 direction = camera.GlobalPosition - center;
            if (direction.LengthSquared() < 0.0001f)
            {
                direction = new Vector3(1, 1, 1);
            }
            direction = direction.Normalized();
            if (Math.Abs(direction.Dot(Vector3.Up)) > 0.98f)
            {
                direction = new Vector3(1, 0.7f, 1).Normalized();
            }

            float distance;
            if (camera.Projection == Camera3D.ProjectionType.Orthogonal)
            {
                camera.Size = Math.Max(
                    Math.Max(extents.X, extents.Y),
                    extents.Z) * 2.5f;
                distance = radius * 2;
            }
            else
            {
                float halfFovRadians = Mathf.DegToRad(camera.Fov * 0.5f);
                distance = radius / Math.Max(Mathf.Tan(halfFovRadians), 0.1f) * 1.25f;
            }
            camera.LookAtFromPosition(
                center + direction * distance,
                center,
                Vector3.Up);
            return true;
        }

        private void HandleConnectionSettingsChanged()
        {
            _queryRevision++;
            _restorePending = false;
            _maps.Clear();
            _selectedMapIndex = -1;
            _settings.SelectedMapId = "";
            _overlay.ClearData();
            Persist();
            UpdateMapOptions();
            SetStatus("Connection settings changed. Refresh maps for the new project.");
            UpdateUiState();
        }

        private void HandleQuerySettingsChanged(string status)
        {
            _queryRevision++;
            _restorePending = false;
            _overlay.ClearData();
            Persist();
            SetStatus(status);
            UpdateUiState();
        }

        private bool HasSelectedMap()
        {
            return _selectedMapIndex >= 0
                && _selectedMapIndex < _maps.Count;
        }

        private bool CanRestoreMapSelection()
        {
            return !string.IsNullOrWhiteSpace(_settings.SelectedMapId)
                && !string.IsNullOrWhiteSpace(_settings.ProjectId)
                && !string.IsNullOrWhiteSpace(ResolveReadApiKey());
        }

        private string ResolveReadApiKey()
        {
            return FramedashEditorLogic.ResolveReadApiKey(
                _settings.ReadApiKey,
                System.Environment.GetEnvironmentVariable(
                    "FRAMEDASH_ANALYTICS_API_KEY"));
        }

        private void Persist()
        {
            _plugin.QueueSaveLayout();
        }

        private void UpdateEnvironmentKeyNotice()
        {
            if (_environmentKeyNotice == null)
            {
                return;
            }
            _environmentKeyNotice.Visible =
                string.IsNullOrWhiteSpace(_settings.ReadApiKey)
                && !string.IsNullOrWhiteSpace(
                    System.Environment.GetEnvironmentVariable(
                        "FRAMEDASH_ANALYTICS_API_KEY"));
        }

        private void UpdateMapOptions()
        {
            if (_map == null)
            {
                return;
            }

            _handlingProgrammaticUi = true;
            try
            {
                _map.Clear();
                string[] names = FramedashEditorLogic.BuildMapNames(_maps);
                for (int i = 0; i < names.Length; i++)
                {
                    _map.AddItem(names[i]);
                }
                _map.Selected = _selectedMapIndex + 1;
            }
            finally
            {
                _handlingProgrammaticUi = false;
            }
        }

        private void UpdateUiState()
        {
            if (_dock == null)
            {
                return;
            }

            bool canUseNetwork = !_busy && !_playing;
            _refreshMaps!.Disabled = !canUseNetwork;
            _fetch!.Disabled = !canUseNetwork || !HasSelectedMap();
            _fetch.Text = _busy ? "Working..." : "Fetch Heatmap";
            _map!.Disabled = _busy || _playing;
            _frame!.Disabled = !_overlay.HasData || _playing;
            _show!.Disabled = _playing;
            _stats!.Text = _overlay.StatsText;
            _status!.Text = _statusText;
        }

        private void SetStatus(string status)
        {
            _statusText = status ?? "";
            if (_status != null)
            {
                _status.Text = _statusText;
            }
        }

        private void RunSafely(Action action)
        {
            if (!_live)
            {
                return;
            }
            try
            {
                action();
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Heatmap editor action failed: "
                    + exception);
                SetStatus("An unexpected Framedash editor error occurred.");
                _busy = false;
                UpdateUiState();
            }
        }

        private static GridContainer CreateFieldGrid()
        {
            return new GridContainer
            {
                Columns = 2,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
        }

        private static LineEdit AddLineField(
            GridContainer grid,
            string labelText,
            string tooltip,
            bool secret)
        {
            grid.AddChild(new Label
            {
                Text = labelText,
                TooltipText = tooltip
            });
            var field = new LineEdit
            {
                Secret = secret,
                TooltipText = tooltip,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            grid.AddChild(field);
            return field;
        }

        private static OptionButton AddOptionField(
            GridContainer grid,
            string labelText)
        {
            grid.AddChild(new Label { Text = labelText });
            var field = new OptionButton
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            grid.AddChild(field);
            return field;
        }

        private static SpinBox AddSpinField(
            GridContainer grid,
            string labelText,
            double minimum,
            double maximum,
            double step)
        {
            grid.AddChild(new Label { Text = labelText });
            var field = new SpinBox
            {
                MinValue = minimum,
                MaxValue = maximum,
                Step = step,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            grid.AddChild(field);
            return field;
        }

        private static void AddLegend(VBoxContainer content)
        {
            content.AddChild(new Label
            {
                Text = "Intensity",
                ThemeTypeVariation = "HeaderSmall"
            });
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "Low" });
            for (int i = 0; i < 5; i++)
            {
                FramedashEditorLogic.HeatmapRgba rgba =
                    FramedashEditorLogic.HeatmapColor(i / 4.0, 1);
                row.AddChild(new ColorRect
                {
                    Color = new Color(rgba.R, rgba.G, rgba.B, 1),
                    CustomMinimumSize = new Vector2(24, 12),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                });
            }
            row.AddChild(new Label { Text = "High" });
            content.AddChild(row);
        }
    }
}
#endif
