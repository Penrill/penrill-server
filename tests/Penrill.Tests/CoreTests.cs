using System.Text.Json.Nodes;
using Penrill.Core.Boards;

namespace Penrill.Tests;

public sealed class CoreTests
{
    private static Change Parse(string json)
    {
        Assert.True(ChangeCodec.TryParse(JsonNode.Parse(json), out var change));
        return change;
    }

    [Theory]
    [InlineData("""{"id":"c1","ops":[]}""")]
    [InlineData("""{"id":"c1","ops":[{"op":"put","item":{"type":"stroke"}}]}""")]
    [InlineData("""{"id":"c1","ops":[{"op":"patch","id":"a","set":{"id":"b"}}]}""")]
    [InlineData("""{"id":"c1","ops":[{"op":"patch","id":"a"}]}""")]
    [InlineData("""{"id":"c1","ops":[{"op":"meta"}]}""")]
    [InlineData("""{"id":"c1","ops":[{"op":"delete","id":"a"}],"extra":1}""")]
    [InlineData("""{"ops":[{"op":"delete","id":"a"}]}""")]
    [InlineData("""{"id":"c1","ops":[{"op":"move","id":"a"}]}""")]
    public void RefusesMalformedChanges(string json) => Assert.False(ChangeCodec.TryParse(JsonNode.Parse(json), out _));

    [Fact]
    public void AppliesOperationsInOrder()
    {
        var document = new BoardDocument("b", 0, "Board", "dots", []);
        document.Apply(1, Parse("""
            {"id":"c1","ops":[
              {"op":"put","item":{"id":"a","z":1,"color":"red","rot":1}},
              {"op":"put","item":{"id":"b","z":2}},
              {"op":"patch","id":"a","set":{"color":"blue"},"unset":["rot"]},
              {"op":"patch","id":"gone","set":{"x":1}},
              {"op":"delete","id":"b"},
              {"op":"meta","title":"Physics"}]}
            """));
        Assert.Equal(1, document.Seq);
        Assert.Equal("Physics", document.Title);
        Assert.Equal("dots", document.Background);
        var items = BoardDocument.ReadItems(document.SerializeItems());
        var item = Assert.Single(items);
        Assert.Equal("""{"id":"a","z":1,"color":"blue"}""", item.ToJsonString());
    }

    [Fact]
    public void ReplayingAChangeGivesTheSameBoard()
    {
        var change = Parse("""{"id":"c1","ops":[{"op":"put","item":{"id":"a","x":1}},{"op":"patch","id":"a","set":{"x":2}},{"op":"delete","id":"z"}]}""");
        var once = new BoardDocument("b", 0, "Board", "dots", []);
        once.Apply(1, change);
        var twice = new BoardDocument("b", 0, "Board", "dots", []);
        twice.Apply(1, change);
        twice.Apply(2, change);
        Assert.Equal(once.SerializeItems(), twice.SerializeItems());
    }

    [Fact]
    public void RefusesBoardsWithDuplicateItems() =>
        Assert.False(BoardContent.TryParse(JsonNode.Parse("""{"title":"T","background":"dots","items":[{"id":"a"},{"id":"a"}]}"""), out _));
}
