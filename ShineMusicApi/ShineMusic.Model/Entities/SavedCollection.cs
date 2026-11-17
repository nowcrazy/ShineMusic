using SqlSugar;

namespace ShineMusic.Model.Entities;

/// <summary>A user's saved public editorial collection; never a user playlist.
/// Discovered by SqlSugarSchemaBootstrap for additive creation in existing databases.
/// Visibility is enforced by the API's editorial collection queries.</summary>
[SugarTable("saved_collections")]
public sealed class SavedCollection
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid UserId { get; set; }

    [SugarColumn(IsPrimaryKey = true)]
    public Guid CollectionId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
