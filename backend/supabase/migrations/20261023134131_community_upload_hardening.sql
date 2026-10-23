-- Home hosting is the only new-upload route. Keep the old private objects
-- readable/removable, but never let old clients bypass validation or backups.
begin;

alter table public.community_clips add column upload_expires_at timestamptz;
update public.community_clips set upload_expires_at = created_at + interval '30 minutes';
alter table public.community_clips alter column upload_expires_at set not null;
alter table public.community_clips alter column upload_expires_at set default (clock_timestamp() + interval '30 minutes');
create index community_home_upload_expiry on public.community_clips(upload_expires_at, id)
    where storage_provider = 'home' and status = 'uploading';

-- These records survive user cleanup/retries and cannot be reset by deleting a
-- clip. A single reservation lock makes both quotas and rolling limits atomic.
create table klipp_private.community_upload_events (
    owner_id uuid not null references auth.users(id) on delete cascade,
    created_at timestamptz not null default clock_timestamp()
);
create index community_upload_events_owner on klipp_private.community_upload_events(owner_id, created_at);
create index community_upload_events_expiry on klipp_private.community_upload_events(created_at);
create table klipp_private.community_upload_expirations (
    clip_id uuid primary key references public.community_clips(id) on delete cascade,
    started_at timestamptz not null default clock_timestamp(),
    last_claimed_at timestamptz not null default clock_timestamp()
);
alter table klipp_private.community_upload_events enable row level security;
alter table klipp_private.community_upload_expirations enable row level security;
revoke all on klipp_private.community_upload_events, klipp_private.community_upload_expirations
    from public, anon, authenticated, service_role;
create policy community_upload_events_private on klipp_private.community_upload_events
    as restrictive for all to anon, authenticated using (false) with check (false);
create policy community_upload_expirations_private on klipp_private.community_upload_expirations
    as restrictive for all to anon, authenticated using (false) with check (false);

create or replace function public.community_begin_upload(p_title text, p_description text, p_duration_seconds double precision, p_byte_size bigint)
returns jsonb language plpgsql security invoker set search_path = '' as $$
begin
    raise exception 'Update Klipp on your PC to upload through the media server.' using errcode = 'KM018';
end;
$$;
create or replace function public.community_publish_clip(p_clip_id uuid)
returns jsonb language plpgsql security invoker set search_path = '' as $$
begin
    raise exception 'Update Klipp on your PC to upload through the media server.' using errcode = 'KM018';
end;
$$;
-- Storage has its own API. This restriction ANDs with all permissive policies,
-- including any added later. Do not modify Storage-managed object metadata.
drop policy community_storage_upload on storage.objects;
create policy community_storage_no_new_cloud_uploads on storage.objects
    as restrictive for insert to anon, authenticated
    with check (bucket_id not in ('community-videos', 'community-posters'));

create or replace function klipp_private.community_storage_access(p_bucket text, p_name text, p_operation text)
returns boolean language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_clip public.community_clips%rowtype;
begin
    if p_bucket not in ('community-videos', 'community-posters') or p_operation = 'insert' then return false; end if;
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then return false; end if;
    select * into v_clip from public.community_clips c where
        (p_bucket = 'community-videos' and c.video_path = p_name)
        or (p_bucket = 'community-posters' and c.poster_path = p_name);
    if v_clip.id is null or v_clip.storage_provider <> 'supabase' then return false; end if;
    if p_operation = 'delete' then return v_clip.owner_id = v_actor and v_clip.status in ('uploading', 'deleting'); end if;
    if p_operation = 'select' then return v_clip.owner_id = v_actor or
        (v_clip.status = 'published' and klipp_private.community_member_id(true) is not null
        and exists(select 1 from public.profiles where id = v_clip.owner_id and username is not null)
        and not klipp_private.community_blocked(v_actor, v_clip.owner_id)); end if;
    return false;
end;
$$;

create or replace function klipp_private.community_begin_home_upload(
    p_title text, p_description text, p_duration_seconds double precision, p_byte_size bigint)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_id uuid; v_title text := btrim(coalesce(p_title, '')); v_now timestamptz;
