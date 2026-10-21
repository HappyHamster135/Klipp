using Klipp.Core.Capture;

namespace Klipp.Tests.Capture;

public sealed class CaptureSelectionPolicyTests
{
    [Theory]
    [InlineData("cs2")]
    [InlineData("ROBLOXPLAYERBETA.EXE")]
    [InlineData("C:\\Games\\eldenring.exe")]
    [InlineData("FortniteClient-Win64-Shipping")]
    public void KnownGameExecutablesDoNotNeedFullscreen(string process) =>
        Assert.True(CaptureGamePolicy.Classify(new(process)).IsGame);

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("steam")]
    [InlineData("EpicGamesLauncher")]
    [InlineData("UnrealEditor")]
    [InlineData("Unity")]
    [InlineData("wallpaper64")]
    [InlineData("Klipp.Desktop")]
    [InlineData("my-game-launcher")]
    public void BrowsersLaunchersShellAndToolsNeverBecomeAutomaticGames(string process) =>
        Assert.False(CaptureGamePolicy.Classify(new(process, "UnityWndClass", true, true, true, true)).IsGame);

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, true, true, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, true, false, true)]
    public void UnknownAppsNeedIndependentGameEvidence(bool engine, bool renderer, bool location, bool fullscreen, bool expected) =>
        Assert.Equal(expected, CaptureGamePolicy.Classify(new("unknown-app", "", engine, renderer, location, fullscreen)).IsGame);

    [Fact]
    public void ManualChoiceWinsAndUnavailableManualChoiceNeverFallsBack()
    {
        CaptureSelectionCandidate[] windows = [new("chosen-app", true, false), new("game", true, true, true)];
        Assert.Equal("chosen-app", AutomaticCapturePolicy.Select(windows, "game", "chosen-app"));
        Assert.Null(AutomaticCapturePolicy.Select(windows, "game", "missing-app"));
    }

    [Fact]
    public void ForegroundGameWinsOverAnExistingGame()
    {
        CaptureSelectionCandidate[] windows = [new("old", true, true), new("new", true, true, true)];
        Assert.Equal("new", AutomaticCapturePolicy.Select(windows, "old"));
    }

    [Fact]
    public void AltTabAndMinimizePreserveTheCurrentGame()
    {
        CaptureSelectionCandidate[] windows = [new("game", true, true, IsMinimized: true), new("browser", true, false, true)];
        Assert.Equal("game", AutomaticCapturePolicy.Select(windows, "game"));
        Assert.Null(AutomaticCapturePolicy.Select(windows, null));
    }

    [Fact]
    public void ClosedGameOrDisabledDetectionWaitsWithoutCapturingDesktop()
    {
        CaptureSelectionCandidate[] windows = [new("browser", true, false, true)];
        Assert.Null(AutomaticCapturePolicy.Select(windows, "closed-game"));
        Assert.Null(AutomaticCapturePolicy.Select([new("game", true, true, true)], "game", autoDetectGames: false));
    }

    [Fact]
    public void ASingleVisibleGameCanBeChosenWhileKlippHasFocusButSeveralWait()
    {
        Assert.Equal("game", AutomaticCapturePolicy.Select([new("game", true, true)], null));
        Assert.Null(AutomaticCapturePolicy.Select([new("one", true, true), new("two", true, true)], null));
        Assert.Null(AutomaticCapturePolicy.Select([new("gone", false, true, true)], null));
    }
}
