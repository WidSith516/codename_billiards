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
    // ---------------------------------------------------------------- theme state
    //
    // Every table and ball colour below is a MUTABLE static rather than `readonly`, because the
    // personalisation screen switches themes at runtime while the class is already loaded. The values
    // here are the DEFAULTS (deep teal felt, Okabe-Ito balls), and ApplyTheme replaces them.
    //
    // Colours that must NOT move with the theme (ball identity, the white hole, warning and meter
    // colours) stay fixed on purpose: the palette rules at the top of this file require that ball
    // identity is never carried by the felt colour, and a white hole must stay the opposite of a
    // pocket in every theme.
    public static int ActiveTheme { get; private set; }

    // ---------------------------------------------------------------- table
    public static Color RoomBg = new Color("#101418");
    public static Color RailWood = new Color("#5a3a22");
    public static Color RailInner = new Color("#3d322c");
    public static Color Felt = new Color("#14402f");
    public static Color FeltEdge = new Color("#0c2a1e");
    public static Color FeltGrid = new Color("#194a36");
    public static Color Pocket = new Color("#070b0a");
    public static Color PocketRim = new Color("#5a6b62");

    // ---------------------------------------------------------------- balls
    /// <summary>Cue ball: ivory, never used for an object ball.</summary>
    public static Color Cue = new Color("#f2efe3");

    /// <summary>Okabe-Ito colour-blind-safe set, assigned by ball id.</summary>
    public static Color[] ObjectFills =
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

    public static Color BallRingDark = new Color("#0a0f0d");
    public static Color BallLabelLight = new Color("#0b1410");

    // ---------------------------------------------------------------- themes

    /// <summary>Human-readable theme names, indexed to match <see cref="ApplyTheme"/>.</summary>
    public static readonly string[] ThemeNames =
    {
        "深空星野（默认）",
        "经典墨绿",
        "午夜蓝",
        "沙岩",
    };

    /// <summary>Swatch colour shown for each theme in the personalisation screen.</summary>
    public static readonly Color[] ThemeSwatches =
    {
        new Color("#0a0a1a"),
        new Color("#14402f"),
        new Color("#12233f"),
        new Color("#4a3b2a"),
    };

    /// <summary>
    /// Switch the whole presentation palette. Colours only -- no physics, no geometry, no gameplay.
    ///
    /// Theme 0 is the SPACE theme the user asked for: the cloth is deep starfield navy, the pockets are
    /// BLACK HOLES (near-void, with an accretion rim so they read as devouring rather than as holes in
    /// a table), and the balls are planets rendered from the Okabe-Ito set so identity survives.
    /// The white hole stays cyan-white in every theme -- it must always be the visual opposite of a
    /// black hole.
    /// </summary>
    public static void ApplyTheme(int index)
    {
        ActiveTheme = index < 0 || index >= ThemeNames.Length ? 0 : index;

        switch (ActiveTheme)
        {
            case 0: // 深空星野 -- space
                RoomBg = new Color("#050510");
                RailWood = new Color("#1a1f3a");
                RailInner = new Color("#11142a");
                Felt = new Color("#0b1030");
                FeltEdge = new Color("#060818");
                FeltGrid = new Color("#1a2450");
                // Black holes: not pure black, so they remain distinguishable from the void behind.
                Pocket = new Color("#01010a");
                PocketRim = new Color("#8a6bd6");
                Cue = new Color("#fdfdff");
                BallRingDark = new Color("#04040c");
                BallLabelLight = new Color("#04040c");
                RailShadow = new Color("#02030a");
                RailLight = new Color("#3b477e");
                CushionLight = new Color("#1b2554");
                CushionMid = new Color("#131a3c");
                CushionInner = new Color("#0a0f28");
                FeltShade = new Color("#060a1e");
                PocketMouth = new Color("#101433");
                PocketLiner = new Color("#000004");
                break;

            case 1: // 经典墨绿 -- the original felt
                RoomBg = new Color("#101418");
                RailWood = new Color("#5a3a22");
                RailInner = new Color("#3d322c");
                Felt = new Color("#14402f");
                FeltEdge = new Color("#0c2a1e");
                FeltGrid = new Color("#194a36");
                Pocket = new Color("#070b0a");
                PocketRim = new Color("#5a6b62");
                Cue = new Color("#f2efe3");
                BallRingDark = new Color("#0a0f0d");
                BallLabelLight = new Color("#0b1410");
                RailShadow = new Color("#0d0906");
                RailLight = new Color("#7a5233");
                CushionLight = new Color("#2f6b4c");
                CushionMid = new Color("#1d4c36");
                CushionInner = new Color("#123425");
                FeltShade = new Color("#0c2a1e");
                PocketMouth = new Color("#241a12");
                PocketLiner = new Color("#0b0806");
                break;

            case 2: // 午夜蓝
                RoomBg = new Color("#0b1220");
                RailWood = new Color("#1b2438");
                RailInner = new Color("#141a28");
                Felt = new Color("#12233f");
                FeltEdge = new Color("#0a1526");
                FeltGrid = new Color("#1b3357");
                Pocket = new Color("#04060c");
                PocketRim = new Color("#6f86a8");
                Cue = new Color("#f4f7fb");
                BallRingDark = new Color("#060a12");
                BallLabelLight = new Color("#05090f");
                RailShadow = new Color("#05080f");
                RailLight = new Color("#3f5578");
                CushionLight = new Color("#1e3a63");
                CushionMid = new Color("#152a49");
                CushionInner = new Color("#0c1a30");
                FeltShade = new Color("#0a1729");
                PocketMouth = new Color("#131b2b");
                PocketLiner = new Color("#020408");
                break;

            default: // 3 -- 沙岩
                RoomBg = new Color("#1a1610");
                RailWood = new Color("#6b5334");
                RailInner = new Color("#463a28");
                Felt = new Color("#4a3b2a");
                FeltEdge = new Color("#33291d");
                FeltGrid = new Color("#5c4a34");
                Pocket = new Color("#0d0a06");
                PocketRim = new Color("#8a7a5c");
                Cue = new Color("#fbf6ea");
                BallRingDark = new Color("#12100a");
                BallLabelLight = new Color("#12100a");
                RailShadow = new Color("#100d08");
                RailLight = new Color("#a3835a");
                CushionLight = new Color("#6b5540");
                CushionMid = new Color("#54422f");
                CushionInner = new Color("#3a2d1f");
                FeltShade = new Color("#3a2d1f");
                PocketMouth = new Color("#2b2217");
                PocketLiner = new Color("#080603");
                break;
        }
    }

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

    // ---------------------------------------------------------------- white hole
    /// <summary>White holes emit energy, so they must never read as a pocket (which swallows).
    /// Cold cyan-white against the orange rewind, so the two special table features stay distinct.</summary>
    public static readonly Color WhiteHoleRing = new Color("#9ff2ff");
    public static readonly Color WhiteHoleHalo = new Color("#6fd8f0");
    public static readonly Color WhiteHoleBody = new Color("#3fd0e8");
    public static readonly Color WhiteHoleCore = new Color("#ffffff");

    // ---------------------------------------------------------------- table depth
    // The table used to be a single flat felt rectangle plus a border, which read as a diagram
    // rather than a physical object. These give it a rail frame and a cushion bevel drawn OUTSIDE
    // the physics box, so the visible cushion edge and the rail collision boundary are the same line.
    public static Color RailShadow = new Color("#0d0906");
    public static Color RailLight = new Color("#7a5233");
    public static Color CushionLight = new Color("#2f6b4c");
    public static Color CushionMid = new Color("#1d4c36");
    public static Color CushionInner = new Color("#123425");
    public static Color FeltShade = new Color("#0c2a1e");
    public static Color PocketMouth = new Color("#241a12");
    public static Color PocketLiner = new Color("#0b0806");

    // ---------------------------------------------------------------- aim
    public static readonly Color AimLine = new Color("#f8f4e6");
    public static readonly Color AimFirstContact = new Color("#64e6ff");
    public static readonly Color AimTargetDir = new Color("#ffa03d");
    public static readonly Color AimCueDeflect = new Color("#b6ffd0");
    public static readonly Color AimBlocked = new Color("#ff5d5d");

    // ---------------------------------------------------------------- hud
    public static readonly Color PanelBg = new Color(0.04f, 0.06f, 0.07f, 0.86f);
    public static readonly Color PanelEdge = new Color("#3a4a44");

    /// <summary>Opaque card body. Distinct from <see cref="PanelBg"/>, which is translucent: a card
    /// sits on top of the table and must not let the felt read through its text.</summary>
    public static readonly Color Panel = new Color("#111a1c");

    /// <summary>Card body while hovered: lighter, so the pointer's target is unambiguous.</summary>
    public static readonly Color PanelRaised = new Color("#1c2a2d");

    /// <summary>
    /// The rail engine's colour: the ripple that spreads along a cushion and the streak that leaves
    /// the contact point. Deliberately NOT themed -- it is a mechanic readout, and the player has to
    /// be able to tell "the rail paid out" from "the rail merely bounced" in every theme.
    /// </summary>
    public static readonly Color RailBoost = new Color("#7fe7ff");

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
