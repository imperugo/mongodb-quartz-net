using MongoDB.Driver;

using Quartz.Impl.Matchers;
using Quartz.Spi.MongoJobStore.Extensions;
using Quartz.Spi.MongoJobStore.Models;

namespace Quartz.Spi.MongoJobStore.Repositories;

internal class PausedTriggerGroupRepository : BaseRepository<PausedTriggerGroup>
{
    public PausedTriggerGroupRepository(IMongoDatabase database, string instanceName, string? collectionPrefix = null)
        : base(database, "pausedTriggerGroups", instanceName, collectionPrefix)
    {
    }

    public override async Task EnsureIndex()
    {
        await Collection.Indexes.CreateOneAsync(
            new CreateIndexModel<PausedTriggerGroup>(
                IndexBuilder.Combine(
                    //
                    IndexBuilder.Ascending(x => x.InstanceName),
                    IndexBuilder.Ascending(x => x.Group)
                ),
                new CreateIndexOptions
                {
                    Unique = true,
                }
            )
        );
    }


    /// <summary>
    /// Selects the paused trigger groups.
    /// </summary>
    /// <returns></returns>
    public async Task<List<string>> GetPausedTriggerGroups()
    {
        return await Collection
            //
            .Find(group => group.InstanceName == InstanceName)
            .Project(group => group.Group)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<bool> IsTriggerGroupPaused(string group)
    {
        var filter = FilterBuilder.Eq(x => x.InstanceName, InstanceName) & //
                     FilterBuilder.Eq(x => x.Group, group);

        return await Collection
            //
            .Find(filter)
            .AnyAsync()
            .ConfigureAwait(false);
    }

    public async Task AddPausedTriggerGroup(string group)
    {
        await Collection.InsertOneAsync(
                new PausedTriggerGroup
                {
                    InstanceName = InstanceName,
                    Group = group,
                }
            )
            .ConfigureAwait(false);
    }

    public async Task DeletePausedTriggerGroup(GroupMatcher<TriggerKey> matcher)
    {
        var regex = matcher.ToBsonRegularExpression().ToRegex();

        var filter = FilterBuilder.Eq(x => x.InstanceName, InstanceName) & //
                     FilterBuilder.Regex(x => x.Group, regex);

        await Collection.DeleteManyAsync(filter).ConfigureAwait(false);
    }

    public async Task DeletePausedTriggerGroup(string groupName)
    {
        var filter = FilterBuilder.Eq(x => x.InstanceName, InstanceName) & // 
                     FilterBuilder.Eq(x => x.Group, groupName);

        await Collection.DeleteOneAsync(filter).ConfigureAwait(false);
    }
}
