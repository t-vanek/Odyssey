using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Odyssey.Core;
using Odyssey.Infrastructure;
using SharpSevenZip;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Odyssey.Tests;

public sealed class ContentIndexingTests
{
    [Fact]
    public void ConfiguredNativeSevenZipLibraryIsLoadable()
    {
        var configured = Environment.GetEnvironmentVariable("ODYSSEY_7ZIP_LIBRARY");
        if (string.IsNullOrWhiteSpace(configured)) return;

        var reader = Assert.IsType<SevenZipArchiveReader>(SevenZipArchiveReader.Default);
        Assert.Equal(Path.GetFullPath(configured), reader.LibraryPath, OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task ZipEntryNamesAreExtractedLocally()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "evidence.zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("contracts/Smlouva Novák 2021.docx");
                archive.CreateEntry("photos/inspection.jpg");
            }

            var result = await new LocalContentExtractor().ExtractAsync(path);
            Assert.Contains("contracts/Smlouva Novák 2021.docx", result!.Text);
            Assert.Contains("photos/inspection.jpg", result.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OpenDocumentContentIsExtractedThroughArchiveEngine()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-odt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "report.odt");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var content = archive.CreateEntry("content.xml");
                await using var stream = content.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                             xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
                      <office:body><office:text><text:p>Rekonstrukce kanalizace Ostravice</text:p></office:text></office:body>
                    </office:document-content>
                    """);
            }

            var result = await new LocalContentExtractor().ExtractAsync(path);
            Assert.Contains("Rekonstrukce kanalizace Ostravice", result!.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NativeSevenZipArchiveEntryNamesAreSupportedWhenEngineIsAvailable()
    {
        var extractor = new LocalContentExtractor();
        if (!extractor.SupportedExtensions.Contains(".7z")) return;

        var root = Path.Combine(Path.GetTempPath(), $"odyssey-7z-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Stavební povolení 2021.txt");
        var archivePath = Path.Combine(root, "evidence.7z");
        try
        {
            await File.WriteAllTextAsync(source, "test");
            var compressor = new SharpSevenZipCompressor { ArchiveFormat = OutArchiveFormat.SevenZip };
            compressor.CompressFiles(archivePath, source);

            var result = await extractor.ExtractAsync(archivePath);
            Assert.Contains("Stavební povolení 2021.txt", result!.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PlainTextContentBecomesSearchable_WithoutChangingTheSource()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "content"));
        var path = Path.Combine(created.Target.RootPath, "unknown-name.txt");
        await File.WriteAllTextAsync(path, "Rekonstrukce kanalizace pro obec Ostravice a pan Novak.");
        var before = new FileInfo(path);
        var modified = new DateTimeOffset(before.LastWriteTimeUtc, TimeSpan.Zero);
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
            [TestEntries.File(created.Target.Id, path, before.Length, modified)]);
        var extractor = new LocalContentExtractor();

        var candidates = await environment.Store.GetContentExtractionCandidatesAsync(
            created.Session.Id, extractor.SupportedExtensions, 10);
        var candidate = Assert.Single(candidates);
        var extracted = await extractor.ExtractAsync(path);
        await environment.Store.UpdateExtractedContentsAsync(
            [new ContentIndexUpdate(candidate.FileId, candidate.ModifiedAt, extracted!.Text, null)]);
        var result = await environment.Search.SearchAsync(new SearchRequest
            { Query = "kanalizace ostravice", SessionId = created.Session.Id }, CancellationToken.None);

        Assert.Equal("unknown-name.txt", Assert.Single(result.Results).Name);
        Assert.Contains("kanalizace", result.Results.Single().MatchSnippet!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.Length, new FileInfo(path).Length);
        Assert.Equal(before.LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task OpenXmlDocumentTextIsExtractedLocally()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "contract.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new Text("Smlouva Novák 2021")))));
                main.Document.Save();
            }

            var result = await new LocalContentExtractor().ExtractAsync(path);
            Assert.Contains("Smlouva Novák 2021", result!.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OpenXmlSpreadsheetValuesAreExtractedWithSharedStrings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-xlsx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "budget.xlsx");
        try
        {
            using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new Workbook();
                var sharedPart = workbook.AddNewPart<SharedStringTablePart>();
                sharedPart.SharedStringTable = new SharedStringTable(new SharedStringItem(
                    new DocumentFormat.OpenXml.Spreadsheet.Text("Rozpočet kanalizace Ostravice")));
                var worksheet = workbook.AddNewPart<WorksheetPart>();
                worksheet.Worksheet = new Worksheet(new SheetData(new Row(new Cell
                {
                    DataType = CellValues.SharedString,
                    CellValue = new CellValue("0")
                })));
                workbook.Workbook.AppendChild(new Sheets(new Sheet
                {
                    Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Rozpočet"
                }));
                workbook.Workbook.Save();
            }

            var result = await new LocalContentExtractor().ExtractAsync(path);
            Assert.Contains("Rozpočet kanalizace Ostravice", result!.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PdfPigExtractsPdfTextInContentOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "permit.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            page.AddText("Building permit reconstruction 2021", 12, new PdfPoint(40, 760), font);
            await File.WriteAllBytesAsync(path, builder.Build());

            var extractor = new LocalContentExtractor();
            var result = await extractor.ExtractAsync(path);
            Assert.Contains("Building permit reconstruction 2021", result!.Text);
            Assert.Contains(".pdf", extractor.SupportedExtensions);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OpenXmlPresentationSlideTextIsExtracted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-pptx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "project.pptx");
        try
        {
            using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
            {
                var presentation = document.AddPresentationPart();
                presentation.Presentation = new P.Presentation();
                var slide = presentation.AddNewPart<SlidePart>();
                slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()),
                    new P.Shape(
                        new P.NonVisualShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 2, Name = "Project" },
                            new P.NonVisualShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.ShapeProperties(),
                        new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                            new A.Paragraph(new A.Run(new A.Text("Vodohospodářský projekt 2021"))))))));
                var slideIds = presentation.Presentation.AppendChild(new P.SlideIdList());
                slideIds.Append(new P.SlideId
                    { Id = 256, RelationshipId = presentation.GetIdOfPart(slide) });
                presentation.Presentation.Save();
            }

            var result = await new LocalContentExtractor().ExtractAsync(path);
            Assert.Contains("Vodohospodářský projekt 2021", result!.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ChangedMetadataInvalidatesPreviouslyIndexedContent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "invalidate-content"));
        var path = Path.Combine(created.Target.RootPath, "record.txt");
        var firstModified = DateTimeOffset.UtcNow.AddMinutes(-5);
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
            [TestEntries.File(created.Target.Id, path, 10, firstModified)]);
        var candidate = Assert.Single(await environment.Store.GetContentExtractionCandidatesAsync(
            created.Session.Id, new HashSet<string> { ".txt" }, 10));
        await environment.Store.UpdateExtractedContentsAsync(
            [new ContentIndexUpdate(candidate.FileId, candidate.ModifiedAt, "old unique content", null)]);

        var nextScan = new ScanSession
            { Id = Guid.NewGuid(), TargetId = created.Target.Id, StartedAt = DateTimeOffset.UtcNow, Status = ScanStatus.Running };
        await environment.Store.StartScanAsync(nextScan);
        await environment.Store.UpsertEntriesAsync(nextScan.Id,
            [TestEntries.File(created.Target.Id, path, 11, firstModified.AddMinutes(1))]);

        var result = await environment.Search.SearchAsync(new SearchRequest
            { Query = "unique", SessionId = created.Session.Id }, CancellationToken.None);
        Assert.Empty(result.Results);
        Assert.Single(await environment.Store.GetContentExtractionCandidatesAsync(
            created.Session.Id, new HashSet<string> { ".txt" }, 10));
    }

    [Fact]
    public async Task ExistingMetadataOnlyFtsSchemaIsMigratedWithoutLosingFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "migration"));
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
            [TestEntries.File(created.Target.Id, Path.Combine(created.Target.RootPath, "preserved.txt"))]);
        await using (var connection = await environment.Connections.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TRIGGER IF EXISTS Files_AfterInsert;
                DROP TRIGGER IF EXISTS Files_AfterDelete;
                DROP TRIGGER IF EXISTS Files_AfterUpdate;
                DROP TABLE FilesFts;
                CREATE VIRTUAL TABLE FilesFts USING fts5(
                    Name, FullPath, ParentPath, content='Files', content_rowid='Id');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await environment.Search.InitializeAsync();
        var response = await environment.Search.SearchAsync(new SearchRequest
            { Query = "preserved", SessionId = created.Session.Id }, CancellationToken.None);
        Assert.Equal("preserved.txt", Assert.Single(response.Results).Name);
        await using var verify = await environment.Connections.OpenAsync();
        await using var schema = verify.CreateCommand();
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE name='FilesFts';";
        Assert.Contains("ContentText", (string)(await schema.ExecuteScalarAsync())!);
    }

    [Fact]
    public void BundledTesseractAndLanguageDataTakePriorityOverPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-bundled-ocr-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "app");
        var bundledOcrDirectory = Path.Combine(applicationDirectory, "ocr");
        var pathDirectory = Path.Combine(root, "path");
        var fileName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        Directory.CreateDirectory(Path.Combine(bundledOcrDirectory, "tessdata"));
        Directory.CreateDirectory(pathDirectory);
        var bundledExecutable = Path.Combine(bundledOcrDirectory, fileName);
        File.WriteAllBytes(bundledExecutable, [1]);
        File.WriteAllBytes(Path.Combine(pathDirectory, fileName), [2]);
        try
        {
            var installation = LocalContentExtractor.ResolveInstallation(
                configuredPath: null,
                configuredDataDirectory: null,
                applicationDirectory,
                pathDirectory);

            Assert.Equal(bundledExecutable, installation.ExecutablePath, OperatingSystem.IsWindows());
            Assert.Equal(Path.Combine(bundledOcrDirectory, "tessdata"), installation.DataDirectory,
                OperatingSystem.IsWindows());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OcrIsEnabledOnlyWithCzechAndEnglish_AndUsesBothLanguages()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "tesseract");
        var image = Path.Combine(root, "scan.png");
        try
        {
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                if [ "$1" = "--list-langs" ]; then
                  printf 'List of available languages (2):\nces\neng\n'
                  exit 0
                fi
                printf 'Česká smlouva and English invoice'
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllBytesAsync(image, [1, 2, 3, 4]);

            var extractor = new LocalContentExtractor(executable);
            var result = await extractor.ExtractAsync(image);

            Assert.True(extractor.IsAvailable);
            Assert.Equal(["ces", "eng"], extractor.Languages);
            Assert.Contains(".png", extractor.SupportedExtensions);
            Assert.Contains("Česká smlouva", result!.Text);
            Assert.Contains("English invoice", result.Text);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MissingCzechLanguageKeepsImageOcrDisabled()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-ocr-language-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "tesseract");
        try
        {
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf 'List of available languages (1):\\neng\\n'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var extractor = new LocalContentExtractor(executable);

            Assert.False(extractor.IsAvailable);
            Assert.DoesNotContain(".png", extractor.SupportedExtensions);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
