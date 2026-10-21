using Klipp.Core.Capture;

namespace Klipp.Tests.Capture;

public sealed class CaptureDiskProtectionPolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void LongMultitrackRecordingReservesBothRepairCopyAndFinalOutput()
    {
        var reserve = CaptureDiskProtectionPolicy.CalculateReserve(true, 2 * GiB, 3 * GiB, 7200, 4, 16, 60);
        reserve.TemporaryBytes.ShouldBeGreaterThan(3 * GiB);
        reserve.OutputBytes.ShouldBeGreaterThan(2 * GiB + 7200L * 5 * 32000);
        var shared = CaptureDiskProtectionPolicy.RequiredBytes(reserve, true, true);
        shared.ShouldBeGreaterThan(5 * GiB);
        CaptureDiskProtectionPolicy.ShouldStop(5 * GiB, shared).ShouldBeTrue();
    }

    [Fact]
    public void DifferentVolumesReserveOnlyTheirOwnFinalizationWork()
    {
        var reserve = CaptureDiskProtectionPolicy.CalculateReserve(true, 4 * GiB, 3 * GiB, 1200, 2, 35, 60);
        var temporary = CaptureDiskProtectionPolicy.RequiredBytes(reserve, false, true);
        var output = CaptureDiskProtectionPolicy.RequiredBytes(reserve, false, false);
        temporary.ShouldBe(reserve.TemporaryBytes);
        output.ShouldBe(reserve.OutputBytes);
        temporary.ShouldBeLessThan(4 * GiB);
        output.ShouldBeLessThan(5 * GiB);
    }

    [Fact]
    public void SmallAndVideoOnlyCapturesKeepMinimumSafetySpace()
    {
        var reserve = CaptureDiskProtectionPolicy.CalculateReserve(true, 1_000_000, 0, 10, 0, 8, 60);
        CaptureDiskProtectionPolicy.RequiredBytes(reserve, true, true).ShouldBe(2 * GiB);
        CaptureDiskProtectionPolicy.ShouldStop(2 * GiB, 2 * GiB).ShouldBeTrue();
        CaptureDiskProtectionPolicy.ShouldStop(2 * GiB + 1, 2 * GiB).ShouldBeFalse();
    }

    [Fact]
    public void InstantClipsIncludeRoomForTheLastClipAndBoundUntrustedInputs()
    {
        var reserve = CaptureDiskProtectionPolicy.CalculateReserve(false, long.MaxValue, long.MaxValue, double.NaN, 99, 999, 999);
        reserve.OutputBytes.ShouldBeGreaterThan(120L * 120_000_000 / 8);
        reserve.OutputBytes.ShouldBeLessThan(3 * GiB);
        reserve.TemporaryBytes.ShouldBeLessThan(GiB);
    }

    [Fact]
    public void OversizedValuesSaturateWithoutWrappingIntoASmallReserve()
    {
        var reserve = CaptureDiskProtectionPolicy.CalculateReserve(true, long.MaxValue, long.MaxValue, double.MaxValue, 4, 120, 120);
        reserve.TemporaryBytes.ShouldBe(long.MaxValue);
        reserve.OutputBytes.ShouldBe(long.MaxValue);
        CaptureDiskProtectionPolicy.RequiredBytes(reserve, true, true).ShouldBe(long.MaxValue);
        CaptureDiskProtectionPolicy.ShouldStop(-1, long.MaxValue).ShouldBeTrue();
    }
}
