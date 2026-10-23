-- Continue abandoned owner-requested deletions as well as expired uploads.
-- A deleting home row is already owner-authorized by community_remove_clip;
-- published rows and all legacy Storage rows are never maintenance candidates.
begin;

create or replace function klipp_private.community_home_claim_expired_uploads(p_limit integer default 10)
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
          and ((c.status = 'uploading' and c.upload_expires_at <= v_now) or c.status = 'deleting')
        -- Owner removals join immediately, even if their reservation expiry is
        -- still in the future. Rotating attempts keeps failed cleanup fair.
        order by coalesce(e.last_claimed_at, case when c.status = 'deleting' then c.created_at else c.upload_expires_at end), c.id
        limit p_limit for update of c skip locked
    loop
        if v_clip.status = 'uploading' then
            update public.community_clips set status = 'deleting' where id = v_clip.id;
        end if;
        insert into klipp_private.community_upload_expirations(clip_id, started_at, last_claimed_at)
            values(v_clip.id, v_now, v_now)
            on conflict (clip_id) do update set last_claimed_at = excluded.last_claimed_at;
        v_result := v_result || jsonb_build_array(jsonb_build_object('id', v_clip.id, 'owner_id', v_clip.owner_id));
    end loop;
    return v_result;
end;
$$;
comment on function klipp_private.community_home_claim_expired_uploads(integer) is
    'Stable maintenance RPC for expired pending uploads and owner-authorized home deletion tombstones. Claims never release quota; gateway deletes physical files before confirming cleanup.';
comment on table klipp_private.community_upload_expirations is
    'Scoped gateway cleanup claims for expired uploads or owner-authorized home removals. Historical table name retained for compatibility. Physical cleanup must complete before metadata deletion.';


-- Incomplete deletions retain the pending-work limits until their physical files
-- are removed. reserve -> remove cannot open another pair of pending slots.
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
    if (select count(*) from public.community_clips where owner_id = v_actor and storage_provider = 'home' and status in ('uploading', 'deleting')) >= 2
      or (select count(*) from public.community_clips where storage_provider = 'home' and status in ('uploading', 'deleting')) >= 10 then
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

commit;
