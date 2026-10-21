using Klipp.Desktop.Services;

var inventory = AudioDeviceSettingsService.GetDevices();
Console.WriteLine($"Audio settings enumeration: {inventory.Outputs.Count} output(s), {inventory.Inputs.Count} input(s).");
Console.WriteLine($"Default output: {inventory.DefaultOutputName}");
Console.WriteLine($"Default input: {inventory.DefaultInputName}");
if (inventory.Outputs.Select(device => device.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inventory.Outputs.Count ||
    inventory.Inputs.Select(device => device.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inventory.Inputs.Count)
    throw new InvalidOperationException("Duplicate audio endpoints were returned.");
if (inventory.Outputs.Concat(inventory.Inputs).Any(device => string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.DisplayName)))
    throw new InvalidOperationException("An enumerated audio endpoint is missing its identity or name.");
using var service = new AudioDeviceSettingsService();
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    service.StartLevelTest(true, "never-open-this-device", cancelled.Token);
    throw new InvalidOperationException("A cancelled level test still opened a microphone.");
}
catch (OperationCanceledException) { }
if (service.Peak != 0) throw new InvalidOperationException("An idle level test retained a signal.");
service.Stop();
Console.WriteLine("PASS: audio device enumeration, endpoint identities, cancelled-test guard and idle cleanup. No audio was captured or played.");

if (args.Contains("--loopback-test", StringComparer.OrdinalIgnoreCase))
{
    var output = inventory.Outputs.FirstOrDefault()
        ?? throw new InvalidOperationException("Connect an output device before running the opt-in loopback test.");
    Console.WriteLine($"Opt-in loopback test: playing a short tone through {output.DisplayName}. No microphone is used; no audio is saved.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    var maximum = 0f;
    try
    {
        service.StartLevelTest(false, output.Id, timeout.Token);
        await Task.Delay(180, timeout.Token);
        var tone = service.PlayTestToneAsync(output.Id, timeout.Token);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(1500);
        while (DateTimeOffset.UtcNow < deadline)
        {
            maximum = Math.Max(maximum, service.Peak);
            if (service.TestError is { } error) throw new InvalidOperationException(error);
            await Task.Delay(15, timeout.Token);
        }
        await tone;
    }
    finally { service.Stop(); }
    if (maximum <= .002f)
        throw new InvalidOperationException($"The output level test did not detect its tone (peak={maximum:0.0000}). Check the selected output and mute state.");
    if (service.Peak != 0) throw new InvalidOperationException("Stopping the level test left a stale meter value.");
    Console.WriteLine($"PASS: explicit-output loopback detected the test tone (peak={maximum:0.0000}, {20 * Math.Log10(maximum):0.0} dB); Stop reset the meter to zero.");
}
