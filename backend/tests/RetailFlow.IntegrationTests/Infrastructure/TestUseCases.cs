using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>
/// A stand-in business operation used to verify the use-case pipeline itself — idempotency,
/// transactions and rollback — before the real operations exist. It writes a branch so a test can
/// check that the business change and the idempotency record share one fate.
/// </summary>
public sealed record CreateTestBranchRequest(string IdempotencyKey, string Code, bool FailAfterWrite = false)
    : IIdempotentRequest
{
    public int SuccessStatusCode => 201;
}

public sealed record CreateTestBranchResponse(Guid Id, string Code, int InvocationNumber);

/// <summary>Counts handler invocations so a test can prove a replay did not run the work twice.</summary>
public sealed class InvocationCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public int Next() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}

public sealed class CreateTestBranchHandler : IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>
{
    private readonly RetailFlowDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly IGuidGenerator _guids;
    private readonly InvocationCounter _counter;

    public CreateTestBranchHandler(
        RetailFlowDbContext context,
        ITenantContext tenant,
        IGuidGenerator guids,
        InvocationCounter counter)
    {
        _context = context;
        _tenant = tenant;
        _guids = guids;
        _counter = counter;
    }

    public async Task<Result<CreateTestBranchResponse>> HandleAsync(
        CreateTestBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        var invocation = _counter.Next();

        var branch = Branch.Create(_guids.NewId(), _tenant.OrganizationId, request.Code, "Test " + request.Code);
        _context.Branches.Add(branch);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (request.FailAfterWrite)
        {
            // A business rejection after the write: the transaction must roll back the branch and
            // the idempotency reservation together, leaving the key reusable.
            return Error.Conflict("test.rejected_after_write", "Rejected on purpose.");
        }

        return new CreateTestBranchResponse(branch.Id, branch.Code, invocation);
    }
}

internal static class TestUseCases
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<InvocationCounter>();
        services.AddSingleton<TestEventSink>();
        services.AddScoped<IOutboxMessageHandler, TestOutboxHandler>();
        services.AddUseCase<CreateTestBranchRequest, CreateTestBranchResponse, CreateTestBranchHandler>();
    }
}
