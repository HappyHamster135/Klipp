using Klipp.Core.Capture;
using Klipp.Desktop.Services;

// Fake drive counters only. No files, captures or physical disks are changed.
const long GiB = 1024L * 1024 * 1024;
var checks = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var calls = new List<string>();
var reserve = new CaptureDiskReserve(3 * GiB, 4 * GiB);
var sameVolume = new CaptureDiskProtectionService(root => { calls.Add(root); return 8 * GiB; });
var snapshot = sameVolume.Inspect(@"C:\Klipp\temp\recordings", @"c:\Videos\Klipp\recording.mp4", reserve);
Require(!snapshot.ShouldStop && snapshot.RequiredBytes == 7 * GiB && snapshot.AvailableBytes == 8 * GiB,
    "A shared volume did not reserve both WAV repair and final MP4 output.");
Require(calls.Count == 1 && calls[0].Equals(@"C:\", StringComparison.OrdinalIgnoreCase),
    "The same volume was queried more than once or compared case-sensitively.");

snapshot = new CaptureDiskProtectionService(_ => 7 * GiB).Inspect(@"C:\temp", @"C:\Videos", reserve);
Require(snapshot.ShouldStop && snapshot.Detail.Contains(@"C:\", StringComparison.OrdinalIgnoreCase),
    "The exact shared-volume safety boundary did not stop capture with an explanation.");

calls.Clear();
snapshot = new CaptureDiskProtectionService(root =>
{
    calls.Add(root);
    return root.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) ? 4 * GiB : 5 * GiB;
}).Inspect(@"C:\temp", @"D:\Videos", reserve);
Require(!snapshot.ShouldStop && snapshot.RequiredBytes == 4 * GiB && snapshot.AvailableBytes == 5 * GiB,
    "Independent volumes incorrectly added both reserves to the output drive.");
Require(calls.SequenceEqual(new[] { @"C:\", @"D:\" }, StringComparer.OrdinalIgnoreCase),
    "Distinct capture/output volume counters were not both checked.");

calls.Clear();
snapshot = new CaptureDiskProtectionService(root =>
{
    calls.Add(root);
    if (!root.Equals(@"C:\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output should not be queried after temporary volume fails.");
    return 3 * GiB;
}).Inspect(@"C:\temp", @"D:\Videos", reserve);
Require(snapshot.ShouldStop && snapshot.RequiredBytes == 3 * GiB && calls.Count == 1,
    "Temporary drive exhaustion did not stop before checking an unrelated output counter.");

snapshot = new CaptureDiskProtectionService(root => root.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) ? 20 * GiB : 4 * GiB)
    .Inspect(@"C:\temp", @"D:\Videos\recording.mp4", reserve);
Require(snapshot.ShouldStop && snapshot.RequiredBytes == 4 * GiB && snapshot.Detail.Contains(@"D:\", StringComparison.OrdinalIgnoreCase),
    "Output drive exhaustion did not identify the drive needed for finalization.");

snapshot = new CaptureDiskProtectionService(_ => -1).Inspect(@"C:\temp", @"D:\Videos", reserve);
Require(snapshot.ShouldStop, "An invalid negative free-space counter was treated as safe.");
foreach (var failure in new Exception[] { new IOException("Drive removed"), new UnauthorizedAccessException("Drive inaccessible"), new ArgumentException("Invalid volume") })
{
    snapshot = new CaptureDiskProtectionService(_ => throw failure).Inspect(@"C:\temp", @"D:\Videos", reserve);
    Require(snapshot.ShouldStop && snapshot.Detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
        "An unavailable capture drive did not produce a safe actionable stop condition.");
}

calls.Clear();
snapshot = new CaptureDiskProtectionService(root =>
{
    calls.Add(root);
    if (root.Equals(@"D:\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Output drive removed");
    return 20 * GiB;
}).Inspect(@"C:\temp", @"D:\Videos", reserve);
Require(snapshot.ShouldStop && calls.Count == 2 && snapshot.Detail.Contains("Output drive removed"),
    "An output-only volume failure was ignored after the temporary drive passed.");

var counterUsed = false;
snapshot = new CaptureDiskProtectionService(_ => { counterUsed = true; return 100 * GiB; })
    .Inspect("bad\0path", @"C:\Videos", reserve);
Require(snapshot.ShouldStop && !counterUsed, "An invalid capture path reached a physical drive counter.");

var small = CaptureDiskProtectionPolicy.CalculateReserve(true, 1_000_000, 0, 3, 0, 8, 60);
snapshot = new CaptureDiskProtectionService(_ => 2 * GiB + 1).Inspect(@"C:\temp", @"D:\Videos", small);
Require(!snapshot.ShouldStop && snapshot.RequiredBytes == 2 * GiB,
    "Video-only capture did not retain the minimum two-GiB margin without premature stopping.");
snapshot = new CaptureDiskProtectionService(_ => 2 * GiB).Inspect(@"C:\temp", @"D:\Videos", small);
Require(snapshot.ShouldStop, "The minimum reserve boundary did not trigger protection.");

var longCapture = CaptureDiskProtectionPolicy.CalculateReserve(true, 4 * GiB, 12 * GiB, 7200, 4, 16, 120);
snapshot = new CaptureDiskProtectionService(_ => 17 * GiB).Inspect(@"C:\temp", @"C:\Videos", longCapture);
Require(snapshot.ShouldStop && snapshot.RequiredBytes > 17 * GiB,
    "A long four-stem recording reserved insufficient room for its repaired WAV plus final mixed/stem MP4.");

Console.WriteLine($"PASS: {checks} injected-counter disk protection checks; no disks filled and no capture stopped.");
