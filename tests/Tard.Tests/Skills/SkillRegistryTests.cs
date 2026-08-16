using Tard.Skills;

namespace Tard.Tests.Skills;

public class SkillRegistryTests
{
    [Fact]
    public void GetSkill_ReturnsRegistered()
    {
        var skill = new TimeSkill();
        var registry = new SkillRegistry(new[] { skill });

        Assert.Same(skill, registry.GetSkill("get_current_time"));
    }

    [Fact]
    public void GetSkill_CaseInsensitive()
    {
        var skill = new TimeSkill();
        var registry = new SkillRegistry(new[] { skill });

        Assert.Same(skill, registry.GetSkill("GET_CURRENT_TIME"));
    }

    [Fact]
    public void GetSkill_Unknown_ReturnsNull()
    {
        var registry = new SkillRegistry(Array.Empty<ISkill>());
        Assert.Null(registry.GetSkill("nonexistent"));
    }

    [Fact]
    public void ToAiTools_MapsCorrectly()
    {
        var skill = new TimeSkill();
        var registry = new SkillRegistry(new[] { skill });

        var tools = registry.ToAiTools();
        Assert.Single(tools);
        Assert.Equal("get_current_time", tools[0].Name);
        Assert.NotEmpty(tools[0].Description);
    }
}
