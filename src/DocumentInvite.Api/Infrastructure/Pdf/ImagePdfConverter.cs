using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace DocumentInvite.Api.Infrastructure.Pdf;

/// <summary>Converts a JPG or PNG into a single-page A4 PDF with the image fitted to the page.</summary>
public interface IImagePdfConverter
{
    byte[] Convert(byte[] image);
}

public sealed class QuestPdfImageConverter : IImagePdfConverter
{
    static QuestPdfImageConverter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Convert(byte[] image) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20);
            page.Content().AlignCenter().AlignMiddle().Image(image).FitArea();
        })).GeneratePdf();
}
