using THOBOTTO.Voice.Commands;

namespace THOBOTTO.Tests;

public class TypedCommandsTests
{
    [Fact]
    public void Only_a_name_followed_by_a_comma_or_colon_is_said_to_a_helper()
    {
        Assert.Equal("skip", TypedCommands.Addressed("Jeeves, skip", "Jeeves"));
        Assert.Equal("what's playing?", TypedCommands.Addressed("jeeves: what's playing?", "Jeeves"));
        Assert.Null(TypedCommands.Addressed("Jeeves is funny", "Jeeves"));
        Assert.Null(TypedCommands.Addressed("Jeeves,", "Jeeves"));
        Assert.Null(TypedCommands.Addressed("Jeevesy, hi", "Jeeves"));
    }
}
