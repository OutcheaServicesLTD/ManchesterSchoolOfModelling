namespace Msm.Portfolio.Web.Domain.Enums;

/// <summary>
/// Where a suggested biography has got to.
/// </summary>
/// <remarks>
/// A draft is never the published biography. It is a suggestion an administrator reads,
/// edits and accepts — or throws away. The public page shows what a person approved, not
/// what a model wrote unattended, because this text describes a real individual and is
/// what an agency reads about them.
/// </remarks>
public enum BiographyDraftStatus
{
    /// <summary>Nothing has been asked for. The starting state, and the end state for
    /// anyone who already had a biography written.</summary>
    NotRequested = 0,

    /// <summary>Asked for at approval, waiting on the worker.</summary>
    Pending = 1,

    /// <summary>Written and waiting for an administrator to read it.</summary>
    Ready = 2,

    /// <summary>Could not be written. The reason is kept so it is not a silent nothing.</summary>
    Failed = 3,

    /// <summary>An administrator used it, or threw it away. Either way it is finished
    /// with, and this is what stops a second one ever being asked for.</summary>
    Closed = 4
}
