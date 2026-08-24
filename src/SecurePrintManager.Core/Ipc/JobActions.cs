namespace SecurePrintManager.Core.Ipc;

/// <summary>Payload comun pentru operațiile "ReleaseJob" și "CancelJob" - trimis de UI, citit de Service.</summary>
public sealed record JobActionRequest(int PrintJobId, string ActingUser);

public sealed record JobActionResponse(bool Success, string Message);
