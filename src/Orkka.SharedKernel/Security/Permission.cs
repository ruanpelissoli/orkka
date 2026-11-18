namespace Orkka.SharedKernel.Security;

/// <summary>
/// Fine-grained member permissions. Persisted by name (string conversion) in the
/// organizations schema — never rename members, only add new ones.
/// Owners bypass permission checks entirely; reads are open to all org members.
/// </summary>
public enum Permission
{
    TasksCreate,
    TasksEdit,
    TasksDelete,
    TasksExecute,
    PlansReview,
    CodeReviewReview,
    CodeReviewMerge,
    DocumentsManage,
    AgentsChat,
    AgentsConfigure,
    DesignReview,
    ProjectsCreate,
    ProjectsManage,
    ProjectsDelete,
    ReposManage,
    BillingManage,
    AuditView,
}
