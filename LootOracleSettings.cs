using System.Collections.Generic;
using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace LootOracle;

public class FilterRule
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public SerializableColor Color { get; set; } = new SerializableColor { R = 50, G = 205, B = 50, A = 255 };
}

public class SerializableColor
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int A { get; set; } = 255;

    public SerializableColor() { }
    public SerializableColor(Color c) { R = c.R; G = c.G; B = c.B; A = c.A; }

    public System.Numerics.Vector4 ToVector4() =>
        new(R / 255f, G / 255f, B / 255f, A / 255f);

    public Color ToColor() => Color.FromArgb(A, R, G, B);
}

public class LootOracleSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    [Menu("Border Thickness")]
    public RangeNode<int> BorderThickness { get; set; } = new(2, 1, 10);

    [Menu("Border Deflation")]
    public RangeNode<int> BorderDeflation { get; set; } = new(2, 0, 20);

    [IgnoreMenu]
    public List<FilterRule> Rules { get; set; } = new();

    // Bump this in code when the embedded default rules change to force a rewrite.
    [IgnoreMenu]
    public int RulesVersion { get; set; } = 0;

    [IgnoreMenu]
    public bool DebugMode { get; set; } = false;

    // Legacy â€” kept for backwards compat, not used
    [IgnoreMenu]
    public TextNode ActiveProfile { get; set; } = new("Default");

    [IgnoreMenu]
    public TextNode CurrentQuery { get; set; } = new("");

    [IgnoreMenu]
    public TextNode ActiveBuildProfile { get; set; } = new("Generic");
}

