namespace Riji.Core;

public sealed record InputThresholds(int MouseDistance = 16, double MouseWindowSeconds = 0.8);

// Distinguish deliberate movement from gradual pointer drift without retaining input content.
public sealed class InputActivity(InputThresholds thresholds)
{
    private (int X, int Y, double At)? anchor;
    public bool MouseMoved(int x, int y, double seconds)
    {
        if (anchor is { } point && seconds - point.At <= thresholds.MouseWindowSeconds && seconds >= point.At)
        {
            if (Math.Abs((long)x - point.X) + Math.Abs((long)y - point.Y) >= thresholds.MouseDistance)
            { anchor = (x, y, seconds); return true; }
        }
        else anchor = (x, y, seconds);
        return false;
    }

    public static double IdleSeconds(uint currentTick, uint lastInputTick) => unchecked(currentTick - lastInputTick) / 1000.0;
}
