using DocumentInvite.Api.Features.Files;
using FluentAssertions;

namespace DocumentInvite.Api.Tests.Files;

public class FileNamingTests
{
    [Theory]
    [InlineData("O'Brien", "OBrien")]
    [InlineData("Bank Statement", "Bank-Statement")]
    [InlineData("  spaced   out  ", "spaced-out")]
    [InlineData("snake_case_name", "snake-case-name")]
    [InlineData("José Müller", "Jose-Muller")]
    [InlineData("a/b\\c:d*e?f", "abcdef")]
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("--dash--", "dash")]
    [InlineData("a - b", "a-b")]
    [InlineData("Tax Return 2024 (Form 1040)", "Tax-Return-2024-Form-1040")]
    [InlineData("名前", "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Sanitise_keeps_only_letters_digits_and_dashes(string? input, string expected) =>
        FileNaming.Sanitise(input).Should().Be(expected);

    [Fact]
    public void Sanitise_caps_the_length_and_never_ends_with_a_dash()
    {
        var result = FileNaming.Sanitise(new string('a', 39) + " tail", 40);

        result.Should().HaveLength(39).And.Be(new string('a', 39));
        FileNaming.Sanitise(new string('x', 500)).Should().HaveLength(FileNaming.MaxSegmentLength);
    }

    [Fact]
    public void Canonical_name_joins_the_sanitised_parts()
    {
        FileNaming.Canonical("LN-2024/007", "O'Brien", "Bank Statements", 2, "pdf")
            .Should().Be("LN-2024007_OBrien_Bank-Statements_2.pdf");
    }

    [Fact]
    public void Canonical_name_lowercases_the_extension_and_accepts_a_leading_dot() =>
        FileNaming.Canonical("L1", "Doe", "Item", 1, ".XLSX").Should().Be("L1_Doe_Item_1.xlsx");

    [Fact]
    public void Canonical_name_never_contains_path_or_separator_characters()
    {
        var name = FileNaming.Canonical("../L", "a\\b", "c/d\0e", 3, "pdf");

        name.Should().MatchRegex("^[A-Za-z0-9-]+_[A-Za-z0-9-]+_[A-Za-z0-9-]+_3\\.pdf$");
    }

    [Fact]
    public void Canonical_name_requires_a_positive_sequence() =>
        FluentActions.Invoking(() => FileNaming.Canonical("L", "D", "I", 0, "pdf"))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(new int[0], 1)]
    [InlineData(new[] { 1 }, 2)]
    [InlineData(new[] { 1, 2, 3 }, 4)]
    [InlineData(new[] { 1, 3 }, 4)]
    public void Sequence_continues_after_the_highest_used_number(int[] used, int expected) =>
        FileNaming.NextSequence(used).Should().Be(expected);

    [Theory]
    [InlineData("application/pdf", "report.PDF", true)]
    [InlineData("image/jpeg", "photo.jpeg", true)]
    [InlineData("image/jpeg", "photo.jpg", true)]
    [InlineData("image/jpeg", "photo.png", false)]
    [InlineData("text/csv; charset=utf-8", "data.csv", true)]
    [InlineData("application/x-msdownload", "setup.exe", false)]
    [InlineData("text/html", "page.html", false)]
    public void Allow_list_matches_content_type_and_extension(string contentType, string fileName, bool allowed)
    {
        var type = FileTypes.Find(contentType);

        (type is not null && FileTypes.ExtensionMatches(type, fileName)).Should().Be(allowed);
    }
}
