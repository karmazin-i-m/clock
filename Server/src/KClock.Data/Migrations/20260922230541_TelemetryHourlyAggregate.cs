using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KClock.Data.Migrations
{
    /// <summary>
    /// Kept single-purpose and reduced to exactly one statement, per DESIGN.md §12:
    /// CREATE MATERIALIZED VIEW ... WITH (timescaledb.continuous) cannot run inside a
    /// transaction block, so suppressTransaction: true is mandatory here, which also means
    /// this migration is not atomic — a failure mid-run leaves a half-applied state, which is
    /// exactly why nothing else shares this migration.
    ///
    /// binding_id MUST be in the GROUP BY (§15 review item 2, §7): an aggregate grouped only
    /// by (device_id, bucket) would silently average two owners' readings together across a
    /// transfer, and the fix would mean dropping and re-materialising the whole view.
    /// </summary>
    public partial class TelemetryHourlyAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WITH NO DATA is not optional: WITH DATA backfills the entire history inside the
            // migration, and there is a known failure executing that inside an
            // extended-protocol pipeline, which is how Npgsql talks to the server (DESIGN.md §7).
            migrationBuilder.Sql(
                """
                CREATE MATERIALIZED VIEW telemetry_1h
                WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
                SELECT device_id, binding_id, time_bucket(INTERVAL '1 hour', ts) AS bucket,
                       avg(temperature_dc)::smallint AS temp_avg,
                       min(temperature_dc) AS temp_min, max(temperature_dc) AS temp_max,
                       avg(pressure_mmhg)::smallint AS pressure_avg,
                       avg(humidity_pct)::smallint  AS humidity_avg,
                       count(*) AS samples
                FROM telemetry
                GROUP BY device_id, binding_id, bucket
                WITH NO DATA;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP MATERIALIZED VIEW IF EXISTS telemetry_1h;",
                suppressTransaction: true);
        }
    }
}
