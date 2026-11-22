using AutoMapper;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Penrill.Application.Boards.Queries.GetBoards;
using Penrill.Domain.Entities;
using Shouldly;

namespace Penrill.Application.UnitTests.Common.Mappings;

public class MappingTests
{
    private readonly IConfigurationProvider _configuration =
        new MapperConfiguration(config => config.AddMaps(typeof(BoardSummaryDto).Assembly), NullLoggerFactory.Instance);

    [Test]
    public void ShouldHaveValidConfiguration() => _configuration.AssertConfigurationIsValid();

    [Test]
    public void BoardsWithoutAnOwnerListAnEmptyOwner()
    {
        var mapper = _configuration.CreateMapper();
        var dto = mapper.Map<BoardSummaryDto>(new Board { Id = "b", Title = "T", Seq = 3, Owner = null });
        dto.Owner.ShouldBe("");
        dto.Seq.ShouldBe(3);
    }
}
