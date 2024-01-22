using MongoDB.Bson.Serialization.Attributes;

using Quartz.Spi.MongoJobStore.Models.Id;

namespace Quartz.Spi.MongoJobStore.Models;

internal enum LockType
{
    TriggerAccess,
    StateAccess,
}

internal class Lock
{
    public const string TriggerAccess = "TRIGGER_ACCESS";
    public const string StateAccess = "STATE_ACCESS";

    [BsonId]
    public LockId Id { get; set; }

    public string InstanceId { get; set; }

    public DateTime AcquiredAt { get; set; }
}
