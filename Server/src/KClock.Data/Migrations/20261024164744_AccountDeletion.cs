using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KClock.Data.Migrations
{
    /// <summary>
    /// Account deletion, DESIGN.md §10, in its two phases.
    ///
    /// Phase 1 is delete_account(): SECURITY DEFINER like find_or_create_account, because
    /// clock_app holds none of the grants it needs (external_login, device_credential,
    /// device_access deletes), and should not. It acts only on current_account_id(), so the
    /// one thing a caller controls is the GUC AccountScopeFilter already set for them. After it
    /// commits no view reaches the account's telemetry: device_access is gone.
    ///
    /// Phase 2 is purge_telemetry(), a TimescaleDB job rather than code in the API, for two
    /// reasons. refresh_continuous_aggregate cannot run inside a transaction block, so it
    /// cannot sit in a function the API calls; a job procedure runs non-atomically and may
    /// COMMIT. And the job runs as its owner, clock_migrator, so the API keeps its three-role
    /// model (§8) instead of gaining a connection that may delete telemetry. It deletes one
    /// (binding, chunk) at a time, oldest first, committing and recording how far it got after
    /// each, so a restart resumes rather than repeats — refreshing the hourly aggregate over
    /// each slice as it goes, which deleting raw rows alone never does (§10, "the step
    /// everyone forgets"), and which per slice stays a bounded job however long the ownership
    /// lasted. Partly decompressed chunks are recompressed by the existing compression policy.
    /// The procedure has no SET search_path, unlike delete_account(), and qualifies every name
    /// instead: PostgreSQL forbids COMMIT in a procedure that carries a SET clause.
    /// purge_queue is raw SQL, not an EF entity, because nothing but this SQL touches it.
    /// </summary>
    public partial class AccountDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE purge_queue (
                    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    binding_id   bigint NOT NULL,
                    device_id    bigint NOT NULL,
                    range_start  timestamptz NOT NULL,
                    range_end    timestamptz NOT NULL,
                    purged_until timestamptz,
                    enqueued_at  timestamptz NOT NULL DEFAULT now(),
                    done_at      timestamptz);

                CREATE INDEX ix_purge_queue_pending ON purge_queue (id) WHERE done_at IS NULL;
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION delete_account() RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $$
                DECLARE
                    v_me uuid := current_account_id();
                BEGIN
                    IF v_me IS NULL THEN
                        RAISE EXCEPTION 'delete_account() needs app.account_id';
                    END IF;

                    UPDATE device_binding
                    SET unbound_at = now(), unbound_reason = 'account deleted'
                    WHERE account_id = v_me AND unbound_at IS NULL;

                    UPDATE device_credential
                    SET revoked_at = now(), revoked_reason = 'account deleted'
                    WHERE revoked_at IS NULL
                      AND binding_id IN (SELECT id FROM device_binding WHERE account_id = v_me);

                    INSERT INTO purge_queue (binding_id, device_id, range_start, range_end)
                    SELECT id, device_id, bound_at, unbound_at
                    FROM device_binding
                    WHERE account_id = v_me;

                    -- The names and zones an owner gave their clocks are theirs too.
                    UPDATE device_binding SET display_name = NULL, timezone = NULL WHERE account_id = v_me;
                    DELETE FROM device_settings
                    WHERE binding_id IN (SELECT id FROM device_binding WHERE account_id = v_me);

                    -- Every grant on the account's bindings, not only its own: a viewer of
                    -- someone's deleted history must lose it too.
                    DELETE FROM device_access
                    WHERE account_id = v_me
                       OR binding_id IN (SELECT id FROM device_binding WHERE account_id = v_me);

                    DELETE FROM enrollment_code WHERE account_id = v_me;
                    DELETE FROM external_login WHERE account_id = v_me;

                    UPDATE account SET email = NULL, display_name = NULL, deleted_at = now() WHERE id = v_me;
                END;
                $$;

                REVOKE ALL ON FUNCTION delete_account() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION delete_account() TO clock_app;
                """);

            migrationBuilder.Sql(
                """
                CREATE PROCEDURE purge_telemetry(job_id integer, config jsonb)
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    q record;
                    c record;
                    lo timestamptz;
                    hi timestamptz;
                    refresh_from timestamptz;
                    refresh_to timestamptz;
                BEGIN
                    FOR q IN
                        SELECT * FROM public.purge_queue WHERE done_at IS NULL ORDER BY id LIMIT 20
                    LOOP
                        FOR c IN
                            SELECT ch.range_start, ch.range_end
                            FROM timescaledb_information.chunks ch
                            WHERE ch.hypertable_schema = 'public' AND ch.hypertable_name = 'telemetry'
                              AND ch.range_end > coalesce(q.purged_until, q.range_start)
                              AND ch.range_start < q.range_end
                            ORDER BY ch.range_start
                        LOOP
                            lo := greatest(c.range_start, q.range_start);
                            hi := least(c.range_end, q.range_end);

                            -- device_id and binding_id are the segment key, so only this
                            -- binding's compressed batches are touched (DESIGN.md §10).
                            DELETE FROM public.telemetry
                            WHERE device_id = q.device_id AND binding_id = q.binding_id
                              AND ts >= lo AND ts < hi;
                            COMMIT;

                            -- The buckets over just this slice, every other device's
                            -- recomputed unchanged because binding_id is in the GROUP BY. Never
                            -- closer to now than samples may still arrive (ingest_max_lateness,
                            -- 2 h, and an hour's margin): telemetry_1h has no refresh policy,
                            -- so a bucket materialised here is never revisited, and a late
                            -- sample from any other clock landing in it would go missing from
                            -- every chart. Recent buckets need no refresh anyway — nothing
                            -- materialised them, so they are computed from the rows just deleted.
                            refresh_from := date_trunc('hour', lo);
                            refresh_to := least(date_trunc('hour', hi) + INTERVAL '1 hour', date_trunc('hour', now() - INTERVAL '3 hours'));
                            IF refresh_to > refresh_from THEN
                                CALL public.refresh_continuous_aggregate('public.telemetry_1h', refresh_from, refresh_to);
                            END IF;

                            UPDATE public.purge_queue SET purged_until = hi WHERE id = q.id;
                            COMMIT;
                        END LOOP;

                        UPDATE public.purge_queue SET done_at = now() WHERE id = q.id;
                        COMMIT;
                    END LOOP;
                END;
                $$;

                SELECT add_job('purge_telemetry', INTERVAL '5 minutes');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SELECT delete_job(job_id) FROM timescaledb_information.jobs WHERE proc_name = 'purge_telemetry';
                DROP PROCEDURE IF EXISTS purge_telemetry(integer, jsonb);
                DROP FUNCTION IF EXISTS delete_account();
                DROP TABLE IF EXISTS purge_queue;
                """);
        }
    }
}
