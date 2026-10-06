using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Services.ArtifactStorage.FileSystem;

[JsonSerializable(typeof(FileSystemArtifactStore.MetadataDocument))]
internal sealed partial class FileSystemJsonContext : JsonSerializerContext;
