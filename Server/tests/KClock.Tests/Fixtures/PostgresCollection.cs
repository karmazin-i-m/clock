using Xunit;

namespace KClock.Tests.Fixtures;

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// A database of its own for tests that materialise the hourly aggregate. Materialising moves
/// its watermark, and buckets below the watermark stop being computed live — which in the
/// shared container would hide the rows every other test inserts after.
/// </summary>
[CollectionDefinition(Name)]
public class MaterializingPostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres, materialising";
}
