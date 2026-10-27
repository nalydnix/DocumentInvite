using System.Web;
using DocumentInvite.Api.Infrastructure.Blob;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Tests.Files;

/// <summary>
/// The Azure implementation. SAS signing is local, so these run without a storage emulator.
/// The round trip test runs against Azurite only when <c>TEST_AZURITE_CONNECTION</c> is set.
/// </summary>
public class AzureBlobStoreTests
{
    // Azurite's well-known development account.
    private const string DevAccount =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static AzureBlobStore NewStore(string connection = DevAccount, bool createContainer = false) =>
        new(Options.Create(new BlobOptions
        {
            ConnectionString = connection,
            ContainerName = "documents",
            CreateContainerIfMissing = createContainer,
        }), new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task The_upload_url_is_a_create_and_write_only_sas_for_one_blob_valid_ten_minutes()
    {
        var url = await NewStore().CreateUploadUrlAsync("invites/i1/f1", TimeSpan.FromMinutes(10));

        var query = HttpUtility.ParseQueryString(url.Url.Query);
        url.Url.AbsolutePath.Should().Be("/devstoreaccount1/documents/invites/i1/f1");
        query["sp"].Should().Be("cw");
        query["sr"].Should().Be("b");
        query["sig"].Should().NotBeNullOrEmpty();
        DateTimeOffset.Parse(query["se"]!).Should().Be(Now.AddMinutes(10));
        url.ExpiresAt.Should().Be(Now.AddMinutes(10));
    }

    [Fact]
    public async Task The_read_url_is_a_read_only_sas_valid_five_minutes_with_the_download_name()
    {
        var url = await NewStore().CreateReadUrlAsync(
            "invites/i1/f1", TimeSpan.FromMinutes(5), "LN-1_Doe_Item_1.pdf", "application/pdf");

        var query = HttpUtility.ParseQueryString(url.Url.Query);
        query["sp"].Should().Be("r");
        query["sr"].Should().Be("b");
        DateTimeOffset.Parse(query["se"]!).Should().Be(Now.AddMinutes(5));
        query["rscd"].Should().Be("attachment; filename=\"LN-1_Doe_Item_1.pdf\"");
        query["rsct"].Should().Be("application/pdf");
    }

    [Fact]
    public void A_missing_connection_string_is_a_clear_error() =>
        FluentActions.Invoking(() => NewStore(connection: ""))
            .Should().Throw<InvalidOperationException>().WithMessage("*Blob:ConnectionString*");

    [Fact]
    public async Task Round_trip_against_azurite_when_configured()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_AZURITE_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        var store = NewStore(connection, createContainer: true);
        var name = $"invites/test/{Guid.NewGuid():D}";
        var bytes = "%PDF-1.4 hello"u8.ToArray();

        // The browser's PUT with the write-only SAS.
        var upload = await store.CreateUploadUrlAsync(name, TimeSpan.FromMinutes(10));
        using var http = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, upload.Url) { Content = new ByteArrayContent(bytes) };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        put.Content.Headers.ContentType = new("application/pdf");
        (await http.SendAsync(put)).EnsureSuccessStatusCode();

        // The write SAS cannot read the blob back.
        (await http.GetAsync(upload.Url)).IsSuccessStatusCode.Should().BeFalse();

        var info = await store.GetInfoAsync(name);
        info.Should().Be(new BlobInfo(bytes.Length, "application/pdf"));

        var read = await store.CreateReadUrlAsync(name, TimeSpan.FromMinutes(5), "x.pdf", "application/pdf");
        var response = await http.GetAsync(read.Url);
        response.EnsureSuccessStatusCode();
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);

        // The read SAS cannot write.
        using var badPut = new HttpRequestMessage(HttpMethod.Put, read.Url) { Content = new ByteArrayContent(bytes) };
        badPut.Headers.Add("x-ms-blob-type", "BlockBlob");
        (await http.SendAsync(badPut)).IsSuccessStatusCode.Should().BeFalse();

        await using (var stream = await store.OpenReadAsync(name))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.ToArray().Should().Equal(bytes);
        }

        await store.DeleteAsync(name);
        (await store.GetInfoAsync(name)).Should().BeNull();
    }
}
