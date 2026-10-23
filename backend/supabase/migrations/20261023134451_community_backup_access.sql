-- A separate PostgreSQL login is used only by the root-owned encrypted backup job.
-- No service-role key, write grant, role membership or public RPC is introduced.
begin;

create role klipp_backup_reader nologin nosuperuser nocreatedb nocreaterole
    noinherit noreplication bypassrls connection limit 2;
alter role klipp_backup_reader set default_transaction_read_only = on;
alter role klipp_backup_reader set statement_timeout = '5min';
alter role klipp_backup_reader set idle_in_transaction_session_timeout = '1min';
alter role klipp_backup_reader set search_path = '';
grant connect on database postgres to klipp_backup_reader;
grant usage on schema public, auth, klipp_private, storage to klipp_backup_reader;
grant select on all tables in schema public, auth, klipp_private, storage to klipp_backup_reader;
grant select on all sequences in schema public, auth, klipp_private, storage to klipp_backup_reader;
-- Future application migrations are owned by postgres. Managed Auth/Storage
-- upgrades owned by another role require a reviewed grant before their next dump.
alter default privileges for role postgres in schema public, klipp_private
    grant select on tables to klipp_backup_reader;
alter default privileges for role postgres in schema public, klipp_private
    grant select on sequences to klipp_backup_reader;

-- Root-only provisioning helper: generate the password in the database and
-- return only a PGP envelope addressed to an ephemeral server-local recipient.
-- The cleartext password is never returned to a dashboard, client or tool log.
create function klipp_private.community_provision_backup_credential(p_recipient text)
returns text language plpgsql security invoker set search_path = '' as $$
declare
    v_password text := encode(extensions.gen_random_bytes(48), 'hex');
    v_payload text;
begin
    if current_user <> 'postgres' or char_length(p_recipient) not between 500 and 12000 then
        raise exception 'Backup credential provisioning requires the database administrator.';
    end if;
    execute format('alter role klipp_backup_reader login password %L', v_password);
    v_payload := jsonb_build_object('password', v_password)::text;
    return encode(extensions.pgp_pub_encrypt(v_payload, extensions.dearmor(p_recipient),
        'cipher-algo=aes256,compress-algo=0'), 'base64');
end;
$$;
revoke all on function klipp_private.community_provision_backup_credential(text)
    from public, anon, authenticated, service_role, klipp_backup_reader;
comment on role klipp_backup_reader is
    'Root-only logical backup reader. Complete scoped SELECT bypasses RLS; never use in clients or the media API. Rotate on host compromise.';

commit;
