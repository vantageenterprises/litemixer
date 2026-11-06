using System.Buffers;
using System.IO.Compression;
using System.Text;

// Packs a folder into the installer payload: one Brotli stream over
//   "LMIX1", long totalBytes, int fileCount, then per file: ushort pathLength, UTF-8 path, long size, bytes.
// Compressing everything as one stream with the largest window shrinks the many similar .NET files a lot.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Pack <folder> <output>");
    return 1;
}

var root = Path.GetFullPath(args[0]);
var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
Array.Sort(files, StringComparer.OrdinalIgnoreCase);
long total = files.Sum(f => new FileInfo(f).Length);

using var output = File.Create(args[1]);
using var encoder = new BrotliEncoder(quality: 11, window: 24);
var compressed = new byte[1 << 20];

void Write(ReadOnlySpan<byte> data, bool final = false)
{
    while (true)
    {
        var status = encoder.Compress(data, compressed, out int consumed, out int written, final);
        output.Write(compressed, 0, written);
        data = data[consumed..];
        if (status == OperationStatus.Done) return;
        if (status == OperationStatus.NeedMoreData && data.IsEmpty) return;
        if (status == OperationStatus.InvalidData) throw new InvalidDataException("Brotli encoder failed");
    }
}

var header = new MemoryStream();
var writer = new BinaryWriter(header);
writer.Write("LMIX1"u8);
writer.Write(total);
writer.Write(files.Length);
foreach (var file in files)
{
    var path = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file));
    writer.Write((ushort)path.Length);
    writer.Write(path);
    writer.Write(new FileInfo(file).Length);
    writer.Flush();
    Write(header.GetBuffer().AsSpan(0, (int)header.Length));
    header.SetLength(0);

    using var input = File.OpenRead(file);
    var buffer = new byte[1 << 20];
    int read;
    while ((read = input.Read(buffer)) > 0) Write(buffer.AsSpan(0, read));
}
Write(default, final: true);

Console.WriteLine($"{files.Length} files, {total / 1048576.0:0.0} MB -> {output.Length / 1048576.0:0.0} MB");
return 0;