begin
    if not klipp_private.community_upload_client_allowed() then
        raise exception 'Upload clips from Klipp on your PC.' using errcode = 'KM014';
    end if;
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then raise exception 'A verified permanent account is required.' using errcode = 'KM001'; end if;
    if klipp_private.community_member_id(true) is null then raise exception 'Choose a username first.' using errcode = 'KM002'; end if;
    if char_length(v_title) not between 1 and 120 or char_length(coalesce(p_description, '')) > 500
      or not coalesce(p_duration_seconds > 0 and p_duration_seconds <= 60, false)
      or not coalesce(p_byte_size between 1 and 16777216, false) then
        raise exception 'Invalid clip details.' using errcode = 'KM003';
    end if;
    if not exists(select 1 from klipp_private.community_home_config where singleton and uploads_enabled and gateway_secret_sha256 is not null) then
        raise exception 'Media uploads are temporarily unavailable.' using errcode = 'KM016';
    end if;
    perform pg_advisory_xact_lock(714, 1221);
    v_now := clock_timestamp();
    -- Expired/deleting reservations retain complete slots until the gateway
    -- confirms physical deletion. Releasing them sooner creates orphan files.
    if (select count(*) from public.community_clips where owner_id = v_actor) >= 5 then
        raise exception 'Your five upload slots are in use.' using errcode = 'KM004';
    end if;
    if (select count(*) from public.community_clips) >= 40 then
        raise exception 'The free beta upload capacity is full.' using errcode = 'KM005';
    end if;
    if (select count(*) from public.community_clips where owner_id = v_actor and storage_provider = 'home' and status = 'uploading') >= 2
      or (select count(*) from public.community_clips where storage_provider = 'home' and status = 'uploading') >= 10 then
        raise exception 'Please wait for an upload to finish before starting another.' using errcode = 'KM019';
    end if;
    delete from klipp_private.community_upload_events where created_at < v_now - interval '1 day';
    if (select count(*) from klipp_private.community_upload_events where owner_id = v_actor and created_at >= v_now - interval '1 hour') >= 5
      or (select count(*) from klipp_private.community_upload_events where owner_id = v_actor) >= 20
      or (select count(*) from klipp_private.community_upload_events where created_at >= v_now - interval '1 hour') >= 100
      or (select count(*) from klipp_private.community_upload_events) >= 300 then
        raise exception 'Please wait before uploading again.' using errcode = 'KM008';
    end if;
    insert into public.community_clips(owner_id, title, description, duration_seconds, byte_size, storage_provider, upload_expires_at)
        values(v_actor, v_title, coalesce(p_description, ''), p_duration_seconds, p_byte_size, 'home', v_now + interval '30 minutes')
        returning id into v_id;
    insert into klipp_private.community_upload_events(owner_id, created_at) values(v_actor, v_now);
    return klipp_private.community_clip_json(v_id, v_actor);
end;
$$;

