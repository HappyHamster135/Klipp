-- Free beta: 40 reserved slots, at most 5 per member. A slot always reserves
-- 16 MiB of video + 256 KiB of poster capacity, including failed/deleting uploads.
-- Create the two PRIVATE buckets through the Storage API before enabling uploads:
-- community-videos: 16777216 bytes, video/mp4; community-posters: 262144 bytes, image/jpeg.
-- Storage-managed tables are read here, never inserted/updated/deleted through SQL.
begin;

create table public.community_clips (
    id uuid primary key default gen_random_uuid(),
    -- Restrict account deletion until physical Storage cleanup has completed.
    owner_id uuid not null references auth.users(id) on delete restrict,
    title text not null check (char_length(title) between 1 and 120 and title = btrim(title)),
    description text not null default '' check (char_length(description) <= 500),
    duration_seconds double precision not null check (duration_seconds > 0 and duration_seconds <= 60),
    byte_size bigint not null check (byte_size between 1 and 16777216),
    status text not null default 'uploading' check (status in ('uploading', 'published', 'deleting')),
    created_at timestamptz not null default clock_timestamp(),
    video_path text generated always as (owner_id::text || '/' || id::text || '/video.mp4') stored,
    poster_path text generated always as (owner_id::text || '/' || id::text || '/poster.jpg') stored
);
create index community_clips_feed on public.community_clips(status, created_at desc, id desc);
create index community_clips_owner on public.community_clips(owner_id, created_at desc, id desc);

create table public.community_likes (
    clip_id uuid not null references public.community_clips(id) on delete cascade,
    owner_id uuid not null references auth.users(id) on delete cascade,
    created_at timestamptz not null default clock_timestamp(),
    primary key (clip_id, owner_id)
);
create index community_likes_owner on public.community_likes(owner_id, clip_id);
create table public.community_comments (
    id uuid primary key default gen_random_uuid(),
    clip_id uuid not null references public.community_clips(id) on delete cascade,
    owner_id uuid not null references auth.users(id) on delete cascade,
    body text not null check (char_length(body) between 1 and 500 and body = btrim(body)),
    created_at timestamptz not null default clock_timestamp()
);
create index community_comments_page on public.community_comments(clip_id, created_at desc, id desc);
create index community_comments_owner on public.community_comments(owner_id);
create table public.community_blocks (
    blocker_id uuid not null references auth.users(id) on delete cascade,
    blocked_id uuid not null references auth.users(id) on delete cascade,
    created_at timestamptz not null default clock_timestamp(),
    primary key (blocker_id, blocked_id),
    check (blocker_id <> blocked_id)
);
create index community_blocks_reverse on public.community_blocks(blocked_id, blocker_id);
create table public.community_reports (
    clip_id uuid not null references public.community_clips(id) on delete cascade,
    reporter_id uuid not null references auth.users(id) on delete cascade,
    reason text not null check (char_length(reason) between 1 and 500),
    status text not null default 'open' check (status in ('open', 'resolved')),
    created_at timestamptz not null default clock_timestamp(),
    primary key (clip_id, reporter_id)
);
create index community_reports_reporter on public.community_reports(reporter_id, created_at);
-- Separate events prevent deleting comments from bypassing the rolling rate limit.
create table klipp_private.community_comment_events (
    owner_id uuid not null references auth.users(id) on delete cascade,
    created_at timestamptz not null default clock_timestamp()
);
create index community_comment_events_owner on klipp_private.community_comment_events(owner_id, created_at);
create index community_comment_events_expiry on klipp_private.community_comment_events(created_at);

alter table public.community_clips enable row level security;
alter table public.community_likes enable row level security;
alter table public.community_comments enable row level security;
alter table public.community_blocks enable row level security;
alter table public.community_reports enable row level security;
alter table klipp_private.community_comment_events enable row level security;
-- No direct client table access: all reads and mutations use the checked RPCs.
revoke all on public.community_clips, public.community_likes, public.community_comments,
    public.community_blocks, public.community_reports, klipp_private.community_comment_events
    from public, anon, authenticated;
grant select, insert, update, delete on public.community_clips, public.community_likes,
    public.community_comments, public.community_blocks, public.community_reports to service_role;
grant usage on schema klipp_private to authenticated;

create function klipp_private.community_member_id(p_require_profile boolean)
returns uuid language sql stable security definer set search_path = '' as $$
    select u.id from auth.users u
    join public.profiles p on p.id = u.id
    where u.id = auth.uid() and not coalesce(u.is_anonymous, false)
      and u.email_confirmed_at is not null and (u.banned_until is null or u.banned_until <= now())
      and (not p_require_profile or p.username is not null)
