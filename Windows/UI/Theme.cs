using System.Numerics;
using Dalamud.Interface.Utility;

namespace XIVPortStudio.Windows.UI;

/// <summary>
/// The one place colours and metrics live, so every window draws with the same
/// vocabulary. Sizes are authored at 100% and pass through <see cref="S"/>, which
/// applies Dalamud's global UI scale — hardcoded pixel widths clip at other scales.
/// </summary>
internal static class Theme
{
    // ── Colours ──────────────────────────────────────────────────────────────

    public static readonly Vector4 Accent = new(0.62f, 0.47f, 1.00f, 1f);
    public static readonly Vector4 Muted  = new(0.55f, 0.55f, 0.66f, 1f);
    public static readonly Vector4 Faint  = new(0.40f, 0.40f, 0.48f, 1f);
    public static readonly Vector4 Good   = new(0.41f, 0.86f, 0.48f, 1f);
    public static readonly Vector4 Warn   = new(0.96f, 0.76f, 0.30f, 1f);
    public static readonly Vector4 Bad    = new(0.94f, 0.36f, 0.42f, 1f);

    /// <summary>Subtle fill behind the status bar and panel headers.</summary>
    public static readonly Vector4 Band   = new(1f, 1f, 1f, 0.04f);

    /// <summary>Row highlight for the object the inspector is showing.</summary>
    public static readonly Vector4 Focus  = new(0.62f, 0.47f, 1.00f, 0.22f);

    // ── Metrics ──────────────────────────────────────────────────────────────

    /// <summary>Scales a 100%-authored pixel size by the user's global UI scale.</summary>
    public static float S(float px) => px * ImGuiHelpers.GlobalScale;

    /// <summary>Width of the label gutter in label/field rows.</summary>
    public static float LabelWidth => S(112);

    /// <summary>Width of the item browser on first use.</summary>
    public const float DefaultBrowserWidth = 300;

    /// <summary>Width of the inspector on first use.</summary>
    public const float DefaultInspectorWidth = 460;

    public const float MinPaneWidth = 180;

    /// <summary>Thickness of the draggable splitter between regions.</summary>
    public static float SplitterWidth => S(6);

    /// <summary>Side length of a texture thumbnail in the material inspector.</summary>
    public static float ThumbSize => S(40);
}
