using Godot;

namespace Rewind.Game;

/// <summary>
/// M2 presentation palette. Deliberately NOT the prototype's felt+gold scheme: the old
/// prototype was unreadable because ball identity was carried by colour alone and one ball was
/// near-black on dark green (port plan 搂4.5 "colour-blind friendly palette").
///
/// Rules encoded here:
///  * object-ball colours are the Okabe-Ito colour-blind-safe set (8 colours, distinguishable
///    under deuteranopia/protanopia/tritanopia);
///  * colour is NEVER the only channel - <see cref="BallStyles"/> draws an ID number and a
///    per-type shape glyph on every ball;
///  * every ball is drawn with a high-contrast ring so a ball can never dissolve into the felt.
/// </summary>
public static class Palette
{
    // ---------------------------------------------------------------- table
    public static readonly Color RoomBg = new Color("#101418");
    public static readonly Color RailWood = new Color("#2b2320");
    public static readonly Color RailInner = new Color("#3d322c");
    public static readonly Color Felt = new Color("#14402f");
    public static readonly Color FeltEdge = new Color("#0c2a1e");
    public static readonly Color FeltGrid = new Color("#194a36");
    public static readonly Color Pocket = new Color("#070b0a");
    public static readonly Color PocketRim = new Color("#5a6b62");

    // ---------------------------------------------------------------- balls
    /// <summary>Cue ball: ivory, never used for an object ball.</summary>
    public static readonly Color Cue = new Color("#f2efe3");

    /// <summary>Okabe-Ito colour-blind-safe set, assigned by ball id.</summary>
    public static readonly Color[] ObjectFills =
    {
        new Color("#e69f00"), // orange
        new Color("#56b4e9"), // sky blue
        new Color("#009e73"), // bluish green
        new Color("#f0e442"), // yellow
        new Color("#0072b2"), // blue
        new Color("#d55e00"), // vermillion
        new Color("#cc79a7"), // reddish purple
        new Color("#b8b8b8"), // grey
    };

    public static readonly Color BallRingDark = new Color("#0a0f0d");
    public static readonly Color BallLabelLight = new Color("#0b1410");

    // ---------------------------------------------------------------- trail
    /// <summary>Oldest end of the time gradient (desaturated, cold).</summary>
    public static readonly Color TrailOld = new Color("#22343a");
    /// <summary>Newest end of the time gradient (bright, saturated).</summary>
    public static readonly Color TrailNew = new Color("#ffd166");
    /// <summary>Rewind session boundary stripe.</summary>
    public static readonly Color SessionStripe = new Color("#7de2ff");
    /// <summary>Turn-back (rewind reversal) marker: diamond.</summary>
    public static readonly Color TurnMarker = new Color("#ff8a3d");
    /// <summary>Turn-forward marker: circle.</summary>
    public static readonly Color TurnForwardMarker = new Color("#7de2ff");

    // ---------------------------------------------------------------- rewind
    public static readonly Color RewindCue = new Color("#ff7a1a");
    public static readonly Color RewindTraversed = new Color("#ffb703");
    public static readonly Color RewindRemaining = new Color("#4d6b7a");
    public static readonly Color RewindTarget = new Color("#ffffff");
    public static readonly Color ContactSoon = new Color("#ff2d55");
    public static readonly Color ContactSoonSoft = new Color("#ffb0bf");
    public static readonly Color GhostBall = new Color(0.95f, 0.95f, 0.95f, 0.30f);

    // ---------------------------------------------------------------- aim
    public static readonly Color AimLine = new Color("#f8f4e6");
    public static readonly Color AimFirstContact = new Color("#64e6ff");
    public static readonly Color AimTargetDir = new Color("#ffa03d");
    public static readonly Color AimCueDeflect = new Color("#b6ffd0");
    public static readonly Color AimBlocked = new Color("#ff5d5d");

    // ---------------------------------------------------------------- hud
    public static readonly Color PanelBg = new Color(0.04f, 0.06f, 0.07f, 0.86f);
    public static readonly Color PanelEdge = new Color("#3a4a44");
    public static readonly Color TextMain = new Color("#eef3ef");
    public static readonly Color TextDim = new Color("#9fb2a9");
    public static readonly Color Accent = new Color("#ffd166");
    public static readonly Color Warn = new Color("#ff7a1a");
    public static readonly Color Good = new Color("#66e08a");
    public static readonly Color Bad = new Color("#ff5d5d");
    public static readonly Color MeterLow = new Color("#63c9ff");
    public static readonly Color MeterHigh = new Color("#ff4d4d");

    /// <summary>Fill colour for an object ball with the given id (never the cue).</summary>
    public static Color ObjectFill(int id)
    {
        var i = id - 1;
        if (i < 0) i = 0;
        return ObjectFills[i % ObjectFills.Length];
    }

    /// <summary>Perceived luminance, used to pick a readable label colour on a ball fill.</summary>
    public static float Luma(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
}
