using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

public sealed record MissingMediaSearchRequest(
    string OriginalPath,
    bool IsAudio,
    bool IsGraphic,
    double ExpectedDurationSeconds);

public sealed record MissingMediaSearchResult(
    MissingMediaSearchRequest Request,
    string? ReplacementPath,
    double? DurationSeconds,
    int CandidateCount,
    bool IsConfident,
    string Status);

public sealed class MissingMediaSearchService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".mp4"
    };

    private static readonly HashSet<string> GraphicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };

    private readonly MediaProbeService _mediaProbeService;

    public MissingMediaSearchService(MediaProbeService? mediaProbeService = null)
    {
        _mediaProbeService = mediaProbeService ?? new MediaProbeService();
    }

    public async Task<IReadOnlyList<MissingMediaSearchResult>> FindMatchesAsync(
        IReadOnlyList<MissingMediaSearchRequest> requests,
        IEnumerable<string> searchRoots,
        CancellationToken cancellationToken = default)
    {
        var roots = NormalizeRoots(searchRoots);
        var results = new List<MissingMediaSearchResult>(requests.Count);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await Task.Run(
                    () => FindCandidates(request, roots, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            results.Add(await ResolveCandidateAsync(request, candidates, cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    private async Task<MissingMediaSearchResult> ResolveCandidateAsync(
        MissingMediaSearchRequest request,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new MissingMediaSearchResult(
                request,
                ReplacementPath: null,
                DurationSeconds: null,
                CandidateCount: 0,
                IsConfident: false,
                Status: "No matching file found");
        }

        if (request.IsGraphic)
        {
            return candidates.Count == 1
                ? new MissingMediaSearchResult(
                    request,
                    candidates[0],
                    DurationSeconds: null,
                    CandidateCount: 1,
                    IsConfident: true,
                    Status: "Exact graphic found")
                : new MissingMediaSearchResult(
                    request,
                    ReplacementPath: null,
                    DurationSeconds: null,
                    CandidateCount: candidates.Count,
                    IsConfident: false,
                    Status: "Several graphics have the same name");
        }

        var readableCandidates = new List<(string Path, double DurationSeconds, double Difference)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = await _mediaProbeService.GetDurationAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (duration is null || duration <= TimeSpan.FromSeconds(0.1))
                continue;

            var difference = request.ExpectedDurationSeconds > 0.1
                ? Math.Abs(duration.Value.TotalSeconds - request.ExpectedDurationSeconds)
                : 0;
            readableCandidates.Add((candidate, duration.Value.TotalSeconds, difference));
        }

        if (readableCandidates.Count == 0)
        {
            return new MissingMediaSearchResult(
                request,
                ReplacementPath: null,
                DurationSeconds: null,
                CandidateCount: candidates.Count,
                IsConfident: false,
                Status: "Matching file could not be read");
        }

        if (request.ExpectedDurationSeconds <= 0.1 && readableCandidates.Count == 1)
        {
            var only = readableCandidates[0];
            return new MissingMediaSearchResult(
                request,
                only.Path,
                only.DurationSeconds,
                candidates.Count,
                IsConfident: true,
                Status: "Exact media found");
        }

        var tolerance = Math.Max(2, request.ExpectedDurationSeconds * 0.02);
        var durationMatches = readableCandidates
            .Where(candidate => candidate.Difference <= tolerance)
            .OrderBy(candidate => candidate.Difference)
            .ToList();
        if (durationMatches.Count == 1)
        {
            var match = durationMatches[0];
            return new MissingMediaSearchResult(
                request,
                match.Path,
                match.DurationSeconds,
                candidates.Count,
                IsConfident: true,
                Status: "Name and duration match");
        }

        return new MissingMediaSearchResult(
            request,
            ReplacementPath: null,
            DurationSeconds: null,
            CandidateCount: candidates.Count,
            IsConfident: false,
            Status: durationMatches.Count > 1
                ? "Several files have the same name and duration"
                : "Filename matched, but duration did not");
    }

    private static IReadOnlyList<string> FindCandidates(
        MissingMediaSearchRequest request,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(request.OriginalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            return Array.Empty<string>();

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(root, fileName, options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSupportedExtension(candidate, request))
                        continue;

                    candidates.Add(Path.GetFullPath(candidate));
                    if (candidates.Count >= 24)
                        return candidates.ToList();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return candidates.ToList();
    }

    private static bool IsSupportedExtension(string path, MissingMediaSearchRequest request)
    {
        var extension = Path.GetExtension(path);
        return request.IsGraphic
            ? GraphicExtensions.Contains(extension)
            : request.IsAudio
                ? AudioExtensions.Contains(extension)
                : VideoExtensions.Contains(extension);
    }

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> searchRoots)
    {
        var candidates = searchRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root =>
            {
                try { return Path.GetFullPath(root.Trim()); }
                catch { return string.Empty; }
            })
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root.Length)
            .ToList();

        var roots = new List<string>();
        foreach (var candidate in candidates)
        {
            if (roots.Any(root => IsWithinDirectory(candidate, root)))
                continue;
            roots.Add(candidate);
        }

        return roots;
    }

    private static bool IsWithinDirectory(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
