using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dapper;
using MichelOliveira.Com.ReactiveLock.Core;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;
using StackExchange.Redis;

public class PaymentService
{
    private IDbConnection Conn { get; }
    private ConsoleWriterService ConsoleWriterService { get; }
    private IConnectionMultiplexer Redis { get; }
    private PaymentBatchInserterService BatchInserter { get; }
    private IReactiveLockTrackerState ReactiveLockTrackerState { get; }
    private HttpClient HttpDefault { get; }

    public PaymentService(
        IHttpClientFactory factory,
        IConnectionMultiplexer redis,
        PaymentBatchInserterService batchInserter,
        IDbConnection conn,
        IReactiveLockTrackerFactory reactiveLockTrackerFactory,
        ConsoleWriterService consoleWriterService
    )
    {
        Conn = conn;
        ConsoleWriterService = consoleWriterService;
        Redis = redis;
        BatchInserter = batchInserter;
        ReactiveLockTrackerState = reactiveLockTrackerFactory.GetTrackerState(Constant.REACTIVELOCK_API_PAYMENTS_SUMMARY_NAME);

        HttpDefault = factory.CreateClient(Constant.DEFAULT_PROCESSOR_NAME);
    }


    public async Task<IResult> EnqueuePaymentAsync(HttpContext context)
    {
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        var rawBody = ms.ToArray();

        _ = Task.Run(async () =>
        {
            var db = Redis.GetDatabase();
            await db.ListRightPushAsync(Constant.REDIS_QUEUE_KEY, rawBody, flags: StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
         }).ConfigureAwait(false);

        return Results.Accepted();
    }


    public async Task<IResult> PurgePaymentsAsync()
    {
        const string sql = "TRUNCATE TABLE payments";
        await Conn.ExecuteAsync(sql).ConfigureAwait(false);
        return Results.Ok("Payments table truncated.");
    }

    private bool TryParseRequest(string message, [NotNullWhen(true)] out PaymentRequest? request)
    {
        request = null;
        var isValid = false;

        try
        {
            var parsed = JsonSerializer.Deserialize(message, JsonContext.Default.PaymentRequest);

            if (parsed != null &&
                parsed.Amount > 0 &&
                parsed.CorrelationId != Guid.Empty)
            {
                request = parsed;
                isValid = true;
            }
        }
        catch (Exception ex)
        {
            ConsoleWriterService.WriteLine($"Failed to deserialize or validate message: {ex.Message}");
        }

        return isValid;
    }

    public async Task ProcessPaymentAsync(string message)
    {
        if (!TryParseRequest(message, out var request))
        {
            return;
        }
        var requestedAt = DateTimeOffset.UtcNow;
        await ReactiveLockTrackerState.WaitIfBlockedAsync().ConfigureAwait(false);
        var response = await HttpDefault.PostAsJsonAsync("/payments", new ProcessorPaymentRequest
        (
            request.Amount,
            requestedAt,
            request.CorrelationId
        ), JsonContext.Default.ProcessorPaymentRequest).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var parameters = new PaymentInsertParameters(
                CorrelationId: request.CorrelationId,
                Processor: Constant.DEFAULT_PROCESSOR_NAME,
                Amount: request.Amount,
                RequestedAt: requestedAt
            );
            await BatchInserter.AddAsync(parameters).ConfigureAwait(false);
            return;
        }
        var statusCode = (int)response.StatusCode;

        if (statusCode >= 400 && statusCode < 500)
        {
            ConsoleWriterService.WriteLine($"Discarding message due to client error: {statusCode} {response.ReasonPhrase}");
            return;
        }
        var redisDb = Redis.GetDatabase();
        await redisDb.ListRightPushAsync(Constant.REDIS_QUEUE_KEY, message).ConfigureAwait(false);
    }   
}