$$;
create function klipp_private.community_blocked(p_a uuid, p_b uuid)
returns boolean language sql stable security definer set search_path = '' as $$
    select exists (select 1 from public.community_blocks b
        where (b.blocker_id = p_a and b.blocked_id = p_b)
           or (b.blocker_id = p_b and b.blocked_id = p_a))
$$;
create function klipp_private.community_clip_json(p_id uuid, p_viewer uuid)
returns jsonb language sql stable security definer set search_path = '' as $$
    select jsonb_build_object(
        'id', c.id, 'owner_id', c.owner_id, 'username', p.username, 'display_name', p.display_name,
        'title', c.title, 'description', c.description, 'duration_seconds', c.duration_seconds,
        'byte_size', c.byte_size, 'created_at', c.created_at, 'status', c.status,
        'video_path', c.video_path, 'poster_path', c.poster_path,
        'like_count', (select count(*) from public.community_likes l where l.clip_id = c.id),
        'comment_count', (select count(*) from public.community_comments m join public.profiles mp on mp.id = m.owner_id
            where m.clip_id = c.id and mp.username is not null and not klipp_private.community_blocked(p_viewer, m.owner_id)),
        'liked_by_me', exists(select 1 from public.community_likes l where l.clip_id = c.id and l.owner_id = p_viewer))
    from public.community_clips c join public.profiles p on p.id = c.owner_id where c.id = p_id
$$;
create function klipp_private.community_comment_json(p_id uuid)
returns jsonb language sql stable security definer set search_path = '' as $$
    select jsonb_build_object('id', m.id, 'clip_id', m.clip_id, 'owner_id', m.owner_id,
        'username', p.username, 'display_name', p.display_name, 'body', m.body, 'created_at', m.created_at)
    from public.community_comments m join public.profiles p on p.id = m.owner_id where m.id = p_id
$$;
create function klipp_private.community_buckets_ready()
returns boolean language sql stable security definer set search_path = '' as $$
    select count(*) = 2 from storage.buckets b where not b.public and (
        (b.id = 'community-videos' and b.file_size_limit = 16777216
            and b.allowed_mime_types = array['video/mp4']::text[])
        or (b.id = 'community-posters' and b.file_size_limit = 262144
            and b.allowed_mime_types = array['image/jpeg']::text[]))
$$;

-- This implementation is not in an exposed schema. Every action revalidates
-- auth.uid against the current Auth row; user-editable JWT metadata is unused.
create function klipp_private.community_dispatch(p_action text, p_args jsonb)
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
    v_video_size bigint;
    v_poster_size bigint;
begin
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

    if p_action = 'begin' then
        v_body := btrim(coalesce(p_args->>'title', ''));
        if char_length(v_body) not between 1 and 120
            or char_length(coalesce(p_args->>'description', '')) > 500
            or not coalesce((p_args->>'duration')::double precision > 0
                and (p_args->>'duration')::double precision <= 60, false)
            or not coalesce((p_args->>'bytes')::bigint between 1 and 16777216, false) then
            raise exception 'Invalid clip details.' using errcode = 'KM003';
        end if;
        if not klipp_private.community_buckets_ready() then
            raise exception 'Upload storage is not configured.' using errcode = 'KM006';
        end if;
        -- Serialize the small, fixed-capacity beta reservation counter.
        perform pg_advisory_xact_lock(714, 1221);
        if (select count(*) from public.community_clips where owner_id = v_actor) >= 5 then
            raise exception 'Your five upload slots are in use.' using errcode = 'KM004';
        end if;
        if (select count(*) from public.community_clips) >= 40 then
            raise exception 'The free beta upload capacity is full.' using errcode = 'KM005';
        end if;
        insert into public.community_clips(owner_id, title, description, duration_seconds, byte_size)
        values (v_actor, v_body, coalesce(p_args->>'description', ''),
            (p_args->>'duration')::double precision, (p_args->>'bytes')::bigint)
        returning id into v_id;
        return klipp_private.community_clip_json(v_id, v_actor);

    elsif p_action in ('feed', 'comments') then
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
    if p_action in ('publish', 'remove', 'finish') then
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
        if exists(select 1 from storage.objects o where
            (o.bucket_id = 'community-videos' and o.name = v_clip.video_path)
            or (o.bucket_id = 'community-posters' and o.name = v_clip.poster_path)) then
            raise exception 'Uploaded files still need cleanup.' using errcode = 'KM010';
        end if;
        delete from public.community_clips where id = v_id;
        return jsonb_build_object('id', v_id, 'deleted', true);
    elsif p_action = 'publish' then
        if v_clip.id is null or v_clip.owner_id <> v_actor then
            raise exception 'Clip unavailable.' using errcode = 'KM007';
        end if;
        if v_clip.status = 'published' then return klipp_private.community_clip_json(v_id, v_actor); end if;
        if v_clip.status <> 'uploading' then
            raise exception 'Clip is being deleted.' using errcode = 'KM007';
        end if;
        select case when o.metadata->>'size' ~ '^[0-9]{1,12}$' then (o.metadata->>'size')::bigint end
            into v_video_size from storage.objects o
            where o.bucket_id = 'community-videos' and o.name = v_clip.video_path
              and o.owner_id = v_actor::text and o.metadata->>'mimetype' = 'video/mp4';
        select case when o.metadata->>'size' ~ '^[0-9]{1,12}$' then (o.metadata->>'size')::bigint end
            into v_poster_size from storage.objects o
            where o.bucket_id = 'community-posters' and o.name = v_clip.poster_path
              and o.owner_id = v_actor::text and o.metadata->>'mimetype' = 'image/jpeg';
        if v_video_size is distinct from v_clip.byte_size or not coalesce(v_poster_size between 1 and 262144, false) then
            raise exception 'Both uploaded files must match the reservation.' using errcode = 'KM006';
        end if;
        update public.community_clips set status = 'published' where id = v_id;
        return klipp_private.community_clip_json(v_id, v_actor);

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

