-- These tables are deliberately RPC-only. Explicit restrictive deny policies
-- document the access model and retain denial if a permissive policy is added.
-- Existing private SECURITY DEFINER implementations and service-role moderation
-- retain their owner/BYPASSRLS access; no client table grants are introduced.
begin;

create policy community_clips_rpc_only on public.community_clips
    as restrictive for all to anon, authenticated using (false) with check (false);
create policy community_likes_rpc_only on public.community_likes
    as restrictive for all to anon, authenticated using (false) with check (false);
create policy community_comments_rpc_only on public.community_comments
    as restrictive for all to anon, authenticated using (false) with check (false);
create policy community_blocks_rpc_only on public.community_blocks
    as restrictive for all to anon, authenticated using (false) with check (false);
create policy community_reports_rpc_only on public.community_reports
    as restrictive for all to anon, authenticated using (false) with check (false);
create policy community_comment_events_rpc_only on klipp_private.community_comment_events
    as restrictive for all to anon, authenticated using (false) with check (false);

commit;
