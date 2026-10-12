using THOBOTTO.Data;

namespace THOBOTTO.Tests;

public class DataStartupTests
{
    [Fact]
    public void Migrations_this_build_doesnt_know_are_named()
        => Assert.Equal(
            ["20270101000000_Later", "20270202000000_LaterStill"],
            DataStartup.NewerMigrations(["20270202000000_LaterStill", "20261010235925_Feedback", "20270101000000_Later"], ["20261010235925_Feedback", "20261011014136_Personality"]));

    [Fact]
    public void Migrations_still_to_apply_arent_newer()
        => Assert.Empty(DataStartup.NewerMigrations(["20261010235925_Feedback"], ["20261010235925_Feedback", "20261011014136_Personality"]));

    [Fact]
    public void An_empty_database_isnt_newer()
        => Assert.Empty(DataStartup.NewerMigrations([], ["20261010235925_Feedback"]));
}
