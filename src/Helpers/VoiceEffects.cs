namespace THOBOTTO.Helpers;

// How a personality's voice is shaped after the engine: speed and pitch (12 is an octave), static, glitches, bit
// crushing, a metallic robot tone and echo. Cheap arithmetic on the audio, the same whichever engine spoke.
public sealed record VoiceEffects(double Speed = 1, int Pitch = 0, double Static = 0, double Glitch = 0, int Bitcrush = 16, int Robot = 0, double Echo = 0)
{
    public static readonly VoiceEffects None = new();

    public static readonly IReadOnlyList<(string Name, double Min, double Max, double Step, string What)> Known =
    [
        ("speed", 0.5, 2, 0.05, "how fast it talks; 1 is the engine's own"),
        ("pitch", -12, 12, 1, "semitones up or down; 12 is an octave"),
        ("static", 0, 1, 0.05, "noise behind the voice"),
        ("glitch", 0, 1, 0.05, "stutters, dropouts and crackle"),
        ("bitcrush", 2, 16, 1, "bits of depth; 16 is clean, 6 is crunchy"),
        ("robot", 0, 200, 5, "a metallic tone in Hz; 0 is off"),
        ("echo", 0, 1, 0.05, "how much echo"),
    ];

    public static VoiceEffects From(IReadOnlyDictionary<string, double>? values) => values is null || values.Count == 0 ? None : new(
        values.GetValueOrDefault("speed", 1),
        (int)values.GetValueOrDefault("pitch", 0),
        values.GetValueOrDefault("static", 0),
        values.GetValueOrDefault("glitch", 0),
        (int)values.GetValueOrDefault("bitcrush", 16),
        (int)values.GetValueOrDefault("robot", 0),
        values.GetValueOrDefault("echo", 0));

    // The first setting that isn't an effect, or is out of its range; null when all fit.
    public static string? Problem(IReadOnlyDictionary<string, double> values)
    {
        foreach (var (name, value) in values)
        {
            if (Known.FirstOrDefault(k => k.Name == name) is not { Name: not null } known)
                return $"\"{name}\" isn't an effect; they are {string.Join(", ", Known.Select(k => k.Name))}.";
            if (double.IsNaN(value) || value < known.Min || value > known.Max)
                return $"\"{name}\" goes from {known.Min} to {known.Max}.";
        }
        return null;
    }

    public static double Default(string name) => name switch { "speed" => 1, "bitcrush" => 16, _ => 0 };

    public static double Factor(int semitones) => Math.Pow(2, semitones / 12.0);

    // The whole line shaped, the same every time for the same line: glitches, static, bit crushing, the robot tone, echo.
    public short[] Apply(short[] samples, int rate, int seed)
    {
        if (samples.Length == 0)
            return samples;
        var shaped = samples;
        if (Glitch > 0)
            shaped = Glitched(shaped, rate, Glitch, seed);
        if (Static > 0)
            shaped = WithStatic(shaped, rate, Static, seed);
        if (Bitcrush < 16)
            shaped = Crushed(shaped, Bitcrush);
        if (Robot > 0)
            shaped = Robotic(shaped, rate, Robot);
        if (Echo > 0)
            shaped = Echoed(shaped, rate, Echo);
        return shaped;
    }

    // Pitch by resampling: higher comes out shorter, which the engine's speed made up for beforehand.
    public static short[] Pitched(short[] samples, double factor)
    {
        if (samples.Length == 0 || Math.Abs(factor - 1) < 1e-6)
            return samples;
        var length = Math.Max(1, (int)(samples.Length / factor));
        var pitched = new short[length];
        for (var i = 0; i < length; i++)
        {
            var at = i * factor;
            var j = Math.Min((int)at, samples.Length - 1);
            var next = Math.Min(j + 1, samples.Length - 1);
            pitched[i] = (short)(samples[j] + (samples[next] - samples[j]) * (at - j));
        }
        return pitched;
    }

    // Now and then a stutter (a slice repeated), a dropout or a crackle, more often with more amount.
    public static short[] Glitched(short[] samples, int rate, double amount, int seed)
    {
        var glitched = (short[])samples.Clone();
        var random = new Random(seed);
        var window = rate / 10;
        var slice = rate * 3 / 100;
        for (var start = 0; start + window <= glitched.Length; start += window)
        {
            if (random.NextDouble() >= amount * 0.6)
                continue;
            switch (random.Next(3))
            {
                case 0:
                    for (var i = slice; i < window; i++)
                        glitched[start + i] = glitched[start + i % slice];
                    break;
                case 1:
                    Array.Clear(glitched, start, Math.Min(random.Next(rate / 50, rate / 20), glitched.Length - start));
                    break;
                default:
                    var burst = random.Next(rate / 100, rate / 40);
                    for (var i = 0; i < burst && start + i < glitched.Length; i++)
                        glitched[start + i] = Clamp(glitched[start + i] + random.Next(-12000, 12000));
                    break;
            }
        }
        return glitched;
    }

    // Hiss behind the voice, with louder bursts now and then.
    public static short[] WithStatic(short[] samples, int rate, double amount, int seed)
    {
        var noisy = new short[samples.Length];
        var random = new Random(seed + 1);
        var hiss = amount * 1800;
        var burstLeft = 0;
        for (var i = 0; i < noisy.Length; i++)
        {
            if (i % (rate / 5) == 0 && random.NextDouble() < amount * 0.4)
                burstLeft = random.Next(rate / 40, rate / 8);
            var level = burstLeft > 0 ? hiss * 4 : hiss;
            if (burstLeft > 0)
                burstLeft--;
            noisy[i] = Clamp(samples[i] + (random.NextDouble() * 2 - 1) * level);
        }
        return noisy;
    }

    public static short[] Crushed(short[] samples, int bits)
    {
        var step = 1 << (16 - Math.Clamp(bits, 2, 16));
        var crushed = new short[samples.Length];
        for (var i = 0; i < crushed.Length; i++)
            crushed[i] = (short)(samples[i] / step * step);
        return crushed;
    }

    // Ring modulation with a tone, part of the voice left dry so the words stay clear.
    public static short[] Robotic(short[] samples, int rate, int hz)
    {
        var robotic = new short[samples.Length];
        for (var i = 0; i < robotic.Length; i++)
            robotic[i] = Clamp(samples[i] * (0.35 + 0.65 * Math.Sin(2 * Math.PI * hz * i / rate)));
        return robotic;
    }

    public static short[] Echoed(short[] samples, int rate, double amount)
    {
        var delay = rate * 18 / 100;
        var gain = 0.5 * amount;
        var echoed = new short[samples.Length + delay * 4];
        for (var i = 0; i < echoed.Length; i++)
            echoed[i] = Clamp((i < samples.Length ? samples[i] : 0) + (i >= delay ? echoed[i - delay] * gain : 0));
        return echoed;
    }

    private static short Clamp(double value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
