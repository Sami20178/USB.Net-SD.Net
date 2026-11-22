using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace USB.Net.Core.Wireless;

public static class WirelessFileTransfer
{
    private static readonly byte[] Magic = "UNETW001"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ChunkSize = 64 * 1024;
    private const int MaxMetadataSize = 16 * 1024;
    public const long DefaultMaximumFileSizeBytes = 4L * 1024 * 1024 * 1024;
    private sealed record Metadata(string FileName, long SizeBytes, string Sha256);

    public static string CreateInviteCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static async Task<WirelessTransferReceipt> ReceiveOneFileAsync(
        IPAddress listenAddress, int port, string inviteCode, string destinationDirectory,
        long maximumFileSizeBytes = DefaultMaximumFileSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listenAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ValidatePort(port);
        ValidateMaximumSize(maximumFileSizeBytes);
        var key = DeriveKey(inviteCode);
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        var listener = new TcpListener(listenAddress, port);
        listener.Start(1);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var magic = new byte[Magic.Length];
            await stream.ReadExactlyAsync(magic, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(magic, Magic))
                throw new IOException("Unsupported USB.Net transfer protocol.");

            var metadataBytes = await ReadFrameAsync(stream, key, MaxMetadataSize, cancellationToken);
            var metadata = JsonSerializer.Deserialize<Metadata>(metadataBytes)
                ?? throw new IOException("Invalid transfer metadata.");
            ValidateMetadata(metadata, maximumFileSizeBytes);
            var finalPath = Path.Combine(root, metadata.FileName);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
                throw new IOException("A file with that name already exists; it will not be overwritten.");

            var temporaryPath = Path.Combine(root, ".usbnet-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                await using (var output = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (received < metadata.SizeBytes)
                    {
                        var remaining = metadata.SizeBytes - received;
                        var chunk = await ReadFrameAsync(
                            stream, key, (int)Math.Min(ChunkSize, remaining), cancellationToken);
                        if (chunk.Length == 0 || chunk.Length > remaining)
                            throw new IOException("Invalid file chunk.");
                        hasher.AppendData(chunk);
                        await output.WriteAsync(chunk, cancellationToken);
                        received += chunk.Length;
                    }
                    await output.FlushAsync(cancellationToken);
                }

                var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(hash, metadata.Sha256, StringComparison.Ordinal))
                    throw new IOException("SHA-256 verification failed; received file rejected.");

                File.Move(temporaryPath, finalPath);
                await WriteFrameAsync(stream, key, "OK"u8.ToArray(), cancellationToken);
                return new WirelessTransferReceipt
                {
                    FileName = metadata.FileName, FilePath = finalPath,
                    SizeBytes = received, Sha256 = hash
                };
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            listener.Stop();
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task<WirelessTransferReceipt> SendFileAsync(
        string host, int port, string inviteCode, string sourceFilePath,
        long maximumFileSizeBytes = DefaultMaximumFileSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ValidatePort(port);
        ValidateMaximumSize(maximumFileSizeBytes);
        var key = DeriveKey(inviteCode);
        try
        {
            var sourcePath = Path.GetFullPath(sourceFilePath);
            var info = new FileInfo(sourcePath);
            if (!info.Exists) throw new FileNotFoundException("Source file not found.", sourcePath);
            if (info.Length > maximumFileSizeBytes)
                throw new IOException("File exceeds the configured transfer limit.");

            await using var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken))
                .ToLowerInvariant();
            input.Position = 0;
            var metadata = new Metadata(Path.GetFileName(sourcePath), info.Length, hash);
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
            if (metadataBytes.Length > MaxMetadataSize) throw new IOException("Metadata too large.");

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            client.NoDelay = true;
            await using var stream = client.GetStream();
            await stream.WriteAsync(Magic, cancellationToken);
            await WriteFrameAsync(stream, key, metadataBytes, cancellationToken);

            var buffer = new byte[ChunkSize];
            long sent = 0;
            while (sent < info.Length)
            {
                var count = (int)Math.Min(buffer.Length, info.Length - sent);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                await WriteFrameAsync(stream, key, buffer.AsMemory(0, count), cancellationToken);
                sent += count;
            }

            var ack = await ReadFrameAsync(stream, key, 16, cancellationToken);
            if (!ack.AsSpan().SequenceEqual("OK"u8))
                throw new IOException("Receiver did not confirm the transfer.");
            return new WirelessTransferReceipt
            {
                FileName = metadata.FileName, FilePath = sourcePath,
                SizeBytes = sent, Sha256 = hash
            };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task WriteFrameAsync(
        Stream stream, byte[] key, ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext.Span, cipher, tag);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, cipher.Length);
        await stream.WriteAsync(nonce, ct);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(cipher, ct);
        await stream.WriteAsync(tag, ct);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream, byte[] key, int maxLength, CancellationToken ct)
    {
        var nonce = new byte[NonceSize];
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(nonce, ct);
        await stream.ReadExactlyAsync(lengthBytes, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length < 0 || length > maxLength) throw new IOException("Invalid encrypted frame length.");
        var cipher = new byte[length];
        var tag = new byte[TagSize];
        await stream.ReadExactlyAsync(cipher, ct);
        await stream.ReadExactlyAsync(tag, ct);
        var plaintext = new byte[length];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plaintext);
        return plaintext;
    }

    private static byte[] DeriveKey(string inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode) || inviteCode.Length != 64)
            throw new ArgumentException("Invite code must be 64 hexadecimal characters.", nameof(inviteCode));
        byte[] raw;
        try { raw = Convert.FromHexString(inviteCode); }
        catch (FormatException ex) { throw new ArgumentException("Invalid invite code.", nameof(inviteCode), ex); }
        var key = SHA256.HashData(raw);
        CryptographicOperations.ZeroMemory(raw);
        return key;
    }

    private static void ValidateMetadata(Metadata metadata, long maxSize)
    {
        if (string.IsNullOrWhiteSpace(metadata.FileName) || metadata.FileName is "." or ".." ||
            metadata.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            metadata.FileName.Contains('/') || metadata.FileName.Contains('\\') ||
            !string.Equals(Path.GetFileName(metadata.FileName), metadata.FileName, StringComparison.Ordinal))
            throw new IOException("Unsafe file name received.");
        if (metadata.SizeBytes < 0 || metadata.SizeBytes > maxSize)
            throw new IOException("Invalid file size received.");
        if (metadata.Sha256.Length != 64 || !metadata.Sha256.All(Uri.IsHexDigit))
            throw new IOException("Invalid SHA-256 value received.");
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }
    private static void ValidateMaximumSize(long size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
    }
}
