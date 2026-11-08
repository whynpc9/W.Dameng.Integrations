using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces;
using W.Dm;
using Xunit.Sdk;

namespace W.TickerQ.Dameng.Tests;

internal sealed class LiveDatabase(Isolation isolation)
{
    internal static async Task RunAsync(Func<LiveDatabase, Task> test)
    {
        var phase = "open isolated test database";
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(root.FullName, "scripts", "tickerq", "run.sh")))
                root = root.Parent ?? throw new InvalidOperationException("Worktree not found.");

            var path = Path.Combine(root.FullName, ".local", "tickerq");
            using var gate = new FileStream(Path.Combine(path, "test.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var isolation = Isolation.Read(Path.Combine(path, "isolation.json"), requireReady: true);
            isolation.Verify();
            var database = new LiveDatabase(isolation);

            phase = "migrate and reset the owned test tables";
            using var host = database.CreateNode("tickerq-test-bootstrap");
            await using var context = await host.Services
                .GetRequiredService<IDbContextFactory<DamengTickerQDbContext>>().CreateDbContextAsync();
            await context.Database.MigrateAsync();
            await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().ExecuteDeleteAsync();
            await context.Set<TimeTickerEntity>().ExecuteDeleteAsync();
            await context.Set<CronTickerEntity>().ExecuteDeleteAsync();

            phase = "exercise TickerQ";
            await test(database);
        }
        catch (Exception error) when (error is not XunitException)
        {
            var site = new StackTrace(error, fNeedFileInfo: true).GetFrames()
                .FirstOrDefault(frame => frame.GetFileName()?.Contains("W.TickerQ.Dameng.Tests", StringComparison.Ordinal) == true);
            throw new XunitException($"{phase}: {Isolation.Failure(error)} at {site?.GetMethod()?.Name}:{site?.GetFileLineNumber()}");
        }
    }

    internal IHost CreateNode(string node, ITickerClock? clock = null, bool runScheduler = false,
        DbCommandInterceptor? interceptor = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ExecutionProbe>();
        builder.Services.AddDamengTickerQ(isolation.ConnectionString, options =>
        {
            if (!runScheduler)
                options.DisableBackgroundServices();

            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = node;
                scheduler.FallbackIntervalChecker = TimeSpan.FromMilliseconds(100);
            });
        }, typeof(LiveDatabase).Assembly.GetName().Name);

        if (clock is not null)
            builder.Services.AddSingleton(clock);

        if (interceptor is not null)
            builder.Services.ConfigureDbContext<DamengTickerQDbContext>(options => options.AddInterceptors(interceptor));

        return builder.Build();
    }

    internal async Task<T> QueryAsync<T>(string sql, Func<DmDataReader, Task<T>> read, Guid? id = null)
    {
        await using var connection = new DmConnection(isolation.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is { } key)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = key.ToString("D");
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync();
        return await read((DmDataReader)reader);
    }

    internal Task<int> CountAsync(string sql)
        => QueryAsync(sql, async reader => await reader.ReadAsync()
            ? Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Count query returned no row."));

    internal string Tablespace => isolation.Tablespace;
}
