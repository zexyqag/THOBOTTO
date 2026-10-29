using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using THOBOTTO.Integrations;

namespace THOBOTTO.Tests;

public class IntegrationEnvironmentTests
{
    private static IntegrationStore With(Dictionary<string, string?> environment)
        => new(null!, new EphemeralDataProtectionProvider(), new ConfigurationBuilder().AddInMemoryCollection(environment).Build(), TimeProvider.System, NullLogger<IntegrationStore>.Instance);

    [Fact]
    public void A_setting_the_environment_gives_takes_its_value_and_names_the_variable()
    {
        var store = With(new() { ["Archive:S3:Bucket"] = "from-the-environment" });
        Assert.Equal("from-the-environment", store.Get(IntegrationStore.ArchiveS3Bucket));
        Assert.Equal("Archive__S3__Bucket", store.SetBy(IntegrationStore.ArchiveS3Bucket));
        Assert.Null(store.SetBy(IntegrationStore.ArchiveS3Url));
        Assert.Null(store.SetBy(IntegrationStore.LastfmApiKey));
    }
}
