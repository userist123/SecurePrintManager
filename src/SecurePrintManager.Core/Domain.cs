namespace SecurePrintManager.Core;

public enum PrincipalType { User, Administrator, Service }
public enum JobState { Created, Authenticated, Authorized, QuotaReserved, Secured, Submitted, Printing, Completed, Failed, Cancelled, RecoveryRequired }
public enum AuthMethod { Username, Pin, Card }
public enum Permission { Print, Scan, ManagePrinters, ManageUsers, ViewAudit, ManageQuotas }

public sealed record Principal(Guid Id, string Name, PrincipalType Type);

public sealed class PrintJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required string PrinterName { get; init; }
    public required string DocumentName { get; init; }
    public int Pages { get; init; }
    public JobState State { get; private set; } = JobState.Created;

    private static readonly IReadOnlyDictionary<JobState, HashSet<JobState>> Allowed = new Dictionary<JobState, HashSet<JobState>>
    {
        [JobState.Created] = [JobState.Authenticated, JobState.Cancelled, JobState.Failed],
        [JobState.Authenticated] = [JobState.Authorized, JobState.Cancelled, JobState.Failed],
        [JobState.Authorized] = [JobState.QuotaReserved, JobState.Cancelled, JobState.Failed],
        [JobState.QuotaReserved] = [JobState.Secured, JobState.Cancelled, JobState.Failed],
        [JobState.Secured] = [JobState.Submitted, JobState.Cancelled, JobState.Failed],
        [JobState.Submitted] = [JobState.Printing, JobState.Failed, JobState.RecoveryRequired],
        [JobState.Printing] = [JobState.Completed, JobState.Failed, JobState.RecoveryRequired],
        [JobState.RecoveryRequired] = [JobState.Submitted, JobState.Cancelled, JobState.Failed],
        [JobState.Completed] = [], [JobState.Failed] = [], [JobState.Cancelled] = []
    };

    public void TransitionTo(JobState next)
    {
        if (!Allowed[State].Contains(next)) throw new InvalidOperationException($"{State} -> {next} is not allowed.");
        State = next;
    }
}
