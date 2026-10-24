using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KClock.Data.Migrations
{
    /// <summary>
    /// The refresh policy telemetry_1h was created without (DESIGN.md §7). Until now nothing
    /// materialised it, so materialized_only = false computed every bucket live from raw rows —
    /// correct, but a year chart rescanned a year of telemetry on every load.
    ///
    /// start_offset is NULL, i.e. the whole history, and that is deliberate on two counts.
    /// Real-time aggregation covers only what lies above the watermark, so a finite start
    /// offset would move the watermark past history it never materialised and that history
    /// would drop out of every chart. And a refresh recomputes only invalidated buckets, so
    /// once caught up a run over all of history costs what its invalidations cost: a late
    /// sample (ingest_max_lateness, 2 h) or a purged range, wherever it lands. end_offset is
    /// one hour, so the hour still filling stays live. The first run, which catches up on
    /// everything stored so far, happens in the job scheduler, not in this migration.
    ///
    /// purge_telemetry is replaced to stop where the policy stops: it used to stay three hours
    /// clear of now because nothing would ever have revisited a bucket it materialised. Now the
    /// policy does, so the only line left is the policy's own, end_offset — refreshing past it
    /// would push the watermark into the live hour, where new rows would sit invisible as
    /// invalidations until the policy caught up an hour later.
    /// </summary>
    public partial class TelemetryHourlyRefreshPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SELECT add_continuous_aggregate_policy('telemetry_1h',
                    start_offset => NULL,
                    end_offset => INTERVAL '1 hour',
                    schedule_interval => INTERVAL '30 minutes');
                """);

            migrationBuilder.Sql(PurgeTelemetry("date_trunc('hour', now() - INTERVAL '1 hour')",
                """
                -- The buckets over just this slice, every other device's
                -- recomputed unchanged because binding_id is in the GROUP BY. Never
                -- past the refresh policy's end_offset: that would move the
                -- watermark into the live hour (TelemetryHourlyRefreshPolicy).
                """));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PurgeTelemetry("date_trunc('hour', now() - INTERVAL '3 hours')",
                """
                -- The buckets over just this slice, every other device's
                -- recomputed unchanged because binding_id is in the GROUP BY. Never
                -- closer to now than samples may still arrive (ingest_max_lateness,
                -- 2 h, and an hour's margin): telemetry_1h has no refresh policy,
                -- so a bucket materialised here is never revisited.
                """));

            migrationBuilder.Sql("SELECT remove_continuous_aggregate_policy('telemetry_1h');");
        }

        // The procedure as AccountDeletion created it, differing only in where the refresh stops.
        private static string PurgeTelemetry(string refreshLimit, string refreshComment) =>
            $$"""
            CREATE OR REPLACE PROCEDURE purge_telemetry(job_id integer, config jsonb)
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

            {{refreshComment}}
                        refresh_from := date_trunc('hour', lo);
                        refresh_to := least(date_trunc('hour', hi) + INTERVAL '1 hour', {{refreshLimit}});
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
            """;
    }
}
