using DMXCore.PluginSdk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DMXCore100.HomeAssistantPlugin.Tests;

[TestClass]
public class ExposedLooksTests
{
    [TestMethod]
    public void Parse_SplitsCommaNewlineAndSemicolon()
    {
        IReadOnlyList<string> tokens = ExposedLooks.Parse("Party Mode, cue.SUNSET;\npreset.IDLE");

        CollectionAssert.AreEqual(new[] { "Party Mode", "cue.SUNSET", "preset.IDLE" }, tokens.ToList());
    }

    [TestMethod]
    public void Parse_Blank_IsEmpty()
    {
        Assert.AreEqual(0, ExposedLooks.Parse("  \n  ").Count);
        Assert.AreEqual(0, ExposedLooks.Parse(null).Count);
    }

    [TestMethod]
    public void Matches_EmptyAllowList_AllowsAll()
    {
        var party = new PluginEntity { Code = "preset.PARTY", Name = "Party Mode", Kind = PluginEntityKind.Scene };

        Assert.IsTrue(ExposedLooks.Matches(party, []));
    }

    [TestMethod]
    public void Matches_NameCodeAndShortcode()
    {
        var party = new PluginEntity { Code = "preset.PARTY", Name = "Party Mode", Kind = PluginEntityKind.Scene };
        var sunset = new PluginEntity { Code = "cue.SUNSET", Name = "Sunset Show", Kind = PluginEntityKind.Scene };

        Assert.IsTrue(ExposedLooks.Matches(party, ["Party Mode"]));
        Assert.IsTrue(ExposedLooks.Matches(party, ["preset.PARTY"]));
        Assert.IsTrue(ExposedLooks.Matches(party, ["PARTY"]));
        Assert.IsFalse(ExposedLooks.Matches(sunset, ["preset.PARTY"]));
        Assert.IsTrue(ExposedLooks.Matches(sunset, ["cue.SUNSET"]));
    }
}