-- Row locks serialize a permitted upload with the owner's remove/finish RPCs.
-- This prevents an in-flight late upload from recreating an orphan after cleanup.
create function klipp_private.community_storage_access(p_bucket text, p_name text, p_operation text)
returns boolean language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_clip public.community_clips%rowtype;
begin
    if p_bucket not in ('community-videos', 'community-posters') then return false; end if;
    v_actor := klipp_private.community_member_id(p_operation = 'insert');
    if v_actor is null then return false; end if;
    if p_operation = 'insert' then
        select * into v_clip from public.community_clips c where c.owner_id = v_actor and c.status = 'uploading'
            and ((p_bucket = 'community-videos' and c.video_path = p_name)
              or (p_bucket = 'community-posters' and c.poster_path = p_name)) for share;
        return v_clip.id is not null and klipp_private.community_buckets_ready();
    end if;
    select * into v_clip from public.community_clips c where
        (p_bucket = 'community-videos' and c.video_path = p_name)
        or (p_bucket = 'community-posters' and c.poster_path = p_name);
    if v_clip.id is null then return false; end if;
    if p_operation = 'delete' then return v_clip.owner_id = v_actor and v_clip.status in ('uploading', 'deleting'); end if;
    if p_operation = 'select' then return v_clip.owner_id = v_actor or
        (v_clip.status = 'published' and klipp_private.community_member_id(true) is not null
        and exists(select 1 from public.profiles where id = v_clip.owner_id and username is not null)
        and not klipp_private.community_blocked(v_actor, v_clip.owner_id)); end if;
    return false;
end;
$$;

-- Public functions deliberately remain SECURITY INVOKER; privileged code lives
-- only in klipp_private and always checks the actual signed-in member.
create function public.community_begin_upload(p_title text, p_description text, p_duration_seconds double precision, p_byte_size bigint)
returns jsonb language sql security invoker set search_path = '' as $$
    select klipp_private.community_dispatch('begin', jsonb_build_object('title', p_title, 'description', p_description, 'duration', p_duration_seconds, 'bytes', p_byte_size)) $$;
create function public.community_publish_clip(p_clip_id uuid)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('publish', jsonb_build_object('id', p_clip_id)) $$;
create function public.community_get_clip(p_clip_id uuid)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('get', jsonb_build_object('id', p_clip_id)) $$;
create function public.community_feed(p_limit integer default 13, p_before timestamptz default null, p_before_id uuid default null, p_mine boolean default false)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('feed', jsonb_build_object('limit', p_limit, 'before', p_before, 'before_id', p_before_id, 'mine', p_mine)) $$;
create function public.community_set_like(p_clip_id uuid, p_liked boolean)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('like', jsonb_build_object('id', p_clip_id, 'liked', p_liked)) $$;
create function public.community_comments(p_clip_id uuid, p_limit integer default 31, p_before timestamptz default null, p_before_id uuid default null)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('comments', jsonb_build_object('id', p_clip_id, 'limit', p_limit, 'before', p_before, 'before_id', p_before_id)) $$;
create function public.community_add_comment(p_clip_id uuid, p_body text)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('comment', jsonb_build_object('id', p_clip_id, 'body', p_body)) $$;
create function public.community_delete_comment(p_comment_id uuid)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('delete_comment', jsonb_build_object('id', p_comment_id)) $$;
create function public.community_remove_clip(p_clip_id uuid)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('remove', jsonb_build_object('id', p_clip_id)) $$;
create function public.community_finish_delete(p_clip_id uuid)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('finish', jsonb_build_object('id', p_clip_id)) $$;
create function public.community_report_clip(p_clip_id uuid, p_reason text)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('report', jsonb_build_object('id', p_clip_id, 'reason', p_reason)) $$;
create function public.community_block_user(p_user_id uuid, p_blocked boolean)
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('block', jsonb_build_object('user_id', p_user_id, 'blocked', p_blocked)) $$;
create function public.community_blocked_members()
returns jsonb language sql security invoker set search_path = '' as $$ select klipp_private.community_dispatch('blocked', '{}'::jsonb) $$;

