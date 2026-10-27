using System.Net;
using System.Net.Http.Json;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Features.Contacts;
using DocumentInvite.Api.Tests.Infrastructure;
using FluentAssertions;

namespace DocumentInvite.Api.Tests.Common;

public class PagingUnitTests
{
    private readonly PageQueryValidator _validator = new();

    [Fact]
    public void Defaults_are_page_1_of_25()
    {
        var query = new PageQuery();

        query.Page.Should().Be(1);
        query.PageSize.Should().Be(25);
        PagingDefaults.MaxPageSize.Should().Be(100);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 100, true)]
    [InlineData(500, 25, true)]
    [InlineData(0, 25, false)]
    [InlineData(-1, 25, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 101, false)]
    [InlineData(1, -5, false)]
    public void Validator_enforces_the_bounds(int page, int pageSize, bool valid)
    {
        _validator.Validate(new PageQuery(page, pageSize)).IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData(1, 25, 0)]
    [InlineData(2, 25, 25)]
    [InlineData(3, 10, 20)]
    [InlineData(int.MaxValue, 100, int.MaxValue)]
    public void Offset_skips_the_earlier_pages(int page, int pageSize, int expected)
    {
        PagingExtensions.Offset(page, pageSize).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 25, 1)]
    [InlineData(1, 25, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(100, 10, 10)]
    [InlineData(101, 10, 11)]
    public void PageCount_rounds_up_and_is_at_least_one(int total, int pageSize, int expected)
    {
        PagingExtensions.PageCount(total, pageSize).Should().Be(expected);
    }
}

public class PagingApiTests : StaffTestBase
{
    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task Invalid_paging_returns_a_validation_problem(string query)
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);

        var response = await client.GetAsync($"/api/contacts?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Pages_never_overlap_or_skip_rows_even_with_identical_sort_keys()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        var created = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            // Same first and last name, so only the id breaks the tie.
            var response = await client.PostAsJsonAsync("/api/contacts", new ContactRequest(
                "Same", "Name", $"same{i}@example.com", null, PartyType.Borrower));
            created.Add((await response.Content.ReadFromJsonAsync<ContactResponse>())!.Id);
        }

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var body = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>($"/api/contacts?page={page}&pageSize=3"))!;
            body.Total.Should().Be(7);
            seen.AddRange(body.Items.Select(i => i.Id));
        }

        seen.Should().HaveCount(7).And.OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(created);
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_reports_the_total()
    {
        var client = await ClientForAsync("lo@example.com", [StaffRoles.LoanOfficer]);
        await client.PostAsJsonAsync("/api/contacts", new ContactRequest("A", "B", "ab@example.com", null, PartyType.Borrower));

        var body = (await client.GetFromJsonAsync<PagedResponse<ContactResponse>>("/api/contacts?page=9&pageSize=5"))!;

        body.Items.Should().BeEmpty();
        body.Total.Should().Be(1);
        body.Page.Should().Be(9);
    }
}
