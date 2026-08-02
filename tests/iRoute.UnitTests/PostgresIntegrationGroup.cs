namespace iRoute.UnitTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresIntegrationGroup
{
    public const string Name = "PostgreSQL integration";
}
