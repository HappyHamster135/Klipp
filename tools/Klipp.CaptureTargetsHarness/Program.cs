using Klipp.Capture.Models;
using Klipp.Desktop.Services;

var target = new CaptureTarget { Kind = CaptureTargetKind.Window, Handle = 123, DisplayName = "Test target", ProcessId = 456 };
var settings = new KlippSettings
{
    CaptureApplicationProfiles = [new() { ProcessName = "cs2", DisplayName = "Game", FrameRate = 30, BitrateMbps = 16, ResolutionMode = "1080p" }]
};
var configured = new CaptureOptions { Target = target, CaptureSystemAudio = false, CaptureCursor = false, HideCaptureBorder = false };
var profile = new CaptureProfileService().ResolveForApplication(settings, configured, "cs2");
if (!ReferenceEquals(profile.Options.Target, target) || profile.Options.CaptureSystemAudio || profile.Options.CaptureCursor ||
    profile.Options.HideCaptureBorder || profile.Options.FrameRate != 30 || profile.Options.BitrateMbps != 16)
    throw new InvalidOperationException("An application profile lost the selected target or capture privacy options.");
if (configured.GetOutputSize(2560, 1600) != (1152, 720) || configured.GetOutputSize(1920, 1080) != (1280, 720))
    throw new InvalidOperationException("Capture dimensions did not follow the selected source's aspect ratio.");
Console.WriteLine("PASS: selected target, cursor/system audio options and actual source aspect ratio survive application profiles.");
var unavailable = target with { Handle = 0 };
var targets = new CaptureTargetService();
if (targets.IsEligible(unavailable)) throw new InvalidOperationException("An unavailable explicit window was considered capturable.");
var rejected = false;
try { targets.GetCaptureSize(unavailable); }
catch (InvalidOperationException) { rejected = true; }
if (!rejected) throw new InvalidOperationException("An unavailable explicit window fell back to desktop capture.");
Console.WriteLine("PASS: an unavailable explicit window is rejected without capturing a fallback display.");
await CaptureRoutingChecks.RunAsync(target);
