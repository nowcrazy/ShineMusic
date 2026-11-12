// 职责：IAudioStorage 的本地磁盘实现。
// 复用已有的 LibraryStoragePaths / QuarantineStoragePaths 路径穿越防护，
// 不重写安全逻辑，只把两个实现收拢到同一个端口后面。

using ShineMusic.Model.Ports;
using ShineMusic.Repository.Data;

namespace ShineMusic.Repository.Storage;

/// <summary>单服务器部署下的本地磁盘音频存储。</summary>
public sealed class LocalAudioStorage(
    LibraryStoragePaths libraryPaths,
    QuarantineStoragePaths quarantinePaths) : IAudioStorage
{
    /// <inheritdoc />
    public string LibraryRoot => libraryPaths.Root;

    /// <inheritdoc />
    public string QuarantineRoot => quarantinePaths.Root;

    /// <inheritdoc />
    public bool TryResolveLibrary(string storageKey, out string filePath) =>
        libraryPaths.TryResolve(storageKey, out filePath);

    /// <inheritdoc />
    public bool TryResolveQuarantine(string storageKey, out string filePath) =>
        quarantinePaths.TryResolve(storageKey, out filePath);
}
