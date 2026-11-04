#if TOOLS
#nullable enable

using Framedash.Editor.Logic;
using Godot;

namespace Framedash.Editor
{
    internal sealed partial class FramedashHeatmapController
    {
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
                "Height Offset",
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
