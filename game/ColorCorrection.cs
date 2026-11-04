using Godot;

namespace SeaDogs;

/// <summary>Screen-wide contrast / gamma / brightness (the video options sliders -> XI_SetColorCorrection). contrast and gamma 1.0 = unchanged, brightness is added in 0..255 units (-100..100, 0 = unchanged).</summary>
public partial class ColorCorrection : CanvasLayer
{
    const string Code = @"
shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform float contrast = 1.0;
uniform float gamma = 1.0;
uniform float bright = 0.0;
void fragment() {
    vec3 c = texture(screen, SCREEN_UV).rgb;
    c = (c - 0.5) * contrast + 0.5;
    c += bright / 255.0;
    c = pow(max(c, vec3(0.0)), vec3(1.0 / max(gamma, 0.05)));
    COLOR = vec4(c, 1.0);
}";
    readonly ShaderMaterial mat = new() { Shader = new Shader { Code = Code } };
    readonly ColorRect rect = new();

    public ColorCorrection()
    {
        Layer = 100; Name = "ColorCorrection";
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rect.MouseFilter = Control.MouseFilterEnum.Ignore;
        rect.Material = mat;
        rect.Visible = false;
        AddChild(rect);
    }

    public void Set(float contrast, float gamma, float bright)
    {
        mat.SetShaderParameter("contrast", contrast); mat.SetShaderParameter("gamma", gamma); mat.SetShaderParameter("bright", bright);
        rect.Visible = Mathf.Abs(contrast - 1f) > 0.01f || Mathf.Abs(gamma - 1f) > 0.01f || Mathf.Abs(bright) > 0.5f;
    }
}