create or replace function klipp_private.community_home_publish(
    p_clip_id uuid, p_video_bytes bigint, p_poster_bytes bigint, p_duration_seconds double precision,
    p_video_sha256 text, p_poster_sha256 text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_clip public.community_clips%rowtype;
begin
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then raise exception 'A verified permanent account is required.' using errcode = 'KM001'; end if;
    if not coalesce(klipp_private.community_home_gateway_allowed(), false) then
        raise exception 'The media server must verify the uploaded files.' using errcode = 'KM015';
    end if;
    select * into v_clip from public.community_clips where id = p_clip_id for update;
    if v_clip.id is null or v_clip.owner_id <> v_actor or v_clip.storage_provider <> 'home' or v_clip.status = 'deleting' then
        raise exception 'Clip unavailable.' using errcode = 'KM007';
    end if;
    if v_clip.status = 'uploading' and v_clip.upload_expires_at <= clock_timestamp() then
        raise exception 'This upload expired. Start a new upload.' using errcode = 'KM017';
    end if;
    if not exists(select 1 from klipp_private.community_home_config where singleton and uploads_enabled) then
        raise exception 'Media uploads are temporarily unavailable.' using errcode = 'KM016';
    end if;
    if klipp_private.community_member_id(true) is null then raise exception 'Choose a username first.' using errcode = 'KM002'; end if;
    if p_video_bytes is distinct from v_clip.byte_size
      or not coalesce(p_poster_bytes between 1 and 262144, false)
      or not coalesce(p_duration_seconds > 0 and p_duration_seconds <= 60 and abs(p_duration_seconds - v_clip.duration_seconds) <= 0.5, false)
      or not coalesce(p_video_sha256 ~ '^[0-9a-f]{64}$' and p_poster_sha256 ~ '^[0-9a-f]{64}$', false) then
        raise exception 'Both validated files must match the reservation.' using errcode = 'KM006';
    end if;
    if v_clip.status = 'published' then
        if v_clip.video_sha256 is distinct from p_video_sha256 or v_clip.poster_sha256 is distinct from p_poster_sha256
          or v_clip.poster_byte_size is distinct from p_poster_bytes then
            raise exception 'Published media cannot be replaced.' using errcode = 'KM010';
        end if;
        return klipp_private.community_clip_json(p_clip_id, v_actor);
    end if;
    update public.community_clips set status = 'published', duration_seconds = p_duration_seconds,
        poster_byte_size = p_poster_bytes, video_sha256 = p_video_sha256, poster_sha256 = p_poster_sha256 where id = p_clip_id;
    return klipp_private.community_clip_json(p_clip_id, v_actor);
end;
$$;

-- A scoped maintenance capability is intentionally separate from member
-- impersonation. It can only expire old pending home uploads, never published
-- clips or ordinary user deletions. No service-role credential is required.
create function klipp_private.community_home_maintenance_allowed()
returns boolean language sql stable security definer set search_path = '' as $$
    select coalesce(jsonb_typeof(h.headers) = 'object'
      and not (h.headers ?| array['origin', 'sec-fetch-mode', 'sec-fetch-site', 'sec-fetch-dest'])
      and c.gateway_secret_sha256 is not null
      and char_length(coalesce(h.headers->>'x-klipp-media-secret', '')) between 32 and 256
      and encode(extensions.digest(h.headers->>'x-klipp-media-secret', 'sha256'), 'hex') = c.gateway_secret_sha256, false)
    from klipp_private.community_home_config c
    cross join lateral (select nullif(current_setting('request.headers', true), '')::jsonb headers) h
    where c.singleton
$$;
revoke all on function klipp_private.community_home_maintenance_allowed() from public, anon, authenticated;

create function klipp_private.community_home_claim_expired_uploads(p_limit integer default 10)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_clip record; v_result jsonb := '[]'::jsonb; v_now timestamptz := clock_timestamp();
begin
    if not coalesce(klipp_private.community_home_maintenance_allowed(), false) then
        raise exception 'The media server must confirm file cleanup.' using errcode = 'KM015';
    end if;
    if not coalesce(p_limit between 1 and 10, false) then
        raise exception 'Invalid cleanup batch size.' using errcode = 'KM003';
    end if;
    for v_clip in
        select c.id, c.owner_id, c.status from public.community_clips c
        left join klipp_private.community_upload_expirations e on e.clip_id = c.id
        where c.storage_provider = 'home'
          and ((c.status = 'uploading' and c.upload_expires_at <= v_now)
            or (c.status = 'deleting' and e.clip_id is not null))
        -- Retry failures fairly: one damaged directory must not keep all later
        -- expired uploads permanently behind the same first ten records.
        order by coalesce(e.last_claimed_at, c.upload_expires_at), c.id
        limit p_limit for update of c skip locked
    loop
        if v_clip.status = 'uploading' then
            update public.community_clips set status = 'deleting' where id = v_clip.id;
            insert into klipp_private.community_upload_expirations(clip_id, started_at, last_claimed_at) values(v_clip.id, v_now, v_now);
        else
            update klipp_private.community_upload_expirations set last_claimed_at = v_now where clip_id = v_clip.id;
        end if;
        v_result := v_result || jsonb_build_array(jsonb_build_object('id', v_clip.id, 'owner_id', v_clip.owner_id));
    end loop;
    return v_result;
end;
$$;

create function klipp_private.community_home_finish_expired_upload(p_clip_id uuid)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_clip public.community_clips%rowtype;
begin
    if not coalesce(klipp_private.community_home_maintenance_allowed(), false) then
        raise exception 'The media server must confirm file cleanup.' using errcode = 'KM015';
    end if;
    select * into v_clip from public.community_clips where id = p_clip_id for update;
    if v_clip.id is null then return jsonb_build_object('id', p_clip_id, 'deleted', true); end if;
    if v_clip.storage_provider <> 'home' or v_clip.status <> 'deleting'
      or not exists(select 1 from klipp_private.community_upload_expirations where clip_id = p_clip_id) then
        raise exception 'This clip was not selected for expired-upload cleanup.' using errcode = 'KM010';
    end if;
    delete from public.community_clips where id = p_clip_id;
    return jsonb_build_object('id', p_clip_id, 'deleted', true);
end;
$$;
create function public.community_home_claim_expired_uploads(p_limit integer default 10)
returns jsonb language sql security invoker set search_path = '' as $$
    select klipp_private.community_home_claim_expired_uploads(p_limit)
$$;
create function public.community_home_finish_expired_upload(p_clip_id uuid)
returns jsonb language sql security invoker set search_path = '' as $$
    select klipp_private.community_home_finish_expired_upload(p_clip_id)
$$;
revoke all on function klipp_private.community_home_claim_expired_uploads(integer),
    klipp_private.community_home_finish_expired_upload(uuid), public.community_home_claim_expired_uploads(integer),
    public.community_home_finish_expired_upload(uuid) from public, anon, authenticated;
-- Private schema remains unexposed. Invoker wrappers require schema USAGE, not
-- access to any table/helper or arbitrary user actions.
grant usage on schema klipp_private to anon;
grant execute on function klipp_private.community_home_claim_expired_uploads(integer),
    klipp_private.community_home_finish_expired_upload(uuid), public.community_home_claim_expired_uploads(integer),
    public.community_home_finish_expired_upload(uuid) to anon, authenticated;


-- Replace the existing dispatcher identity as well: internal/custom RPC callers
-- must not retain a second legacy path through its begin/publish actions.
create or replace function klipp_private.community_dispatch(p_action text, p_args jsonb)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
    v_actor uuid;
    v_id uuid;
    v_other uuid;
    v_clip public.community_clips%rowtype;
    v_comment public.community_comments%rowtype;
    v_limit integer;
    v_before timestamptz;
    v_before_id uuid;
    v_body text;
    v_result jsonb;
    v_now timestamptz := clock_timestamp();
begin
    if p_action in ('begin', 'publish') then
        raise exception 'Update Klipp on your PC to upload through the media server.' using errcode = 'KM018';
    end if;
    -- Cleanup remains possible if a member clears their handle after uploading.
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then
        raise exception 'A verified permanent account is required.' using errcode = 'KM001';
    end if;
    if p_action not in ('remove', 'finish', 'get', 'blocked')
        and not (p_action = 'feed' and coalesce((p_args->>'mine')::boolean, false))
        and not (p_action = 'block' and not coalesce((p_args->>'blocked')::boolean, false))
        and klipp_private.community_member_id(true) is null then
        raise exception 'Choose a username first.' using errcode = 'KM002';
    end if;
    v_id := (p_args->>'id')::uuid;

    if p_action in ('feed', 'comments') then
        v_limit := coalesce((p_args->>'limit')::integer, case when p_action = 'feed' then 13 else 31 end);
        v_before := (p_args->>'before')::timestamptz;
        v_before_id := (p_args->>'before_id')::uuid;
        if v_limit not between 1 and 50 or ((v_before is null) <> (v_before_id is null)) then
            raise exception 'Invalid page cursor.' using errcode = 'KM003';
        end if;
        if p_action = 'feed' then
            select coalesce(jsonb_agg(q.item order by q.created_at desc, q.id desc), '[]'::jsonb) into v_result
            from (select c.id, c.created_at, klipp_private.community_clip_json(c.id, v_actor) item
                from public.community_clips c join public.profiles cp on cp.id = c.owner_id
                where ((coalesce((p_args->>'mine')::boolean, false) and c.owner_id = v_actor)
                    or (not coalesce((p_args->>'mine')::boolean, false) and c.status = 'published' and cp.username is not null))
                  and not klipp_private.community_blocked(v_actor, c.owner_id)
                  and (v_before is null or (c.created_at, c.id) < (v_before, v_before_id))
                order by c.created_at desc, c.id desc limit v_limit) q;
            return v_result;
        end if;
    end if;

    if p_action = 'blocked' then
        select coalesce(jsonb_agg(q.item order by q.display_name, q.user_id), '[]'::jsonb) into v_result
        from (select p.id user_id, p.display_name,
            jsonb_build_object('user_id', p.id, 'username', coalesce(p.username, ''), 'display_name', p.display_name) item
            from public.community_blocks b join public.profiles p on p.id = b.blocked_id
            where b.blocker_id = v_actor order by p.display_name, p.id limit 100) q;
        return v_result;
    end if;
    if p_action in ('remove', 'finish') then
        select * into v_clip from public.community_clips where id = v_id for update;
    elsif p_action not in ('delete_comment', 'block') then
        select * into v_clip from public.community_clips where id = v_id;
    end if;
    if p_action in ('remove', 'finish') then
        if v_clip.id is null then
            return jsonb_build_object('id', v_id, 'status', 'deleted', 'deleted', true);
        end if;
        if v_clip.owner_id <> v_actor then
            raise exception 'Only the owner can remove this clip.' using errcode = 'KM011';
        end if;
        if p_action = 'remove' then
            update public.community_clips set status = 'deleting' where id = v_id;
            return klipp_private.community_clip_json(v_id, v_actor);
        end if;
        if v_clip.status <> 'deleting' then
            raise exception 'Remove the clip before finishing cleanup.' using errcode = 'KM010';
        end if;
        if v_clip.storage_provider = 'home' and not coalesce(klipp_private.community_home_gateway_allowed(), false) then
            raise exception 'The media server must confirm file cleanup.' using errcode = 'KM015';
        end if;
        if exists(select 1 from storage.objects o where
            (o.bucket_id = 'community-videos' and o.name = v_clip.video_path)
            or (o.bucket_id = 'community-posters' and o.name = v_clip.poster_path)) then
            raise exception 'Uploaded files still need cleanup.' using errcode = 'KM010';
        end if;
        delete from public.community_clips where id = v_id;
        return jsonb_build_object('id', v_id, 'deleted', true);
    elsif p_action = 'delete_comment' then
        select * into v_comment from public.community_comments where id = v_id for update;
        if v_comment.id is null then return jsonb_build_object('deleted', true); end if;
        if v_comment.owner_id <> v_actor then
            raise exception 'Only the author can delete a comment.' using errcode = 'KM011';
        end if;
        delete from public.community_comments where id = v_id;
        return jsonb_build_object('deleted', true);

    elsif p_action = 'block' then
        v_other := (p_args->>'user_id')::uuid;
        if v_other is null or v_other = v_actor or not exists(select 1 from public.profiles where id = v_other) then
            raise exception 'Invalid member.' using errcode = 'KM003';
        end if;
        perform pg_advisory_xact_lock(714, 1222);
        if coalesce((p_args->>'blocked')::boolean, false) then
            if not exists(select 1 from public.community_blocks where blocker_id = v_actor and blocked_id = v_other)
                and (select count(*) from public.community_blocks where blocker_id = v_actor) >= 100 then
                raise exception 'Block list limit reached.' using errcode = 'KM009';
            end if;
            insert into public.community_blocks(blocker_id, blocked_id) values(v_actor, v_other) on conflict do nothing;
        else
            delete from public.community_blocks where blocker_id = v_actor and blocked_id = v_other;
        end if;
        return jsonb_build_object('blocked', coalesce((p_args->>'blocked')::boolean, false));
    end if;

    if v_clip.id is null or (v_clip.status <> 'published' and not (p_action = 'get' and v_clip.owner_id = v_actor))
        or klipp_private.community_blocked(v_actor, v_clip.owner_id)
        or (v_clip.owner_id <> v_actor and (klipp_private.community_member_id(true) is null
            or not exists(select 1 from public.profiles where id = v_clip.owner_id and username is not null))) then
        raise exception 'Clip unavailable.' using errcode = 'KM007';
    end if;
    if p_action = 'get' then return klipp_private.community_clip_json(v_id, v_actor);
    elsif p_action = 'like' then
        if p_args->>'liked' is null then raise exception 'Choose a like state.' using errcode = 'KM003'; end if;
        if (p_args->>'liked')::boolean then
            insert into public.community_likes(clip_id, owner_id) values(v_id, v_actor) on conflict do nothing;
        else
            delete from public.community_likes where clip_id = v_id and owner_id = v_actor;
        end if;
        return klipp_private.community_clip_json(v_id, v_actor);
    elsif p_action = 'comments' then
        select coalesce(jsonb_agg(q.item order by q.created_at desc, q.id desc), '[]'::jsonb) into v_result
        from (select m.id, m.created_at, klipp_private.community_comment_json(m.id) item
            from public.community_comments m join public.profiles mp on mp.id = m.owner_id where m.clip_id = v_id and mp.username is not null
              and not klipp_private.community_blocked(v_actor, m.owner_id)
              and (v_before is null or (m.created_at, m.id) < (v_before, v_before_id))
            order by m.created_at desc, m.id desc limit v_limit) q;
        return v_result;
    elsif p_action = 'comment' then
        v_body := btrim(coalesce(p_args->>'body', ''));
        if char_length(v_body) not between 1 and 500 then
            raise exception 'Comments must contain 1–500 characters.' using errcode = 'KM003';
        end if;
        perform pg_advisory_xact_lock(714, 1223);
        delete from klipp_private.community_comment_events where created_at < v_now - interval '1 hour';
        if (select count(*) from klipp_private.community_comment_events where owner_id = v_actor
                and created_at >= v_now - interval '1 minute') >= 5
            or (select count(*) from klipp_private.community_comment_events where owner_id = v_actor) >= 30 then
            raise exception 'Please wait before commenting again.' using errcode = 'KM008';
        end if;
        if (select count(*) from public.community_comments where clip_id = v_id) >= 100 then
            raise exception 'This clip has reached its comment limit.' using errcode = 'KM009';
        end if;
        insert into public.community_comments(clip_id, owner_id, body) values(v_id, v_actor, v_body) returning id into v_other;
        insert into klipp_private.community_comment_events(owner_id) values(v_actor);
        return klipp_private.community_comment_json(v_other);
    elsif p_action = 'report' then
        v_body := btrim(coalesce(p_args->>'reason', ''));
        if char_length(v_body) not between 1 and 500 then
            raise exception 'Reports must contain 1–500 characters.' using errcode = 'KM003';
        end if;
        perform pg_advisory_xact_lock(714, 1224);
        if not exists(select 1 from public.community_reports where clip_id = v_id and reporter_id = v_actor) then
            if (select count(*) from public.community_reports where reporter_id = v_actor
                    and created_at >= v_now - interval '1 day') >= 20 then
                raise exception 'Report limit reached. Please try later.' using errcode = 'KM008';
            end if;
            if (select count(*) from public.community_reports) >= 2000 then
                raise exception 'Report capacity reached.' using errcode = 'KM009';
            end if;
            insert into public.community_reports(clip_id, reporter_id, reason) values(v_id, v_actor, v_body);
        end if;
        return jsonb_build_object('reported', true);
    end if;
    raise exception 'Unknown media action.' using errcode = 'KM003';
end;
$$;

create or replace function klipp_private.community_clip_json(p_id uuid, p_viewer uuid)
returns jsonb language sql stable security definer set search_path = '' as $$
    select jsonb_build_object(
        'id', c.id, 'owner_id', c.owner_id, 'username', p.username, 'display_name', p.display_name,
        'title', c.title, 'description', c.description, 'duration_seconds', c.duration_seconds,
        'byte_size', c.byte_size, 'created_at', c.created_at, 'status', c.status,
        'upload_expires_at', c.upload_expires_at,
        'video_path', c.video_path, 'poster_path', c.poster_path, 'storage_provider', c.storage_provider,
        'like_count', (select count(*) from public.community_likes l where l.clip_id = c.id),
        'comment_count', (select count(*) from public.community_comments m join public.profiles mp on mp.id = m.owner_id
            where m.clip_id = c.id and mp.username is not null and not klipp_private.community_blocked(p_viewer, m.owner_id)),
        'liked_by_me', exists(select 1 from public.community_likes l where l.clip_id = c.id and l.owner_id = p_viewer))
    from public.community_clips c join public.profiles p on p.id = c.owner_id where c.id = p_id
$$;

commit;
