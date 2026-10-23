using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Klipp.Core.Community;
using Klipp.Desktop.Services;

/// <summary>
/// Explicit, operator-controlled live check. OTPs arrive only through a Windows-current-user pipe;
/// they are never command-line arguments, console output, or plaintext files.
/// </summary>
internal static class LiveCommunityChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        var stage = "configuration";
        CommunityAccountService? accounts = null;
        HttpClient? http = null;
        var wroteSession = false;
        var completed = false;
        var profilePatched = false;
        try
        {
            if (args.Contains("--help", StringComparer.Ordinal))
            {
                Console.WriteLine("Live check: --live --config <public-config-path> --email <owner-controlled-address> --pipe Klipp.CommunityHarness.<random> --keep-session [--username <chosen-username>] [--session-directory <Windows-credential-folder>]");
                Console.WriteLine("Sends two requested account emails and retains a current-user DPAPI session after successful verification. Codes enter only through the named pipe.");
                return 0;
            }
            if (!OperatingSystem.IsWindows()) throw Failure("windows_required");
            var options = ParseOptions(args);
            var configuration = CommunityConfigurationService.Load(options.ConfigPath);
            if (!configuration.IsConfigured) throw Failure("invalid_public_configuration");
            var email = CommunityProfileValidation.NormalizeEmail(options.Email);
            var store = new CommunitySessionStore(configuration, options.SessionDirectory);
            // Never overwrite a different person's saved Windows account as part of a live check.
            var existing = await store.LoadAsync();
            if (existing?.User is { } previous && !string.Equals(previous.Email, email, StringComparison.OrdinalIgnoreCase))
                throw Failure("existing_session_owner_mismatch");

            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
            accounts = new CommunityAccountService(configuration, http, store);
            Console.WriteLine("LIVE_START uses_real_backend=true uses_real_dpapi=true account_details_logged=false");

            stage = "signup-send";
            await RequireCommandAsync(options.PipeName, stage, "send");
            await accounts.SendEmailCodeAsync(email, createAccount: true);
            Console.WriteLine("LIVE_SENT stage=signup");

            stage = "signup-code";
            var signupCode = await ReceiveAsync(options.PipeName, stage, "code");
            await accounts.VerifyEmailCodeAsync(email, signupCode);
            signupCode = string.Empty;
            wroteSession = true;
            var identity = accounts.User?.Id ?? throw Failure("missing_verified_identity");
            Console.WriteLine("LIVE_VERIFIED stage=signup authenticated=true");

            stage = "profile";
            var profile = await accounts.GetProfileAsync() ?? throw Failure("missing_profile");
            if (profile.Id != identity) throw Failure("profile_owner_mismatch");
            Console.WriteLine("LIVE_PROFILE loaded=true complete=" + profile.IsComplete.ToString().ToLowerInvariant());
            var requestedUsername = options.Username;
            var skipPatch = false;
            if (requestedUsername is null)
            {
                stage = "profile-update";
                var command = await ReceiveAsync(options.PipeName, stage, "username:<chosen-value>_or_keep_or_skip");
                skipPatch = command == "skip";
                if (command is not "skip" and not "keep")
                {
                    if (!command.StartsWith("username:", StringComparison.Ordinal)) throw Failure("invalid_operator_command");
                    requestedUsername = command["username:".Length..];
                }
            }
            requestedUsername ??= profile.Username;
            if (!skipPatch && !string.IsNullOrWhiteSpace(requestedUsername) && profile.Bio == profile.Bio.Trim())
            {
                var saved = await accounts.SaveProfileAsync(requestedUsername, profile.DisplayName, profile.Bio);
                if (saved.Id != profile.Id || saved.DisplayName != profile.DisplayName || saved.Bio != profile.Bio)
                    throw Failure("profile_changed_unexpectedly");
                profilePatched = true;
                Console.WriteLine("LIVE_PROFILE_PATCH passed=true existing_name_and_bio_preserved=true");
            }
            else
            {
                // A new member must choose their own username; the harness never invents one.
                Console.WriteLine("LIVE_PROFILE_PATCH skipped=true reason=" + (skipPatch ? "operator_skipped" : profile.IsComplete ? "preserve_bio_whitespace" : "username_not_chosen"));
            }

            stage = "signout";
            await RequireCommandAsync(options.PipeName, stage, "continue");
            await accounts.SignOutAsync();
            if (accounts.IsSignedIn || await store.LoadAsync() is not null) throw Failure("local_signout_failed");
            wroteSession = false;
            Console.WriteLine("LIVE_SIGNED_OUT authenticated=false credential_removed=true");

            stage = "signin-send";
            await RequireCommandAsync(options.PipeName, stage, "send");
            await accounts.SendEmailCodeAsync(email, createAccount: false);
            Console.WriteLine("LIVE_SENT stage=signin");

            stage = "signin-code";
            var signinCode = await ReceiveAsync(options.PipeName, stage, "code");
            await accounts.VerifyEmailCodeAsync(email, signinCode);
            signinCode = string.Empty;
            wroteSession = true;
            if (accounts.User?.Id != identity) throw Failure("signin_identity_mismatch");
            var signedInProfile = await accounts.GetProfileAsync();
            if (signedInProfile?.Id != identity) throw Failure("signin_profile_owner_mismatch");
            Console.WriteLine("LIVE_VERIFIED stage=signin same_account=true profile_loaded=true");

            stage = "restore";
            accounts.Dispose();
            accounts = new CommunityAccountService(configuration, http, new CommunitySessionStore(configuration, options.SessionDirectory));
            await accounts.RestoreSessionAsync();
            var restoredProfile = await accounts.GetProfileAsync();
            if (accounts.User?.Id != identity || restoredProfile?.Id != identity) throw Failure("restored_identity_mismatch");
            completed = true;
            Console.WriteLine("LIVE_COMPLETE signup_verified=true signin_verified=true profile_patch_verified=" + profilePatched.ToString().ToLowerInvariant() + " session_restored=true protected_session_retained=true");
            return 0;
        }
        catch (CommunityAccountException exception)
        {
            // These codes are generated by the client, not raw provider messages.
            Console.WriteLine("LIVE_FAIL stage=" + stage + " code=" + exception.Code);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("LIVE_FAIL stage=" + stage + " code=operator_input_timeout");
            return 1;
        }
        catch (Exception)
        {
            Console.WriteLine("LIVE_FAIL stage=" + stage + " code=local_check_failed");
            return 1;
        }
        finally
        {
            if (!completed && wroteSession && accounts is not null)
            {
                try
                {
                    await accounts.SignOutAsync();
                    Console.WriteLine("LIVE_CLEANUP protected_session_removed=true");
                }
                catch { Console.WriteLine("LIVE_CLEANUP protected_session_removed=false retry_signout_required=true"); }
            }
            accounts?.Dispose();
            http?.Dispose();
        }
    }

    private static Options ParseOptions(string[] args)
    {
        string? config = null, email = null, pipe = null, directory = null, username = null;
        var keep = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--live": break;
                case "--keep-session": keep = true; break;
                case "--config": config = Next(); break;
                case "--email": email = Next(); break;
                case "--pipe": pipe = Next(); break;
                case "--session-directory": directory = Next(); break;
                case "--username": username = Next(); break;
                default: throw Failure("invalid_live_arguments");
            }
            string Next() => ++index < args.Length ? args[index] : throw Failure("missing_live_argument");
        }
        if (!keep || string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(email)
            || pipe is null || !Regex.IsMatch(pipe, "^Klipp\\.CommunityHarness\\.[A-Za-z0-9._-]{12,100}$", RegexOptions.CultureInvariant))
            throw Failure("invalid_live_arguments");
        return new(Path.GetFullPath(config), email, pipe, directory is null ? null : Path.GetFullPath(directory), username);
    }

    private static async Task RequireCommandAsync(string pipeName, string stage, string command)
    {
        if (await ReceiveAsync(pipeName, stage, command) != command) throw Failure("invalid_operator_command");
    }

    private static async Task<string> ReceiveAsync(string pipeName, string stage, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Console.WriteLine("LIVE_READY stage=" + stage + " pipe=" + pipeName + " expects=" + expected);
        await Console.Out.FlushAsync();
        await pipe.WaitForConnectionAsync(timeout.Token);
        var bytes = new byte[64];
        try
        {
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await pipe.ReadAsync(bytes.AsMemory(count, 1), timeout.Token);
                if (read == 0) throw Failure("incomplete_operator_input");
                if (bytes[count] == (byte)'\n')
                {
                    var input = Encoding.ASCII.GetString(bytes, 0, count).TrimEnd('\r');
                    if (expected == "code" && (input.Length is < 6 or > 10 || input.Any(c => c is < '0' or > '9')))
                        throw Failure("invalid_operator_code_format");
                    return input;
                }
                count++;
            }
            throw Failure("operator_input_too_long");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static CommunityAccountException Failure(string code) => new(code, "The live account check could not continue.");
    private sealed record Options(string ConfigPath, string Email, string PipeName, string? SessionDirectory, string? Username);
}
