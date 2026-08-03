using iRoute.Core;
using iRoute.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace iRoute.UnitTests;

/// <summary>
/// iRoute supports exactly two storage providers: PostgreSQL for deployment and SQLite for
/// single-node development. An unsupported provider must be refused at startup, not on the first
/// database call, so a misconfigured host fails immediately instead of accepting work it cannot
/// durably record.
/// </summary>
public sealed class StorageProviderTests
{
    private static IConfiguration Configuration(string? provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = provider,
                ["ConnectionStrings:iRoute"] = "Data Source=:memory:"
            })
            .Build();

    private static IServiceCollection Build(string? provider) =>
        new ServiceCollection().AddIRouteInfrastructure(Configuration(provider));

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    public void SupportedProvidersRegisterADurableExecutionStore(string provider)
    {
        var services = Build(provider);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IExecutionStore)
                && descriptor.ImplementationType == typeof(EfExecutionStore));
    }

    [Fact]
    public void TheDefaultProviderIsSqlite()
    {
        var services = Build(null);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IExecutionStore)
                && descriptor.ImplementationType == typeof(EfExecutionStore));
    }

    [Fact]
    public void TheMemoryProviderIsRefusedAndNamesTheSupportedProviders()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build("Memory"));

        Assert.Contains("Memory", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Sqlite", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Postgres", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownProviderIsRefusedDuringRegistrationNotOnFirstUse()
    {
        // Previously this threw from inside the DbContext options callback, which runs lazily, so
        // a typo was only discovered when the first request tried to reach the database.
        var failure = Assert.Throws<InvalidOperationException>(() => Build("MySql"));

        Assert.Contains("MySql", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADurableProviderRequiresAConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Postgres"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddIRouteInfrastructure(configuration));
    }
}
