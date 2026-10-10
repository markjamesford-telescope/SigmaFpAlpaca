using SigmaFp.Camera.Session;
using SigmaFp.Camera.Sigma;

namespace SigmaFp.Camera;

public sealed record ExposurePlan(TriggerKind Kind, double RequestedSeconds, double ActualSeconds, byte ShutterCode)
{
    public bool Abortable => Kind == TriggerKind.ExternalBulb;
}

/// <summary>
/// Chooses how to take an exposure of a given length:
/// <list type="bullet">
/// <item>&lt;= 30 s: camera-timed, nearest shutter step.</item>
/// <item>&gt; 30 s with an external trigger: bulb timed by the driver (any length up to the camera limit).</item>
/// <item>exactly the camera bulb limit (300 s) without a trigger: camera bulb.</item>
/// <item>anything else: rejected (the old driver silently took 300 s instead).</item>
/// <item><paramref name="externalOnly"/> (Mass Storage mode): every exposure is a relay-timed bulb.</item>
/// </list>
/// </summary>
public sealed class ExposurePlanner(bool hasExternalTrigger, double cameraBulbSeconds = 300, double minExternalBulbSeconds = 1, bool externalOnly = false)
{
    public double MaxExposure => cameraBulbSeconds;
    public double MinExposure => externalOnly ? minExternalBulbSeconds : Apex.MinTimedSeconds;

    public ExposurePlan Plan(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < Apex.MinTimedSeconds * 0.5)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"Exposure must be at least {Apex.MinTimedSeconds} s");

        if (externalOnly)
        {
            if (!hasExternalTrigger) throw new InvalidOperationException("Mass Storage mode needs the relay (bulb trigger)");
            if (seconds < minExternalBulbSeconds - 1e-9 || seconds > cameraBulbSeconds + 0.5)
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                    $"In Mass Storage mode exposures are relay-timed bulbs of {minExternalBulbSeconds}-{cameraBulbSeconds} s");
            return new ExposurePlan(TriggerKind.ExternalBulb, seconds, seconds, SigmaMessages.ShutterBulb);
        }

        if (seconds <= Apex.MaxTimedSeconds)
        {
            var (code, actual) = Apex.Shutter(seconds);
            return new ExposurePlan(TriggerKind.Timed, seconds, actual, code);
        }

        if (seconds > cameraBulbSeconds + 0.5)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"The fp L's bulb limit is {cameraBulbSeconds} s");

        if (hasExternalTrigger && seconds >= minExternalBulbSeconds)
            return new ExposurePlan(TriggerKind.ExternalBulb, seconds, seconds, SigmaMessages.ShutterBulb);

        if (Math.Abs(seconds - cameraBulbSeconds) <= 0.5)
            return new ExposurePlan(TriggerKind.CameraBulb, seconds, cameraBulbSeconds, SigmaMessages.ShutterBulb);

        throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
            $"Without a bulb cable the fp L supports 1/8000-30 s or exactly {cameraBulbSeconds} s");
    }
}
