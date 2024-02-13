using JetBrains.Annotations;

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Reddoxx.Quartz.MongoDbJobStore.Models;

internal enum LockType
{
    /// <summary>
    /// Use TRIGGER_ACCESS database locking
    /// </summary>
    TriggerAccess,

    StateAccess,
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal class Lock
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>
    /// SCHED_NAME
    /// </summary>
    [BsonRequired]
    public required string InstanceName { get; init; }

    /// <summary>
    /// LOCK_NAME
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public LockType LockType { get; set; }

    /// <summary>
    /// Random lock key which is set when acquiring the lock with findOneAndUpdate.
    /// </summary>
    public ObjectId LockKey { get; set; }
}
