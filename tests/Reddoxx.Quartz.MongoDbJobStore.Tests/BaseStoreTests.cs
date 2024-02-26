using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Quartz;

using Reddoxx.Quartz.MongoDbJobStore.Database;
using Reddoxx.Quartz.MongoDbJobStore.Extensions;
using Reddoxx.Quartz.MongoDbJobStore.Locking;
using Reddoxx.Quartz.MongoDbJobStore.ServiceStackRedis;
using Reddoxx.Quartz.MongoDbJobStore.Tests.Options;
using Reddoxx.Quartz.MongoDbJobStore.Tests.Persistence;

using ServiceStack.Redis;

namespace Reddoxx.Quartz.MongoDbJobStore.Tests;

public abstract class BaseStoreTests
{
    public const string Barrier = "BARRIER";
    public const string DateStamps = "DATE_STAMPS";
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(125);

    protected static async Task<IScheduler> CreateScheduler(string instanceName = "QUARTZ_TEST", bool clustered = true)
    {
        var sp = CreateServiceProvider(instanceName, clustered);

        var schedulerFactory = sp.GetRequiredService<ISchedulerFactory>();
        return await schedulerFactory.GetScheduler();
    }

    private static ServiceProvider CreateServiceProvider(string instanceName, bool clustered)
    {
        var configuration = new ConfigurationBuilder()
            // 
            .AddUserSecrets(Assembly.GetExecutingAssembly(), true)
            .Build();


        var services = new ServiceCollection();
        services.AddLogging(builder => { builder.AddDebug(); });

        services.AddSingleton<IConfiguration>(configuration);

        services.AddOptions<MongoDbOptions>().Bind(configuration.GetSection("MongoDb"));
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection("Redis"));


        // ServiceStack 
        services.AddSingleton<IRedisClientsManager>(
            sp =>
            {
                var redisOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisOptions>>().Value;

                return new RedisManagerPool(redisOptions.ConnectionString);
            }
        );
        services.AddSingleton(sp => (IRedisClientsManagerAsync)sp.GetRequiredService<IRedisClientsManager>());

        services.AddSingleton<IQuartzJobStoreLockingManager, ServiceStackRedisQuartzLockingManager>();

        // ^--

        services.AddSingleton<IQuartzMongoDbJobStoreFactory, LocalQuartzMongoDbJobStoreFactory>();
        services.AddQuartz(
            q =>
            {
                q.SchedulerId = "AUTO";
                q.SchedulerName = instanceName;
                q.MaxBatchSize = 10;

                q.UsePersistentStore<MongoDbJobStore>(
                    storage =>
                    {
                        if (clustered)
                        {
                            storage.UseClustering(
                                c =>
                                {
                                    c.CheckinInterval = TimeSpan.FromSeconds(10);
                                    c.CheckinMisfireThreshold = TimeSpan.FromSeconds(15);
                                }
                            );
                        }

                        storage.UseNewtonsoftJsonSerializer();

                        storage.ConfigureMongoDb(
                            c =>
                            {
                                //
                                c.CollectionPrefix = "HelloWorld";
                            }
                        );
                    }
                );
            }
        );


        return services.BuildServiceProvider();
    }
}
