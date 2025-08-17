using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using MichelOliveira.Com.ReactiveLock.Core;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

public class PaymentBatchInserterService
{
    private ConcurrentQueue<PaymentInsertParameters> Buffer { get; } = new();

    private IDbConnection DbConnection { get; }
    public DefaultOptions Options { get; }
    private IReactiveLockTrackerController ReactiveLockTrackerController { get; }
    private IReactiveLockTrackerState ReactiveLockTrackerState { get; }

    public PaymentBatchInserterService(IDbConnection dbConnection,
    IReactiveLockTrackerFactory reactiveLockTrackerFactory,
    IOptions<DefaultOptions> options)
    {
        DbConnection = dbConnection ?? throw new ArgumentNullException(nameof(dbConnection));
        Options = options.Value;
        ReactiveLockTrackerController = reactiveLockTrackerFactory.GetTrackerController(Constant.REACTIVELOCK_POSTGRES_NAME);
        ReactiveLockTrackerState = reactiveLockTrackerFactory.GetTrackerState(Constant.REACTIVELOCK_POSTGRES_NAME);
    }

    public async Task<int> AddAsync(PaymentInsertParameters payment)
    {
        await ReactiveLockTrackerController.IncrementAsync().ConfigureAwait(false);
        Buffer.Enqueue(payment);

        if (Buffer.Count >= Options.BATCH_SIZE)
        {
            return await FlushBatchAsync().ConfigureAwait(false);
        }
        return 0;
    }
    
    public async Task<int> FlushBatchAsync()
    {
        if (Buffer.IsEmpty)
            return 0;

        int totalInserted = 0;

        if (DbConnection is not NpgsqlConnection npgsqlConn)
            throw new InvalidOperationException("DbConnection must be an NpgsqlConnection.");

        string connectionString = npgsqlConn.ConnectionString;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        while (!Buffer.IsEmpty)
        {
            var batch = new List<PaymentInsertParameters>(Options.BATCH_SIZE);
            while (batch.Count < Options.BATCH_SIZE && Buffer.TryDequeue(out var item))
                batch.Add(item);

            if (batch.Count == 0)
                break;

            var batchStopwatch = Stopwatch.StartNew();

            var sqlBuilder = new System.Text.StringBuilder();
            var parameters = new DynamicParameters();

            sqlBuilder.AppendLine("INSERT INTO payments (correlation_id, processor, amount, requested_at) VALUES");

            for (int i = 0; i < batch.Count; i++)
            {
                var p = batch[i];
                var suffix = i.ToString();

                sqlBuilder.AppendLine($"(@CorrelationId{suffix}, @Processor{suffix}, @Amount{suffix}, @RequestedAt{suffix}){(i < batch.Count - 1 ? "," : ";")}");

                parameters.Add($"CorrelationId{suffix}", p.CorrelationId);
                parameters.Add($"Processor{suffix}", p.Processor);
                parameters.Add($"Amount{suffix}", p.Amount);
                parameters.Add($"RequestedAt{suffix}", p.RequestedAt);
            }

            string finalSql = sqlBuilder.ToString();

            await conn.ExecuteAsync(finalSql, parameters).ConfigureAwait(false);

            batchStopwatch.Stop();

            totalInserted += batch.Count;

            await ReactiveLockTrackerController.DecrementAsync(batch.Count).ConfigureAwait(false);
        }

        return totalInserted;
    }

}
