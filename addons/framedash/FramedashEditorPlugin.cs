#if TOOLS
#nullable enable

using System;
using Framedash.Editor;
using Godot;

namespace Framedash
{
    /// <summary>
    /// Editor-only plugin that wires the Framedash telemetry SDK into the host
    /// project and owns the cloud heatmap dock and viewport overlay. Enabling
    /// the plugin registers the "Framedash" autoload; disabling it removes the
    /// registration and editor visualization.
    ///
    /// The entire class is guarded by #if TOOLS so it compiles out of exported
    /// games -- only the editor build defines the TOOLS symbol. The autoload
    /// itself (TelemetrySDK) still ships in exported games.
    /// </summary>
    [Tool]
    public partial class FramedashEditorPlugin : EditorPlugin
    {
        private const string AutoloadName = "Framedash";
        private const string AutoloadPath = "res://addons/framedash/Runtime/TelemetrySDK.cs";
        private readonly FramedashHeatmapSettings _heatmapSettings =
            new FramedashHeatmapSettings();
        private FramedashHeatmapController? _heatmapController;
        private bool _heatmapLayoutRestored;

        public override void _EnterTree()
        {
            try
            {
                AddAutoloadSingleton(AutoloadName, AutoloadPath);
                _heatmapController = new FramedashHeatmapController(
                    this,
                    _heatmapSettings);
                _heatmapController.Initialize();
                if (_heatmapLayoutRestored)
                {
                    _heatmapController.ApplySettings(true);
                }
                SceneChanged += OnSceneChanged;
                SetProcess(true);
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Editor plugin initialization failed: "
                    + exception);
            }
        }

        public override void _ExitTree()
        {
            try
            {
                SetProcess(false);
                SceneChanged -= OnSceneChanged;
                _heatmapController?.Shutdown();
                _heatmapController = null;
                RemoveAutoloadSingleton(AutoloadName);
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Editor plugin shutdown failed: "
                    + exception);
            }
        }

        public override void _Process(double delta)
        {
            _heatmapController?.ProcessEditorFrame();
        }

        public override void _GetWindowLayout(ConfigFile configuration)
        {
            _heatmapSettings.Save(configuration);
        }

        public override void _SetWindowLayout(ConfigFile configuration)
        {
            try
            {
                _heatmapSettings.Load(configuration);
                _heatmapLayoutRestored = true;
                _heatmapController?.ApplySettings(true);
            }
            catch (Exception exception)
            {
                GD.PushError(
                    "[Framedash] Failed to restore editor heatmap settings: "
                    + exception);
            }
        }

        private void OnSceneChanged(Node sceneRoot)
        {
            _heatmapController?.OnSceneChanged(sceneRoot);
        }
    }
}
#endif
