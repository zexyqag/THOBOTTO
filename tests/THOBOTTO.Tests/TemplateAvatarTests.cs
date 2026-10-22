using THOBOTTO.Helpers;

namespace THOBOTTO.Tests;

public class TemplateAvatarTests
{
    [Fact]
    public void Every_template_has_a_small_png_avatar()
    {
        Assert.NotEmpty(PersonalityFile.Templates);
        Assert.All(PersonalityFile.Templates, template =>
        {
            var avatar = template.AvatarData;
            Assert.NotNull(avatar);
            Assert.Equal("image/png", avatar.Value.Type);
            Assert.StartsWith("\u0089PNG", System.Text.Encoding.Latin1.GetString(avatar.Value.Bytes[..4]));
            Assert.InRange(avatar.Value.Bytes.Length, 1, PersonalityBook.MaxAvatarBytes);
        });
    }
}
