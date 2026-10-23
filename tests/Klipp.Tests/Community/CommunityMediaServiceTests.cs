using System.Net;
using System.Text;
using System.Text.Json;
using Klipp.Core.Community;

namespace Klipp.Tests.Community;

public sealed class CommunityMediaServiceTests
{
    private static readonly Guid Owner = Guid.Parse("161cd682-c82e-4eac-8776-4ecdf5ef13a0");
    private static readonly Guid ClipId = Guid.Parse("261cd682-c82e-4eac-8776-4ecdf5ef13a0");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public async Task Media_requires_a_current_account_and_never_uses_the_public_key_as_authorization()
    {
        using var fixture = new Fixture();
        var signedOut = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync());
        signedOut.Code.ShouldBe("signed_out");
        fixture.Handler.Requests.ShouldBeEmpty();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[]"));
        await fixture.Service.GetMediaFeedAsync();
        var request = fixture.Handler.Requests.Last();
        request.Authorization.ShouldBe("Bearer access-secret");
        request.ApiKey.ShouldBe("sb_publishable_testpublickey123456789");
        request.Body!.ShouldNotContain("access-secret");
        request.Body!.ShouldNotContain("refresh-secret");
    }

    [Fact]
    public async Task Expiring_media_session_rotates_tokens_before_the_rpc_and_never_uses_refresh_tokens_for_storage()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Clock.Value = Now.AddMinutes(59).AddSeconds(30);
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.StartsWith("/auth/v1/token")
            ? Json(JsonSerializer.Serialize(new
            {
                access_token = "rotated-access", refresh_token = "rotated-refresh",
                expires_at = fixture.Clock.Value.AddHours(1).ToUnixTimeSeconds(),
                user = new { id = Owner, email = "player@example.com" }
            })) : Json("[]"));
        await fixture.Service.GetMediaFeedAsync();
        fixture.Handler.Requests[1].Authorization.ShouldBeNull();
        fixture.Handler.Requests[1].Body!.ShouldContain("refresh-secret");
        fixture.Handler.Requests[2].Authorization.ShouldBe("Bearer rotated-access");
        fixture.Handler.Requests[2].Body!.ShouldNotContain("refresh");
        fixture.Store.Session!.RefreshToken.ShouldBe("rotated-refresh");
    }

    [Fact]
    public async Task Cancelling_a_queued_operation_never_sends_it_or_changes_the_signed_in_account()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = (_, _) => { started.TrySetResult(); return reply.Task; };
        var first = fixture.Service.GetMediaFeedAsync();
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var second = fixture.Service.SetClipLikedAsync(ClipId, true, cancellation.Token);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => second);
        reply.SetResult(Json("[]"));
        await first;
        fixture.Handler.Requests.Count.ShouldBe(2);
        fixture.Service.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task Likes_set_the_requested_state_instead_of_toggling_and_use_the_canonical_response()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            body.RootElement.GetProperty("p_clip_id").GetGuid().ShouldBe(ClipId);
            body.RootElement.GetProperty("p_liked").GetBoolean().ShouldBeTrue();
            body.RootElement.EnumerateObject().Count().ShouldBe(2);
            return Task.FromResult(Json(Clip(liked: true)));
        };
        (await fixture.Service.SetClipLikedAsync(ClipId, true)).IsLikedByMe.ShouldBeTrue();
        (await fixture.Service.SetClipLikedAsync(ClipId, true)).LikeCount.ShouldBe(4);
        fixture.Handler.Requests.Skip(1).All(r => r.Path == "/rest/v1/rpc/community_set_like").ShouldBeTrue();
    }

    [Fact]
    public async Task Feed_uses_a_bounded_keyset_and_retains_the_last_visible_row_as_cursor()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[" + string.Join(',',
            Enumerable.Range(0, 13).Select(i => Clip(id: Id(i + 1), created: Now.AddSeconds(-i)))) + "]"));
        var page = await fixture.Service.GetMediaFeedAsync(mine: true);
        page.Clips.Count.ShouldBe(12);
        page.NextCursor.ShouldBe(new CommunityFeedCursor(Now.AddSeconds(-11), Id(12)));
        using (var first = JsonDocument.Parse(fixture.Handler.Requests.Last().Body!))
        {
            first.RootElement.GetProperty("p_limit").GetInt32().ShouldBe(13);
            first.RootElement.GetProperty("p_mine").GetBoolean().ShouldBeTrue();
            first.RootElement.GetProperty("p_before").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[]"));
        (await fixture.Service.GetMediaFeedAsync(page.NextCursor, mine: true)).NextCursor.ShouldBeNull();
        using var second = JsonDocument.Parse(fixture.Handler.Requests.Last().Body!);
        second.RootElement.GetProperty("p_before_id").GetGuid().ShouldBe(Id(12));
        DateTimeOffset.Parse(second.RootElement.GetProperty("p_before").GetString()!).ShouldBe(Now.AddSeconds(-11));
    }

    [Fact]
    public async Task Duplicate_out_of_order_or_extra_feed_rows_are_rejected()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        foreach (var response in new[]
        {
            $"[{Clip()},{Clip()}]",
            $"[{Clip(created: Now.AddSeconds(-1))},{Clip(id: Id(3), created: Now)}]",
            "[" + string.Join(',', Enumerable.Range(0, 14).Select(i => Clip(id: Id(i+1), created: Now.AddSeconds(-i)))) + "]"
        })
        {
            fixture.Handler.Reply = (_, _) => Task.FromResult(Json(response));
            (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync())).Code.ShouldBe("unavailable");
        }
    }

    [Fact]
    public async Task A_mine_feed_cannot_expose_another_accounts_rows()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json($"[{Clip(owner: Id(900))}]"));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync(mine: true));
    }

    [Fact]
    public async Task My_uploads_can_recover_an_own_reservation_even_if_the_profile_handle_was_cleared()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        var own = Clip(status: "deleting").Replace("\"username\":\"player\"", "\"username\":null");
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json($"[{own}]"));
        var clip = (await fixture.Service.GetMediaFeedAsync(mine: true)).Clips.Single();
        clip.ProfileUsername.ShouldBe(string.Empty);
        clip.OwnerId.ShouldBe(Owner);
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_remove_clip")
            ? Json(JsonSerializer.Serialize(new { id = ClipId, owner_id = Owner, status = "deleting", username = (string?)null }))
            : CleanupReply(request));
        await fixture.Service.DeleteClipAsync(ClipId);
        fixture.Handler.Requests.Last().Path.ShouldBe("/rest/v1/rpc/community_finish_delete");
    }

    [Fact]
    public async Task Nullable_handles_on_other_users_public_rows_do_not_bypass_response_validation()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        var other = Clip(owner: Id(900)).Replace("\"username\":\"player\"", "\"username\":null");
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json($"[{other}]"));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync());
    }

    [Fact]
    public async Task PostgreSQL_character_limits_accept_emoji_without_breaking_other_members_feed()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        var name = string.Concat(Enumerable.Repeat("🎮", 40));
        var title = string.Concat(Enumerable.Repeat("🎮", 120));
        using var row = JsonDocument.Parse(Clip(owner: Id(900)));
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.RootElement.GetRawText())!;
        fields["display_name"] = JsonSerializer.SerializeToElement(name);
        fields["title"] = JsonSerializer.SerializeToElement(title);
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[" + JsonSerializer.Serialize(fields) + "]"));
        var clip = (await fixture.Service.GetMediaFeedAsync()).Clips.Single();
        clip.ProfileDisplayName.ShouldBe(name);
        clip.Title.ShouldBe(title);
    }

    [Fact]
    public async Task Comments_have_an_older_page_cursor_and_keep_the_selected_clip_boundary()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[" + string.Join(',', Enumerable.Range(0, 31)
            .Select(i => Comment(Id(i+1), Now.AddSeconds(-i)))) + "]"));
        var page = await fixture.Service.GetClipCommentsPageAsync(ClipId);
        page.Items.Count.ShouldBe(30);
        page.NextCursor!.Id.ShouldBe(Id(30));
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json($"[{Comment(Id(31), Now.AddSeconds(-31), clipId: Id(900))}]"));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetClipCommentsPageAsync(ClipId, page.NextCursor));
    }

    [Fact]
    public async Task Comment_and_report_validation_happens_before_network_and_owner_ids_are_never_supplied()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        var initial = fixture.Handler.Requests.Count;
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.AddClipCommentAsync(ClipId, new string('a', 501)));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.ReportClipAsync(ClipId, " "));
        fixture.Handler.Requests.Count.ShouldBe(initial);
        fixture.Handler.Reply = (request, _) =>
        {
            request.Body!.ShouldNotContain("owner_id");
            using var body = JsonDocument.Parse(request.Body!);
            body.RootElement.GetProperty("p_body").GetString().ShouldBe("Nice clip");
            return Task.FromResult(Json(Comment(Id(7), Now)));
        };
        var comment = await fixture.Service.AddClipCommentAsync(ClipId, " Nice clip ");
        comment.OwnerId.ShouldBe(Owner);
    }

    [Theory]
    [InlineData("KM001", "verified_account_required")]
    [InlineData("KM002", "profile_required")]
    [InlineData("KM004", "upload_limit")]
    [InlineData("KM005", "storage_full")]
    [InlineData("KM006", "upload_not_ready")]
    [InlineData("KM007", "clip_unavailable")]
    [InlineData("KM008", "rate_limited")]
    [InlineData("KM009", "comment_limit")]
    [InlineData("KM010", "cleanup_pending")]
    [InlineData("KM017", "upload_expired")]
    [InlineData("KM018", "legacy_upload_retired")]
    [InlineData("KM019", "uploads_busy")]
    public async Task Server_errors_are_mapped_without_copying_database_details_or_urls(string code, string expected)
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        {
            code, message = "access-secret https://private.example/?token=secret", details = "private database"
        }), HttpStatusCode.BadRequest));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync());
        error.Code.ShouldBe(expected);
        error.Message.ShouldNotContain("secret");
        error.Message.ShouldNotContain("https:");
        error.Message.ShouldNotContain("database");
    }

    [Fact]
    public async Task Report_and_block_limits_have_messages_for_the_actual_action()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("{\"code\":\"KM009\"}", HttpStatusCode.BadRequest));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.ReportClipAsync(ClipId, "Spam"))).Code.ShouldBe("report_limit");
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.BlockUserAsync(Id(900)))).Code.ShouldBe("block_limit");
    }

    [Fact]
    public async Task Block_lists_are_current_user_authorized_bounded_and_allow_cleared_handles_to_be_unblocked()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new[]
            { new { user_id = Id(900), username = (string?)null, display_name = "Player" } })));
        var user = (await fixture.Service.GetBlockedUsersAsync()).Single();
        user.UserId.ShouldBe(Id(900));
        user.Username.ShouldBe(string.Empty);
        fixture.Handler.Requests.Last().Path.ShouldBe("/rest/v1/rpc/community_blocked_members");
        fixture.Handler.Requests.Last().Authorization.ShouldBe("Bearer access-secret");
        fixture.Handler.Reply = (request, _) =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            body.RootElement.GetProperty("p_user_id").GetGuid().ShouldBe(Id(900));
            body.RootElement.GetProperty("p_blocked").GetBoolean().ShouldBeFalse();
            return Task.FromResult(Json("{\"blocked\":false}"));
        };
        await fixture.Service.SetUserBlockedAsync(user.UserId, false);
    }

    [Fact]
    public async Task Block_list_duplicate_self_and_over_limit_responses_are_rejected()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        foreach (var ids in new[] { new[] { Owner }, new[] { Id(900), Id(900) }, Enumerable.Range(1, 101).Select(Id).ToArray() })
        {
            fixture.Handler.Reply = (_, _) => Task.FromResult(Json(JsonSerializer.Serialize(ids.Select(id => new
                { user_id = id, username = "player", display_name = "Player" }))));
            (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetBlockedUsersAsync())).Code.ShouldBe("unavailable");
        }
    }

    [Fact]
    public async Task Responses_are_bounded_before_json_parsing()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json(new string('x', 512 * 1024 + 1)));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync())).Code.ShouldBe("unavailable");
    }

    [Fact]
    public async Task Invalid_cursors_and_non_finite_durations_cannot_start_requests()
    {
        using var fixture = new Fixture();
        using var files = new PreparedFiles();
        await fixture.SignIn();
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync(new(Now, Guid.Empty)));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request with { DurationSeconds = double.NaN }));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request with { DurationSeconds = 60.01 }));
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Signed_playback_uses_RLS_with_the_current_token_and_accepts_only_the_expected_private_object()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_get_clip")
            ? Json(Clip()) : Json(JsonSerializer.Serialize(new { signedURL = $"/object/sign/community-videos/{Owner:D}/{ClipId:D}/video.mp4?token=signed-short-lived" })));
        var uri = await fixture.Service.GetClipPlaybackUriAsync(ClipId);
        uri.Host.ShouldBe("community.example.com");
        uri.AbsolutePath.ShouldBe($"/storage/v1/object/sign/community-videos/{Owner:D}/{ClipId:D}/video.mp4");
        var signing = fixture.Handler.Requests.Last();
        signing.Authorization.ShouldBe("Bearer access-secret");
        using var body = JsonDocument.Parse(signing.Body!);
        body.RootElement.GetProperty("expiresIn").GetInt32().ShouldBe(300);
    }

    [Theory]
    [InlineData("https://evil.example/storage/v1/object/sign/community-videos/x?token=secret")]
    [InlineData("http://community.example.com/storage/v1/object/sign/community-videos/x?token=secret")]
    [InlineData("https://user@community.example.com/storage/v1/object/sign/community-videos/x?token=secret")]
    [InlineData("/object/sign/community-videos/wrong-path/video.mp4?token=secret")]
    [InlineData("//evil.example/object/sign/community-videos/x?token=secret")]
    public async Task Untrusted_signed_urls_are_rejected_without_returning_them(string signedUrl)
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_get_clip")
            ? Json(Clip()) : Json(JsonSerializer.Serialize(new { signedURL = signedUrl })));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetClipPlaybackUriAsync(ClipId));
        error.Message.ShouldNotContain("secret");
        error.Message.ShouldNotContain("evil");
    }

    [Fact]
    public async Task Upload_streams_files_with_progress_without_upsert_then_publishes()
    {
        using var files = new PreparedFiles();
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path switch
        {
            "/rest/v1/rpc/community_begin_upload" => Json(Clip(status: "uploading")),
            "/rest/v1/rpc/community_publish_clip" => Json(Clip()),
            _ => Json("{\"Key\":\"ok\"}")
        });
        var progress = new ProgressRecorder();
        (await fixture.Service.UploadClipAsync(files.Request, progress)).IsPublished.ShouldBeTrue();
        var requests = fixture.Handler.Requests.Skip(1).ToArray();
        requests.Length.ShouldBe(4);
        requests[0].Path.ShouldBe("/rest/v1/rpc/community_begin_upload");
        requests[1].Path.ShouldBe($"/storage/v1/object/community-videos/{Owner:D}/{ClipId:D}/video.mp4");
        requests[2].Path.ShouldBe($"/storage/v1/object/community-posters/{Owner:D}/{ClipId:D}/poster.jpg");
        requests[3].Path.ShouldBe("/rest/v1/rpc/community_publish_clip");
        requests[1].Upsert.ShouldBe("false");
        requests[1].ContentType.ShouldBe("video/mp4");
        requests[1].ByteCount.ShouldBe(files.VideoBytes);
        requests[1].LargestWrite.ShouldBeLessThanOrEqualTo(64 * 1024);
        progress.Values.Last().Status.ShouldBe("Uploaded");
        progress.Values.Last().CompletedBytes.ShouldBe(files.VideoBytes + files.PosterBytes);
        progress.Values.Zip(progress.Values.Skip(1)).All(p => p.First.CompletedBytes <= p.Second.CompletedBytes).ShouldBeTrue();
        requests.All(r => r.Authorization == "Bearer access-secret").ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_cleans_the_reservation_and_partial_objects_with_current_auth()
    {
        using var files = new PreparedFiles();
        using var fixture = new Fixture();
        await fixture.SignIn();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = async (request, token) =>
        {
            if (request.Path.StartsWith("/storage/v1/object/community-videos/") && request.Method == "POST")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return CleanupReply(request);
        };
        using var source = new CancellationTokenSource();
        var upload = fixture.Service.UploadClipAsync(files.Request, cancellationToken: source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => upload);
        var cleanup = fixture.Handler.Requests.Skip(3).ToArray();
        cleanup.Select(r => r.Path).ShouldBe(new[] { "/rest/v1/rpc/community_remove_clip",
            "/storage/v1/object/community-videos", "/storage/v1/object/community-posters", "/rest/v1/rpc/community_finish_delete" });
        cleanup.All(r => r.Authorization == "Bearer access-secret").ShouldBeTrue();
        fixture.Service.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_cleanup_does_not_release_the_reservation_and_my_uploads_can_recover_it()
    {
        using var files = new PreparedFiles();
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path switch
        {
            "/rest/v1/rpc/community_begin_upload" => Json(Clip(status: "uploading")),
            "/rest/v1/rpc/community_remove_clip" => Json(Clip(status: "deleting")),
            _ => Json("private upstream failure", HttpStatusCode.BadGateway)
        });
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request));
        fixture.Handler.Requests.Any(r => r.Path.EndsWith("community_finish_delete")).ShouldBeFalse();
        fixture.Handler.Requests.Count(r => r.Method == "DELETE").ShouldBe(2);
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json($"[{Clip(status: "deleting")}]"));
        (await fixture.Service.GetMediaFeedAsync(mine: true)).Clips.Single().Status.ShouldBe("deleting");
        fixture.Handler.Reply = (request, _) => Task.FromResult(CleanupReply(request));
        await fixture.Service.DeleteClipAsync(ClipId);
        fixture.Handler.Requests.Last().Path.ShouldBe("/rest/v1/rpc/community_finish_delete");
    }

    [Fact]
    public async Task Signout_cancels_an_upload_promptly_and_never_uses_old_auth_for_cleanup()
    {
        using var files = new PreparedFiles();
        using var fixture = new Fixture();
        await fixture.SignIn();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = async (request, token) =>
        {
            if (request.Path.StartsWith("/storage/v1/object/community-videos/") && request.Method == "POST")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return CleanupReply(request);
        };
        var upload = fixture.Service.UploadClipAsync(files.Request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Service.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<OperationCanceledException>(() => upload);
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Handler.Requests.Any(r => r.Path.EndsWith("community_remove_clip")).ShouldBeFalse();
        fixture.Store.Session.ShouldBeNull();
    }

    [Fact]
    public async Task Late_signed_url_response_after_signout_cannot_escape_the_generation_fence()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = async (request, _) =>
        {
            if (request.Path.StartsWith("/storage/v1/object/sign/")) { started.SetResult(); return await reply.Task; }
            return request.Path.EndsWith("community_get_clip") ? Json(Clip()) : Json("{}");
        };
        var signing = fixture.Service.GetClipPlaybackUriAsync(ClipId);
        await started.Task;
        var signout = fixture.Service.SignOutAsync();
        reply.SetResult(Json(JsonSerializer.Serialize(new { signedURL = $"/object/sign/community-videos/{Owner:D}/{ClipId:D}/video.mp4?token=secret" })));
        await Should.ThrowAsync<OperationCanceledException>(() => signing);
        await signout;
    }

    [Fact]
    public async Task Oversized_or_missing_prepared_files_are_rejected_before_a_reservation_is_created()
    {
        using var files = new PreparedFiles();
        using var fixture = new Fixture();
        await fixture.SignIn();
        using (var file = File.OpenWrite(files.Request.VideoPath)) file.SetLength(CommunityMediaLimits.MaximumVideoBytes + 1);
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request))).Code.ShouldBe("file_too_large");
        fixture.Handler.Requests.Count.ShouldBe(1);
        File.Delete(files.Request.VideoPath);
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request))).Code.ShouldBe("invalid_file");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_hides_the_clip_before_objects_and_finishes_only_after_both_storage_deletes()
    {
        using var fixture = new Fixture();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(CleanupReply(request));
        await fixture.Service.DeleteClipAsync(ClipId);
        var operations = fixture.Handler.Requests.Skip(1).ToArray();
        operations[0].Path.ShouldBe("/rest/v1/rpc/community_remove_clip");
        operations[1].Method.ShouldBe("DELETE");
        operations[2].Method.ShouldBe("DELETE");
        operations[3].Path.ShouldBe("/rest/v1/rpc/community_finish_delete");
        using var body = JsonDocument.Parse(operations[1].Body!);
        body.RootElement.GetProperty("prefixes")[0].GetString().ShouldBe($"{Owner:D}/{ClipId:D}/video.mp4");
    }

    [Fact]
    public async Task Home_playback_uses_only_the_publisher_origin_and_never_sends_the_publishable_key()
    {
        using var fixture = new Fixture(home: true);
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_get_clip")
            ? Json(Clip(provider: "home")) : HomeLinks());
        (await fixture.Service.GetClipPlaybackUriAsync(ClipId)).AbsolutePath.ShouldBe($"/v1/assets/{Owner:D}/{ClipId:D}/video");
        (await fixture.Service.GetClipPosterUriAsync(ClipId)).AbsolutePath.ShouldBe($"/v1/assets/{Owner:D}/{ClipId:D}/poster");
        var home = fixture.Handler.Requests.Where(request => request.Host == "media.example.com").ToArray();
        home.Length.ShouldBe(2);
        home.All(request => request.Path == $"/v1/clips/{ClipId:D}/playback" && request.Method == "POST"
            && request.Authorization == "Bearer access-secret" && request.ApiKey is null).ShouldBeTrue();
    }

    [Fact]
    public async Task Home_upload_streams_both_files_to_the_configured_host_before_publishing()
    {
        using var fixture = new Fixture(home: true);
        using var files = new PreparedFiles();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_begin_home_upload")
            ? Json(Clip(status: "uploading", provider: "home")) : request.Path.EndsWith("/publish")
                ? Json(Clip(provider: "home")) : Json("{}"));
        var result = await fixture.Service.UploadClipAsync(files.Request);
        result.StorageProvider.ShouldBe("home");
        var operations = fixture.Handler.Requests.Skip(1).ToArray();
        operations.Select(request => request.Path).ShouldBe(new[] { "/rest/v1/rpc/community_begin_home_upload",
            $"/v1/clips/{ClipId:D}/video", $"/v1/clips/{ClipId:D}/poster", $"/v1/clips/{ClipId:D}/publish" });
        operations[1].Method.ShouldBe("PUT"); operations[1].ContentType.ShouldBe("video/mp4");
        operations[1].ByteCount.ShouldBe(files.VideoBytes); operations[1].ContentLength.ShouldBe(files.VideoBytes);
        operations[2].Method.ShouldBe("PUT"); operations[2].ContentType.ShouldBe("image/jpeg");
        operations[2].ByteCount.ShouldBe(files.PosterBytes); operations[2].ContentLength.ShouldBe(files.PosterBytes);
        operations.Skip(1).All(request => request.Host == "media.example.com" && request.ApiKey is null
            && request.Authorization == "Bearer access-secret").ShouldBeTrue();
        operations[1].LargestWrite.ShouldBeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task Home_upload_failures_clean_the_reserved_clip_without_silently_switching_storage()
    {
        using var fixture = new Fixture(home: true);
        using var files = new PreparedFiles();
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path switch
        {
            "/rest/v1/rpc/community_begin_home_upload" => Json(Clip(status: "uploading", provider: "home")),
            "/rest/v1/rpc/community_remove_clip" => Json(Clip(status: "deleting", provider: "home")),
            _ when request.Method == "DELETE" => Json(JsonSerializer.Serialize(new { id = ClipId, deleted = true })),
            _ => Json("{}", HttpStatusCode.ServiceUnavailable)
        });
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request))).Code.ShouldBe("offline");
        fixture.Handler.Requests.Any(request => request.Path.StartsWith("/storage/") || request.Path.EndsWith("/publish")).ShouldBeFalse();
        fixture.Handler.Requests.Last().Method.ShouldBe("DELETE");
        fixture.Handler.Requests.Last().Path.ShouldBe($"/v1/clips/{ClipId:D}");
    }

    [Theory]
    [InlineData("uploads_paused")]
    [InlineData("backup_required")]
    public async Task Paused_home_uploads_show_a_clear_message_without_exposing_provider_details(string code)
    {
        using var fixture = new Fixture(home: true); using var files = new PreparedFiles(); await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path switch
        {
            "/rest/v1/rpc/community_begin_home_upload" => Json(Clip(status: "uploading", provider: "home")),
            "/rest/v1/rpc/community_remove_clip" => Json(Clip(status: "deleting", provider: "home")),
            _ when request.Method == "DELETE" => Json(JsonSerializer.Serialize(new { id = ClipId, deleted = true })),
            _ => Json(JsonSerializer.Serialize(new { code, message = "private provider detail" }), HttpStatusCode.ServiceUnavailable)
        });
        var failure = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.UploadClipAsync(files.Request));
        failure.Code.ShouldBe("hosting_unavailable"); failure.Message.ShouldContain("temporarily paused");
        failure.Message.ShouldNotContain("private provider");
    }

    [Fact]
    public async Task Home_deletion_keeps_the_clip_hidden_until_server_cleanup_acknowledges_the_same_id()
    {
        using var fixture = new Fixture(home: true);
        await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_remove_clip")
            ? Json(Clip(status: "deleting", provider: "home")) : Json(JsonSerializer.Serialize(new { id = ClipId, deleted = true })));
        await fixture.Service.DeleteClipAsync(ClipId);
        fixture.Handler.Requests.Skip(1).Select(request => request.Path).ShouldBe(new[] {
            "/rest/v1/rpc/community_remove_clip", $"/v1/clips/{ClipId:D}" });
        fixture.Handler.Requests.Last().ApiKey.ShouldBeNull();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_remove_clip")
            ? Json(Clip(status: "deleting", provider: "home")) : Json(JsonSerializer.Serialize(new { id = Id(999), deleted = true })));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.DeleteClipAsync(ClipId));
    }

    [Fact]
    public async Task Home_assets_fail_closed_without_public_configuration_and_for_unknown_providers()
    {
        using var fixture = new Fixture(); await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json(Clip(provider: "home")));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetClipPlaybackUriAsync(ClipId))).Code.ShouldBe("media_not_configured");
        fixture.Handler.Requests.Count.ShouldBe(2);
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[" + Clip(provider: "untrusted") + "]"));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync())).Code.ShouldBe("unavailable");
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://media.example.com")]
    [InlineData("https://player@media.example.com")]
    public async Task Home_signed_links_cannot_redirect_to_an_untrusted_origin(string origin)
    {
        using var fixture = new Fixture(home: true); await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_get_clip")
            ? Json(Clip(provider: "home")) : HomeLinks(origin));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetClipPlaybackUriAsync(ClipId))).Code.ShouldBe("unavailable");
    }

    [Theory]
    [InlineData("&extra=1")]
    [InlineData("&signature=duplicate")]
    public async Task Home_signed_links_reject_ambiguous_or_unexpected_query_fields(string suffix)
    {
        using var fixture = new Fixture(home: true); await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => Task.FromResult(request.Path.EndsWith("community_get_clip")
            ? Json(Clip(provider: "home")) : HomeLinks(suffix: suffix));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetClipPlaybackUriAsync(ClipId))).Code.ShouldBe("unavailable");
    }

    [Fact]
    public async Task A_late_home_link_cannot_survive_signout()
    {
        using var fixture = new Fixture(home: true); await fixture.SignIn();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Reply = (request, _) =>
        {
            if (request.Path.EndsWith("community_get_clip")) return Task.FromResult(Json(Clip(provider: "home")));
            if (request.Path.EndsWith("/playback")) { started.SetResult(); return reply.Task; }
            return Task.FromResult(Json("{}"));
        };
        var pending = fixture.Service.GetClipPlaybackUriAsync(ClipId); await started.Task;
        var signout = fixture.Service.SignOutAsync(); reply.SetResult(HomeLinks());
        await Should.ThrowAsync<OperationCanceledException>(() => pending); await signout;
        fixture.Service.IsSignedIn.ShouldBeFalse();
    }

    private static HttpResponseMessage HomeLinks(string origin = "https://media.example.com", string suffix = "") => Json(JsonSerializer.Serialize(new {
        video_url = $"{origin}/v1/assets/{Owner:D}/{ClipId:D}/video?expires={Now.AddMinutes(5).ToUnixTimeSeconds()}&signature={new string('a',64)}{suffix}",
        poster_url = $"{origin}/v1/assets/{Owner:D}/{ClipId:D}/poster?expires={Now.AddMinutes(2).ToUnixTimeSeconds()}&signature={new string('b',64)}{suffix}",
        expires_at = Now.AddMinutes(2)
    }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Hidden_clips_never_request_cloud_or_home_capabilities_even_for_the_owner(bool home, bool poster)
    {
        using var fixture = new Fixture(home); await fixture.SignIn();
        fixture.Handler.Reply = (request, _) => request.Path.EndsWith("community_get_clip", StringComparison.Ordinal)
            ? Task.FromResult(Json(Clip(provider: home ? "home" : "supabase", hidden: true)))
            : throw new InvalidOperationException("Hidden media must never request a capability");
        var error = await Should.ThrowAsync<CommunityAccountException>(() => poster
            ? fixture.Service.GetClipPosterUriAsync(ClipId) : fixture.Service.GetClipPlaybackUriAsync(ClipId));
        error.Code.ShouldBe("clip_unavailable"); fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Hidden_owner_upload_is_listed_for_cleanup_but_not_playable_or_exposed_in_public_feed()
    {
        using var fixture = new Fixture(home: true); await fixture.SignIn();
        fixture.Handler.Reply = (_, _) => Task.FromResult(Json("[" + Clip(provider: "home", hidden: true) + "]"));
        var own = (await fixture.Service.GetMediaFeedAsync(mine: true)).Clips.Single();
        own.ModerationHidden.ShouldBeTrue(); own.CanPlay.ShouldBeFalse(); own.IsPublished.ShouldBeTrue();
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetMediaFeedAsync());
    }

    private static HttpResponseMessage CleanupReply(Request request) => request.Path switch
    {
        "/rest/v1/rpc/community_begin_upload" => Json(Clip(status: "uploading")),
        "/rest/v1/rpc/community_remove_clip" => Json(Clip(status: "deleting")),
        "/rest/v1/rpc/community_finish_delete" => Json("{\"deleted\":true}"),
        _ => Json("[]")
    };
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static string Clip(Guid? id = null, Guid? owner = null, DateTimeOffset? created = null,
        bool liked = false, string status = "published", string? provider = null, bool hidden = false) => JsonSerializer.Serialize(new
        {
            id = id ?? ClipId, owner_id = owner ?? Owner, username = "player", display_name = "Player",
            title = "A clip", description = "Description", duration_seconds = 10d, byte_size = 192 * 1024,
            created_at = created ?? Now, like_count = 4, comment_count = 1, liked_by_me = liked, status, storage_provider = provider, moderation_hidden = hidden,
            video_path = $"{owner ?? Owner:D}/{id ?? ClipId:D}/video.mp4", poster_path = $"{owner ?? Owner:D}/{id ?? ClipId:D}/poster.jpg"
        });
    private static string Comment(Guid id, DateTimeOffset created, Guid? clipId = null) => JsonSerializer.Serialize(new
    {
        id, clip_id = clipId ?? ClipId, owner_id = Owner, username = "player", display_name = "Player", body = "Nice clip", created_at = created
    });
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class Fixture : IDisposable
    {
        public Handler Handler { get; } = new();
        public Store Store { get; } = new();
        public Clock Clock { get; } = new();
        public CommunityAccountService Service { get; }
        private readonly HttpClient _http;
        public Fixture(bool home = false)
        {
            _http = new(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Service = new(new("https://community.example.com", "sb_publishable_testpublickey123456789",
                home ? "https://media.example.com" : ""), _http, Store, Clock);
        }
        public Task SignIn()
        {
            Handler.Reply = (_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                access_token = "access-secret", refresh_token = "refresh-secret", expires_at = Now.AddHours(1).ToUnixTimeSeconds(),
                user = new { id = Owner, email = "player@example.com" }
            })));
            return Service.VerifyEmailCodeAsync("player@example.com", "123456");
        }
        public void Dispose() { Service.Dispose(); _http.Dispose(); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Value;
    }
    private sealed class Store : ICommunitySessionStore
    {
        public CommunitySession? Session { get; private set; }
        public Task<CommunitySession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);
        public Task SaveAsync(CommunitySession session, CancellationToken cancellationToken = default) { Session = session; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default) { Session = null; return Task.CompletedTask; }
    }
    private sealed class PreparedFiles : IDisposable
    {
        public CommunityUploadRequest Request { get; }
        public int VideoBytes => 192 * 1024;
        public int PosterBytes => 2048;
        public PreparedFiles()
        {
            var name = Path.Combine(Path.GetTempPath(), "klipp-media-test-" + Guid.NewGuid().ToString("N"));
            Request = new(name + ".mp4", name + ".jpg", "A clip", "Description", 10);
            using (var video = File.Create(Request.VideoPath)) video.SetLength(VideoBytes);
            using (var poster = File.Create(Request.PosterPath)) poster.SetLength(PosterBytes);
        }
        public void Dispose() { File.Delete(Request.VideoPath); File.Delete(Request.PosterPath); }
    }
    private sealed class ProgressRecorder : IProgress<CommunityUploadProgress>
    {
        public List<CommunityUploadProgress> Values { get; } = [];
        public void Report(CommunityUploadProgress value) => Values.Add(value);
    }
    private sealed record Request(string Method, string Path, string? Authorization, string? ApiKey, string? Body,
        string? Upsert, string? ContentType, long ByteCount, int LargestWrite, string Host, long? ContentLength);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public Func<Request, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = (_, _) => throw new InvalidOperationException("No test reply");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            string? body = null;
            var count = new CountingStream();
            if (message.Content is not null)
            {
                if (message.Content.Headers.ContentType?.MediaType == "application/json")
                    body = await message.Content.ReadAsStringAsync(cancellationToken);
                else await message.Content.CopyToAsync(count, cancellationToken);
            }
            var request = new Request(message.Method.Method, message.RequestUri!.PathAndQuery,
                message.Headers.Authorization?.ToString(), message.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null,
                body, message.Headers.TryGetValues("x-upsert", out var upsert) ? upsert.Single() : null,
                message.Content?.Headers.ContentType?.MediaType, count.Bytes, count.LargestWrite,
                message.RequestUri.Host, message.Content?.Headers.ContentLength);
            Requests.Add(request);
            return await Reply(request, cancellationToken);
        }
    }
    private sealed class CountingStream : Stream
    {
        public long Bytes { get; private set; }
        public int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { Bytes += count; LargestWrite = Math.Max(count, LargestWrite); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write([], 0, buffer.Length); return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
