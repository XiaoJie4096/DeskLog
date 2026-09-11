using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Riji.Core;

namespace Riji.Infrastructure;

public sealed record ArchiveImage(string Name, long Length, string Sha256);
public sealed record ArchiveManifest(string Product, int Format, string DataSha256, ArchiveImage[] Images);
public sealed record PreparedArchive(PortableData Data, string ImageRoot, string FullImagePath);

public static class DataArchive
{
    private const long MaxJson = 64L * 1024 * 1024;
    private const long MaxImages = 2L * 1024 * 1024 * 1024;

    // Publish a complete archive atomically; failure never replaces an existing selected backup.
    public static void Export(LocalStore store, string dataDirectory, string destination)
    {
        var snapshot = store.ExportData(); snapshot.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.LongLength > MaxJson) throw new InvalidDataException("备份记录超过当前 64 MiB 限制，未截断导出。");
        var path = Path.GetFullPath(destination); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var imageDirectory = Path.Combine(Path.GetFullPath(dataDirectory), store.ImageRootName());
        List<ArchiveImage> images = []; long imageBytes = 0;
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("data.json", CompressionLevel.Optimal).Open()) stream.Write(bytes);
                foreach (var job in snapshot.Jobs)
                {
                    var imagePath = Path.Combine(imageDirectory, job.Image);
                    if (!File.Exists(imagePath)) continue;
                    using var input = File.OpenRead(imagePath);
                    imageBytes += input.Length;
                    if (input.Length > MaxJson || imageBytes > MaxImages || images.Count >= 10000) throw new InvalidDataException("截图备份超过当前大小或数量限制，未截断导出。");
                    var hash = Convert.ToHexString(SHA256.HashData(input)); input.Position = 0;
                    using (var output = zip.CreateEntry("images/" + job.Image, CompressionLevel.Fastest).Open()) input.CopyTo(output);
                    images.Add(new(job.Image, input.Length, hash));
                }
                var manifest = new ArchiveManifest("Riji", snapshot.Format, Convert.ToHexString(SHA256.HashData(bytes)), images.ToArray());
                using var metadata = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(metadata, manifest);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Extract only declared GUID filenames into a new generation, never into the current image tree.
    public static PreparedArchive Prepare(string archivePath, string dataDirectory)
    {
        var root = "Screenshots-" + Guid.NewGuid().ToString("N"); var target = Path.Combine(Path.GetFullPath(dataDirectory), root);
        Directory.CreateDirectory(target);
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count > 10002 || zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                throw new InvalidDataException("备份条目重复或过多。");
            byte[] Read(string name, long limit)
            {
                var entry = zip.GetEntry(name) ?? throw new InvalidDataException("备份缺少必要条目。");
                if (entry.Length > limit) throw new InvalidDataException("备份条目超过大小限制。");
                using var stream = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
                while ((count = stream.Read(buffer)) != 0) { if (output.Length + count > limit) throw new InvalidDataException("解压数据超过限制。"); output.Write(buffer, 0, count); }
                return output.ToArray();
            }
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(Read("manifest.json", 4 * 1024 * 1024)) ?? throw new InvalidDataException("备份清单无效。");
            if (manifest.Product != "Riji" || manifest.Format is not (1 or 2 or 3) || manifest.Images.Length > 10000) throw new InvalidDataException("不是支持的日迹备份。");
            var bytes = Read("data.json", MaxJson);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != manifest.DataSha256) throw new InvalidDataException("备份数据校验失败。");
            var data = JsonSerializer.Deserialize<PortableData>(bytes) ?? throw new InvalidDataException("备份记录无效。"); data.Validate();
            if (data.Format != manifest.Format) throw new InvalidDataException("备份清单与记录格式版本不一致。");
            var allowedImages = data.Jobs.Select(job => job.Image).ToHashSet(StringComparer.Ordinal);
            var entries = new HashSet<string>(StringComparer.Ordinal) { "manifest.json", "data.json" }; long total = 0;
            foreach (var image in manifest.Images)
            {
                if (!allowedImages.Contains(image.Name) || !entries.Add("images/" + image.Name) || image.Length < 0 || image.Length > MaxJson || (total += image.Length) > MaxImages)
                    throw new InvalidDataException("备份图片引用或大小无效。");
                var imageBytes = Read("images/" + image.Name, MaxJson);
                if (imageBytes.LongLength != image.Length || Convert.ToHexString(SHA256.HashData(imageBytes)) != image.Sha256) throw new InvalidDataException("截图校验失败。");
                File.WriteAllBytes(Path.Combine(target, image.Name), imageBytes);
            }
            if (zip.Entries.Any(entry => !entries.Contains(entry.FullName))) throw new InvalidDataException("备份包含未声明条目。");
            return new(data, root, target);
        }
        catch { DeleteGeneration(dataDirectory, root, null); throw; }
    }

    // Retire old generations only after the database atomically references the new one.
    public static string? CleanupRetired(LocalStore store, string dataDirectory)
    {
        var pending = store.Read<string[]>("retired-image-roots") ?? []; List<string> remaining = [];
        if (pending.Length == 0) return null;
        foreach (var root in pending)
            try { DeleteGeneration(dataDirectory, root, store.ImageRootName()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { remaining.Add(root); }
        try { store.SaveValue("retired-image-roots", remaining.ToArray()); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return "记录已处理，但截图清理进度未能保存；重启后会复核。"; }
        return remaining.Count == 0 ? null : "记录已处理，但部分旧截图清理失败；重启后会继续尝试。";
    }

    public static void DeleteGeneration(string directory, string name, string? active)
    {
        if (name == active || name != "Screenshots" && (!name.StartsWith("Screenshots-", StringComparison.Ordinal) || !Guid.TryParseExact(name[12..], "N", out _))) throw new ArgumentException("拒绝清理无效截图目录。");
        var root = Path.GetFullPath(directory); var target = Path.GetFullPath(Path.Combine(root, name));
        if (!Path.GetDirectoryName(target)!.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("截图目录超出数据范围。");
        if (!Directory.Exists(target)) return;
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(target).Any()) throw new IOException("截图目录含非预期链接或子目录。");
        Directory.Delete(target, recursive: true);
    }
}
