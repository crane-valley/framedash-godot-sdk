#if TOOLS
using Godot;

namespace Framedash
{
    /// <summary>
    /// Editor-only plugin that wires the Framedash telemetry SDK into the host
    /// project as an autoload singleton. Enabling the plugin registers the
    /// "Framedash" autoload; disabling it removes the registration.
    ///
    /// The entire class is guarded by #if TOOLS so it compiles out of exported
    /// games -- only the editor build defines the TOOLS symbol. The autoload
    /// itself (TelemetrySDK) still ships in exported games; this class merely
    /// manages its project-settings registration at edit time.
    /// </summary>
    [Tool]
    public partial class FramedashEditorPlugin : EditorPlugin
    {
        private const string AutoloadName = "Framedash";
        private const string AutoloadPath = "res://addons/framedash/Runtime/TelemetrySDK.cs";

        public override void _EnterTree()
        {
            AddAutoloadSingleton(AutoloadName, AutoloadPath);
        }

        public override void _ExitTree()
        {
            RemoveAutoloadSingleton(AutoloadName);
        }
    }
}
#endif
