namespace Klipp.Core.Editing;

public static class EditorAudioDuckingMath
{
    public const double Threshold = .02;
    public static bool IsControlSource(string role, bool hasMicrophone, bool isSourceAudio) =>
        isSourceAudio && (role == "microphone" || !hasMicrophone && string.IsNullOrEmpty(role));

    public static double Gain(double level, double strength)
    {
        if (!double.IsFinite(level) || level <= Threshold || !double.IsFinite(strength) || strength <= 0) return 1;
        var ratio = 1 + 15 * Math.Clamp(strength, 0, 1);
        return Math.Clamp(Math.Pow(Threshold / level, 1 - 1 / ratio), 0, 1);
    }
}

public sealed class EditorAudioDuckingEnvelope
{
    public double Level { get; private set; }

    public double Update(double level, double elapsedSeconds, bool reset = false)
    {
        level = double.IsFinite(level) ? Math.Max(0, level) : 0;
        if (reset) return Level = level;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return Level;
        var timeConstant = level > Level ? .02 : .35;
        return Level = level + (Level - level) * Math.Exp(-elapsedSeconds / timeConstant);
    }
}
