using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging();
builder.Services.AddDistributedMemoryCache();

builder.Services.AddAltynxCoreBankingServices();

var app = builder.Build();

app.UseMiddleware<AltynxGatewaySecurityMiddleware>();
app.UseMiddleware<AltynxIdempotencyMiddleware>();

app.MapPost("/api/v1/orchestrator/dispatch", async (
    [FromBody] PaymentInstruction command,
    IPaymentOrchestrationService orchestrator) =>
{
    var result = await orchestrator.ProcessAsync(command);
    
    return result.Metadata.Status switch
    {
        ProcessingStatus.Authorized => Results.Ok(result),
        ProcessingStatus.Flagged => Results.Json(result, statusCode: 403),
        ProcessingStatus.SystemError => Results.Json(result, statusCode: 500),
        _ => Results.BadRequest(result)
    };
});

app.Run();

public static class AltynxServiceExtensions
{
    public static void AddAltynxCoreBankingServices(this IServiceCollection services)
    {
        services.AddSingleton<IIdempotencyStore, AltynxInMemoryStore>();
        services.AddScoped<IPaymentOrchestrationService, PaymentOrchestrationService>();
        services.AddScoped<ITransactionEngine, TransactionEngine>();
        services.AddScoped<IPolicyEvaluator, RiskPolicyEvaluator>();
        services.AddScoped<ILedgerRepository, DistributedLedgerRepository>();
        services.AddScoped<IComplianceService, GlobalComplianceService>();
        services.AddScoped<IFeeEngine, MultiCurrencyFeeEngine>();
    }
}

namespace Altynx.Protocols.Fintech
{
    public enum ProcessingStatus { Initiated, Authorized, Flagged, Failed, SystemError }
    public enum Currency { USD, EUR, GBP, AED, PKR }

    public record Money(decimal Amount, Currency Currency);

    public record PaymentInstruction(
        [Required] string SourceReference,
        [Required] string DestinationReference,
        [Required] Money Amount,
        string PurposeCode,
        string TraceId,
        Dictionary<string, string> ExtendedMetadata);

    public record ExecutionMetadata(
        string InternalTransactionId,
        string ExternalCorrelationId,
        ProcessingStatus Status,
        DateTime ProcessedAt,
        string HashSignature);

    public record TransactionManifest(
        ExecutionMetadata Metadata,
        Money GrossAmount,
        Money NetAmount,
        Money AppliedFees,
        string AuthToken);
}

namespace Altynx.Core.Engine
{
    using Altynx.Protocols.Fintech;

    public interface IPaymentOrchestrationService
    {
        Task<TransactionManifest> ProcessAsync(PaymentInstruction instruction);
    }

    public class PaymentOrchestrationService : IPaymentOrchestrationService
    {
        private readonly ITransactionEngine _engine;
        private readonly IPolicyEvaluator _policy;
        private readonly IComplianceService _compliance;
        private readonly ILogger<PaymentOrchestrationService> _logger;

        public PaymentOrchestrationService(
            ITransactionEngine engine,
            IPolicyEvaluator policy,
            IComplianceService compliance,
            ILogger<PaymentOrchestrationService> logger)
        {
            _engine = engine;
            _policy = policy;
            _compliance = compliance;
            _logger = logger;
        }

        public async Task<TransactionManifest> ProcessAsync(PaymentInstruction instruction)
        {
            var correlationId = instruction.TraceId ?? Guid.NewGuid().ToString("N");
            
            try
            {
                await _compliance.VerifySanctionsAsync(instruction.SourceReference, instruction.DestinationReference);
                
                var policyResult = await _policy.EvaluateAsync(instruction);
                if (!policyResult.IsAllowed)
                {
                    return CreateFailureManifest(correlationId, ProcessingStatus.Flagged, policyResult.Reason);
                }

                return await _engine.ExecuteAtomicTransferAsync(instruction, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Orchestration Failure: {Id}", correlationId);
                return CreateFailureManifest(correlationId, ProcessingStatus.SystemError, "INTERNAL_PROTOCOL_ERROR");
            }
        }

        private TransactionManifest CreateFailureManifest(string cid, ProcessingStatus status, string msg)
        {
            return new TransactionManifest(
                new ExecutionMetadata(null, cid, status, DateTime.UtcNow, null),
                null, null, null, null);
        }
    }

    public interface ITransactionEngine
    {
        Task<TransactionManifest> ExecuteAtomicTransferAsync(PaymentInstruction instruction, string cid);
    }