revoke all on function klipp_private.community_member_id(boolean), klipp_private.community_blocked(uuid, uuid),
    klipp_private.community_clip_json(uuid, uuid), klipp_private.community_comment_json(uuid),
    klipp_private.community_buckets_ready(), klipp_private.community_dispatch(text, jsonb),
    klipp_private.community_storage_access(text, text, text) from public, anon, authenticated;
grant execute on function klipp_private.community_dispatch(text, jsonb),
    klipp_private.community_storage_access(text, text, text) to authenticated;
revoke all on function public.community_begin_upload(text, text, double precision, bigint),
    public.community_publish_clip(uuid), public.community_get_clip(uuid), public.community_feed(integer, timestamptz, uuid, boolean),
    public.community_set_like(uuid, boolean), public.community_comments(uuid, integer, timestamptz, uuid),
    public.community_add_comment(uuid, text), public.community_delete_comment(uuid), public.community_remove_clip(uuid),
    public.community_finish_delete(uuid), public.community_report_clip(uuid, text), public.community_block_user(uuid, boolean), public.community_blocked_members()
    from public, anon, authenticated;
grant execute on function public.community_begin_upload(text, text, double precision, bigint),
    public.community_publish_clip(uuid), public.community_get_clip(uuid), public.community_feed(integer, timestamptz, uuid, boolean),
    public.community_set_like(uuid, boolean), public.community_comments(uuid, integer, timestamptz, uuid),
    public.community_add_comment(uuid, text), public.community_delete_comment(uuid), public.community_remove_clip(uuid),
    public.community_finish_delete(uuid), public.community_report_clip(uuid, text), public.community_block_user(uuid, boolean), public.community_blocked_members()
    to authenticated;

create policy community_storage_read on storage.objects for select to authenticated using (
    bucket_id in ('community-videos', 'community-posters') and klipp_private.community_storage_access(bucket_id, name, 'select'));
create policy community_storage_upload on storage.objects for insert to authenticated with check (
    bucket_id in ('community-videos', 'community-posters') and owner_id = (select auth.uid())::text
    and klipp_private.community_storage_access(bucket_id, name, 'insert'));
create policy community_storage_delete on storage.objects for delete to authenticated using (
    bucket_id in ('community-videos', 'community-posters') and klipp_private.community_storage_access(bucket_id, name, 'delete'));
-- Restrictive guards keep future permissive bucket policies from weakening ours.
create policy community_storage_read_guard on storage.objects as restrictive for select to authenticated using (
    bucket_id not in ('community-videos', 'community-posters') or klipp_private.community_storage_access(bucket_id, name, 'select'));
create policy community_storage_upload_guard on storage.objects as restrictive for insert to authenticated with check (
    bucket_id not in ('community-videos', 'community-posters') or (owner_id = (select auth.uid())::text
        and klipp_private.community_storage_access(bucket_id, name, 'insert')));
create policy community_storage_delete_guard on storage.objects as restrictive for delete to authenticated using (
    bucket_id not in ('community-videos', 'community-posters') or klipp_private.community_storage_access(bucket_id, name, 'delete'));
create policy community_storage_no_replace on storage.objects as restrictive for update to authenticated
    using (bucket_id not in ('community-videos', 'community-posters'))
    with check (bucket_id not in ('community-videos', 'community-posters'));
create policy community_storage_no_anonymous on storage.objects as restrictive for all to anon
    using (bucket_id not in ('community-videos', 'community-posters'))
    with check (bucket_id not in ('community-videos', 'community-posters'));

comment on table public.community_clips is 'Free beta reservations; release capacity only after Storage API file cleanup. Full 16.25 MiB slot accounting, never claimed byte-size accounting.';
comment on table public.community_reports is 'Moderator/service-role only. No client report reads or moderation privileges.';
commit;
