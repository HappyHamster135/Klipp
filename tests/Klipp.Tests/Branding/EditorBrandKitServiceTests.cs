using System.Buffers.Binary;
using System.Text;
using Klipp.Core.Branding;

namespace Klipp.Tests.Branding;

public sealed class EditorBrandKitServiceTests
{
    [Fact]
    public async Task SavesNormalizedKitAndReloadsIt()
    {
        using var storage = new TemporaryBrandStorage();
        var service = storage.CreateService();
        var saved = await service.SaveAsync(new EditorBrandKit
        {
            Id = string.Empty,
            Name = "  Stream brand  ",
            FontFamily = "IMPACT",
            PrimaryColor = "aabbcc",
            SecondaryColor = "#123456",
            AccentColor = "invalid",
            TextColor = "#ffffff"
        });

        var reloaded = await storage.CreateService().GetAllAsync();
        var kit = reloaded.Single(candidate => candidate.Id == saved.Id);

        kit.Name.ShouldBe("Stream brand");
        kit.FontFamily.ShouldBe("impact");
        kit.PrimaryColor.ShouldBe("#AABBCC");
        kit.SecondaryColor.ShouldBe("#123456");
        kit.AccentColor.ShouldBe("#B0FF50");
        kit.TextColor.ShouldBe("#FFFFFF");
    }

    [Fact]
    public async Task ImportedLogoRemainsAvailableAfterRemovingItFromKit()
    {
        using var storage = new TemporaryBrandStorage();
        var service = storage.CreateService();
        var kit = (await service.GetAllAsync()).Single();
        var sourcePath = Path.Combine(storage.Root, "logo.png");
        await File.WriteAllBytesAsync(sourcePath, [0x89, 0x50, 0x4E, 0x47]);

        var logo = await service.ImportLogoAsync(kit.Id, sourcePath);
        File.Exists(logo.StoredPath).ShouldBeTrue();
        Path.GetFullPath(logo.StoredPath).ShouldStartWith(Path.GetFullPath(storage.AssetsRoot));

        (await service.RemoveLogoAsync(kit.Id, logo.Id)).ShouldBeTrue();
        File.Exists(logo.StoredPath).ShouldBeTrue();
        (await service.GetAllAsync()).Single().Logos.ShouldBeEmpty();
    }

    [Fact]
    public async Task RejectsUnsupportedLogoType()
    {
        using var storage = new TemporaryBrandStorage();
        var service = storage.CreateService();
        var kit = (await service.GetAllAsync()).Single();
        var sourcePath = Path.Combine(storage.Root, "logo.svg");
        await File.WriteAllTextAsync(sourcePath, "<svg />");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.ImportLogoAsync(kit.Id, sourcePath));
    }

    [Fact]
    public void ReadsFamilyNameFromOpenTypeNameTable()
    {
        var info = OpenTypeFontInspector.Inspect(CreateMinimalTrueType("Klipp Test Sans"));

        info.FamilyName.ShouldBe("Klipp Test Sans");
        info.FullName.ShouldBe("Klipp Test Sans");
    }

    [Fact]
    public async Task ImportedFontRemainsAvailableAfterRemovingItFromKit()
    {
        using var storage = new TemporaryBrandStorage();
        var service = storage.CreateService();
        var kit = (await service.GetAllAsync()).Single();
        var sourcePath = Path.Combine(storage.Root, "brand.ttf");
        await File.WriteAllBytesAsync(sourcePath, CreateMinimalTrueType("Klipp Test Sans"));

        var font = await service.ImportFontAsync(kit.Id, sourcePath);
        font.FamilyName.ShouldBe("Klipp Test Sans");
        File.Exists(font.StoredPath).ShouldBeTrue();
        Path.GetFullPath(font.StoredPath).ShouldStartWith(Path.GetFullPath(storage.AssetsRoot));

        (await service.RemoveFontAsync(kit.Id)).ShouldBeTrue();
        File.Exists(font.StoredPath).ShouldBeTrue();
        (await service.GetAllAsync()).Single().FontAsset.ShouldBeNull();
    }

    private static byte[] CreateMinimalTrueType(string familyName)
    {
        var nameBytes = Encoding.BigEndianUnicode.GetBytes(familyName);
        const int tableOffset = 28;
        var nameTableLength = 18 + nameBytes.Length;
        var data = new byte[tableOffset + nameTableLength];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 1);
        Encoding.ASCII.GetBytes("name").CopyTo(data, 12);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), (uint)nameTableLength);
        var table = data.AsSpan(tableOffset);
        BinaryPrimitives.WriteUInt16BigEndian(table, 0);
        BinaryPrimitives.WriteUInt16BigEndian(table[2..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(table[4..], 18);
        BinaryPrimitives.WriteUInt16BigEndian(table[6..], 3);
        BinaryPrimitives.WriteUInt16BigEndian(table[8..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(table[10..], 0x0409);
        BinaryPrimitives.WriteUInt16BigEndian(table[12..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(table[14..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(table[16..], 0);
        nameBytes.CopyTo(table[18..]);
        return data;
    }

    private sealed class TemporaryBrandStorage : IDisposable
    {
        public TemporaryBrandStorage()
        {
            Root = Path.Combine(Path.GetTempPath(), "KlippBrandKitTests", Guid.NewGuid().ToString("N"));
            AssetsRoot = Path.Combine(Root, "assets");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string AssetsRoot { get; }

        public EditorBrandKitService CreateService() => new(
            Path.Combine(Root, "brand-kits.json"),
            AssetsRoot);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
