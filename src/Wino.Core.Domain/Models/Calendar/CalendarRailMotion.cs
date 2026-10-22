#nullable enable
using System;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Decides where a moving calendar rail comes to rest. Positions are fractional unit indices:
/// 3.4 means the viewport starts 40% into unit 3. Platform surfaces own the pointer, wheel and
/// animation plumbing; they ask this class for the snap unit to glide to.
/// </summary>
public static class CalendarRailMotion
{
    /// <summary>
    /// How far past a snap point the rail must travel, as a fraction of the gap to the next one,
    /// before a forward movement settles on the next snap point instead of falling back.
    /// </summary>
    public const double ForwardSnapThreshold = 0.3d;

    /// <summary>
    /// The mirror of <see cref="ForwardSnapThreshold"/> for movement towards earlier units.
    /// </summary>
    public const double BackwardSnapThreshold = 0.7d;

    /// <summary>
    /// How long a flick keeps travelling, in milliseconds, when its velocity is projected forward.
    /// </summary>
    public const double FlickProjectionMilliseconds = 320d;

    /// <summary>
    /// The nearest snap unit to a position. <paramref name="direction"/> biases the choice towards
    /// the unit the rail is moving to, so a short wheel movement in one direction still turns a
    /// page instead of springing back.
    /// </summary>
    public static int ChooseSnapUnit(CalendarRailSpace space, double position, int direction)
    {
        ArgumentNullException.ThrowIfNull(space);

        var before = space.SnapAtOrBefore((int)Math.Floor(position));
        var after = space.SnapAtOrAfter((int)Math.Ceiling(position));

        if (before == after)
        {
            return before;
        }

        var fraction = (position - before) / (after - before);
        var threshold = direction > 0 ? ForwardSnapThreshold : direction < 0 ? BackwardSnapThreshold : 0.5d;

        return fraction > threshold ? after : before;
    }

    /// <summary>
    /// Where a rail that scrolls freely comes to rest. The month view stops wherever the movement
    /// ends, so week rows scroll steplessly. The timed views stop on a whole day column, biased in
    /// the direction of travel, so no column is left cut in half. Whole periods are only turned by
    /// the arrow buttons, the keyboard and an external range change.
    /// </summary>
    public static double ChooseRestPosition(CalendarRailSpace space, double position, int direction)
    {
        ArgumentNullException.ThrowIfNull(space);

        if (space.Axis == CalendarRailAxis.Vertical)
        {
            return position;
        }

        var before = Math.Floor(position);
        var fraction = position - before;
        var threshold = direction > 0 ? ForwardSnapThreshold : direction < 0 ? BackwardSnapThreshold : 0.5d;

        return fraction > threshold ? before + 1d : before;
    }

    /// <summary>
    /// The rest position of a released drag: the velocity is projected forward, so a flick keeps
    /// gliding and a slow release stops close to where the pointer let go.
    /// </summary>
    /// <param name="velocityUnitsPerMillisecond">Signed velocity in units per millisecond; positive moves to later units.</param>
    public static double ChooseReleasePosition(CalendarRailSpace space, double position, double velocityUnitsPerMillisecond)
    {
        var projected = position + (velocityUnitsPerMillisecond * FlickProjectionMilliseconds);

        return ChooseRestPosition(space, projected, Math.Sign(velocityUnitsPerMillisecond));
    }

    /// <summary>
    /// Clamps a long jump so the glide to <paramref name="target"/> starts at most
    /// <paramref name="maxTravelUnits"/> away. Jumping from March to today in November lands
    /// close first, then glides, instead of flying through eight months.
    /// </summary>
    public static double ClampGlideStart(double position, int target, double maxTravelUnits)
    {
        var distance = target - position;

        if (Math.Abs(distance) <= maxTravelUnits)
        {
            return position;
        }

        return target - (Math.Sign(distance) * maxTravelUnits);
    }

    /// <summary>
    /// Cubic ease-out shared by every platform's glide, so a period turn feels the same everywhere.
    /// </summary>
    public static double EaseOut(double progress)
    {
        var clamped = Math.Clamp(progress, 0d, 1d);
        var inverse = 1d - clamped;

        return 1d - (inverse * inverse * inverse);
    }
}
