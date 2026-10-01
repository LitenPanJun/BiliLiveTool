using System.Text.Json.Nodes;
using BiliLiveTool.Services;
using FluentAssertions;

namespace BiliLiveTool.Tests.Live;

public class JsonReadTests
{
    [Fact]
    public void ReadLong_Handles_Typed_Long_Int_Double_And_String()
    {
        JsonRead.ReadLong(235L).Should().Be(235);
        JsonRead.ReadLong(235).Should().Be(235); // typed int 节点严格不宽化，辅助须兼容
        JsonRead.ReadLong(235.0).Should().Be(235);
        JsonRead.ReadLong("235").Should().Be(235);
        JsonRead.ReadLong(JsonNode.Parse("235")).Should().Be(235);
        JsonRead.ReadLong(JsonNode.Parse("235.0")).Should().Be(235);
        JsonRead.ReadLong(JsonNode.Parse("\"3259\"")).Should().Be(3259);
        JsonRead.ReadLong(null).Should().BeNull();
        JsonRead.ReadLong(new JsonObject()).Should().BeNull();
    }

    [Fact]
    public void ReadText_Formats_Numbers_And_Returns_Strings()
    {
        JsonRead.ReadText("开播啦").Should().Be("开播啦");
        JsonRead.ReadText(3259).Should().Be("3259");
        JsonRead.ReadText(3259L).Should().Be("3259");
        JsonRead.ReadText(JsonNode.Parse("3259")).Should().Be("3259");
        JsonRead.ReadText(JsonNode.Parse("\"123\"")).Should().Be("123");
        JsonRead.ReadText(null).Should().BeNull();
        JsonRead.ReadText(new JsonArray()).Should().BeNull();
    }

    [Fact]
    public void ReadInt_Narrows_And_Falls_Back_To_Null()
    {
        JsonRead.ReadInt(235).Should().Be(235);
        JsonRead.ReadInt(235L).Should().Be(235);
        JsonRead.ReadInt("204").Should().Be(204);
        JsonRead.ReadInt(null).Should().BeNull();
        JsonRead.ReadInt(new JsonObject()).Should().BeNull();
    }

    [Fact]
    public void ReadDouble_Handles_Numbers_And_Parses_Strings()
    {
        JsonRead.ReadDouble(1.5).Should().Be(1.5);
        JsonRead.ReadDouble(2L).Should().Be(2);
        JsonRead.ReadDouble("2.5").Should().Be(2.5);
        JsonRead.ReadDouble(null).Should().Be(0);
        JsonRead.ReadDouble(new JsonObject()).Should().Be(0);
    }
}
