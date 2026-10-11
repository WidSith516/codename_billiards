namespace Rewind.Sim;

/// <content>
/// Level files live outside the kernel: the simulation never touches the filesystem, which is
/// what lets the M4 solvability solver link the same sim sources with no engine present.
/// </content>
public static class LevelIds
{
    public const string H5Rack = "h5_rack";
    public const string StraightLine = "straight_line";
    public const string Corridor = "corridor";
}