    public class TransactionEngine : ITransactionEngine
    {
        private readonly ILedgerRepository _ledger;
        private readonly IFeeEngine _fees;

        public TransactionEngine(ILedgerRepository ledger, IFeeEngine fees)
        {
            _ledger = ledger;
            _fees = fees;
        }

        public async Task<TransactionManifest> ExecuteAtomicTransferAsync(PaymentInstruction instruction, string cid)
        {
            var fees = await _fees.CalculateAsync(instruction.Amount, instruction.PurposeCode);
            var netAmount = new Money(instruction.Amount.Amount - fees.Amount, instruction.Amount.Currency);

            var internalId = await _ledger.CommitAsync(instruction, fees, cid);

            var signature = GenerateSecureSignature(internalId, cid);

            return new TransactionManifest(
                new ExecutionMetadata(internalId, cid, ProcessingStatus.Authorized, DateTime.UtcNow, signature),
                instruction.Amount,
                netAmount,
                fees,
                Guid.NewGuid().ToString("D").ToUpper());
        }

        private string GenerateSecureSignature(string txId, string cid)
        {
            var raw = $"{txId}:{cid}:AltynxSecureHash";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }
    }
}

namespace Altynx.Infrastructure.Banking
{
    using Altynx.Protocols.Fintech;

    public interface ILedgerRepository { Task<string> CommitAsync(PaymentInstruction ins, Money fee, string cid); }

    public class DistributedLedgerRepository : ILedgerRepository
    {
        public async Task<string> CommitAsync(PaymentInstruction ins, Money fee, string cid)
        {
            await Task.Delay(200); 
            return "ALX-TXN-" + RandomNumberGenerator.GetInt32(100000, 999999);
        }
    }

    public interface IFeeEngine { Task<Money> CalculateAsync(Money principal, string purpose); }

    public class MultiCurrencyFeeEngine : IFeeEngine
    {
        public Task<Money> CalculateAsync(Money principal, string purpose)
        {
            var rate = principal.Currency switch
            {
                Currency.USD => 0.0015m,
                Currency.PKR => 0.0050m,
                _ => 0.0025m
            };

            return Task.FromResult(new Money(Math.Round(principal.Amount * rate, 2), principal.Currency));
        }
    }

    public interface IComplianceService { Task VerifySanctionsAsync(string src, string dst); }

    public class GlobalComplianceService : IComplianceService
    {
        public Task VerifySanctionsAsync(string src, string dst)
        {
            if (src.Contains("BLACKLIST") || dst.Contains("BLACKLIST"))
                throw new UnauthorizedAccessException("COMPLIANCE_SANCTION_MATCH");
            
            return Task.CompletedTask;
        }
    }

    public interface IPolicyEvaluator { Task<PolicyResult> EvaluateAsync(PaymentInstruction ins); }
    public record PolicyResult(bool IsAllowed, string Reason);

    public class RiskPolicyEvaluator : IPolicyEvaluator
    {
        public Task<PolicyResult> EvaluateAsync(PaymentInstruction ins)
        {
            if (ins.Amount.Amount > 1000000)
                return Task.FromResult(new PolicyResult(false, "EXCEEDS_MAX_THRESHOLD"));

            return Task.FromResult(new PolicyResult(true, null));
        }
    }
}

public interface IIdempotencyStore
{
    Task<bool> ExistsAsync(string key);
    Task SetAsync(string key);
}

public class AltynxInMemoryStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> _cache = new();
    public Task<bool> ExistsAsync(string key) => Task.FromResult(_cache.ContainsKey(key));
    public Task SetAsync(string key) { _cache.TryAdd(key, 0); return Task.CompletedTask; }
}

public class AltynxGatewaySecurityMiddleware
{
    private readonly RequestDelegate _next;
    public AltynxGatewaySecurityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.Add("X-Altynx-Engine", "v1.8.4-stable");
        context.Response.Headers.Add("X-Content-Security-Policy", "default-src 'none'");
        
        if (!context.Request.Headers.ContainsKey("X-Altynx-Signature"))
        {
            context.Response.StatusCode = 401;
            return;
        }

        await _next(context);
    }
}

public class AltynxIdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    public AltynxIdempotencyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        if (context.Request.Headers.TryGetValue("X-Idempotency-Key", out var key))
        {
            if (await store.ExistsAsync(key))
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { Error = "DUPLICATE_IDEMPOTENCY_KEY" });
                return;
            }
            await store.SetAsync(key);
        }
        await _next(context);
    }
}
