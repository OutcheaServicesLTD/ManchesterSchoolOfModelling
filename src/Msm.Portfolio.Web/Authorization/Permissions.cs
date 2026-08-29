namespace Msm.Portfolio.Web.Authorization;

/// <summary>
/// Individual capabilities, granted as claims on a role rather than inferred from the
/// role name (specification sections 4, 5 and 35).
/// </summary>
/// <remarks>
/// The specification is explicit that permissions must not reduce to a single
/// unrestricted Admin flag, because MSM expects several staff accounts holding
/// different privileges. Granting capabilities as claims means one Admin can be given
/// permanent deletion rights while another is not, without introducing a new role.
/// </remarks>
public static class Permissions
{
    /// <summary>Claim type carrying a permission value on a role or user.</summary>
    public const string ClaimType = "permission";

    public static class Clients
    {
        public const string ViewAll = "clients.view.all";
        public const string ViewAssigned = "clients.view.assigned";
        public const string Create = "clients.create";
        public const string Edit = "clients.edit";
        public const string ViewOwn = "clients.view.own";
        public const string EditOwn = "clients.edit.own";
    }

    public static class Media
    {
        /// <summary>See the full private pool, which is larger than the public portfolio.</summary>
        public const string ViewPool = "media.pool.view";
        public const string Upload = "media.upload";
        public const string Delete = "media.delete";

        /// <summary>Choose which pool images appear on the portfolio, and in what order.</summary>
        public const string Select = "media.select";
        public const string SetFeatured = "media.featured.set";
        public const string UploadOwn = "media.upload.own";
        public const string SelectOwn = "media.select.own";
    }

    public static class Portfolios
    {
        public const string View = "portfolio.view";
        public const string Edit = "portfolio.edit";
        public const string EditOwn = "portfolio.edit.own";
        public const string SubmitForReview = "portfolio.submit";
        public const string ChangeStatus = "portfolio.status.change";
        public const string Publish = "portfolio.publish";
        public const string Unpublish = "portfolio.unpublish";
        public const string Archive = "portfolio.archive";

        /// <summary>Super Admin only: destroys the record rather than archiving it.</summary>
        public const string DeletePermanently = "portfolio.delete.permanent";

        /// <summary>Super Admin only: brings an archived portfolio back.</summary>
        public const string Restore = "portfolio.restore";

        /// <summary>
        /// Super Admin only: retypes the web address by hand. Editing the photographs,
        /// biography and other portfolio content stays under <see cref="Edit"/> — this is
        /// narrower and covers only the slug, because a link already shared with an
        /// agency breaks the moment it changes (specification section 39).
        /// </summary>
        public const string ChangeSlug = "portfolio.slug.change";
    }

    public static class Payments
    {
        public const string View = "payments.view";
        public const string StartCheckout = "payments.checkout.start";
        public const string MarkNoSale = "payments.nosale.mark";
        public const string Review = "payments.review";

        /// <summary>Super Admin only: forces a payment state the provider has not reported.</summary>
        public const string Override = "payments.override";
    }

    public static class Users
    {
        public const string ManageStaff = "users.staff.manage";

        /// <summary>Super Admin only: creating or altering Admin accounts.</summary>
        public const string ManageAdministrators = "users.administrators.manage";
    }

    public static class System
    {
        /// <summary>Super Admin only: editing configurable limits, prices and contact details.</summary>
        public const string ChangeConfiguration = "system.configuration.change";
        public const string ViewAudit = "system.audit.view";
        public const string ViewIntegrationState = "system.integration.view";
    }

    /// <summary>
    /// Capabilities reserved to the Super Admin (specification section 4). Listed
    /// explicitly so a future change cannot quietly grant one of them to Admin.
    /// </summary>
    public static readonly IReadOnlyList<string> SuperAdminOnly = new[]
    {
        Portfolios.DeletePermanently,
        Portfolios.Restore,
        Portfolios.ChangeSlug,
        Payments.Override,
        Users.ManageAdministrators,
        System.ChangeConfiguration
    };

    /// <summary>
    /// Default capability set per role, applied at seeding. Super Admin is absent
    /// deliberately: it is granted everything by
    /// <see cref="PermissionAuthorizationHandler"/> rather than by a claim list that
    /// could fall out of step as permissions are added.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultsByRole =
        new Dictionary<string, string[]>
        {
            [Roles.Admin] =
            [
                Clients.ViewAll, Clients.Create, Clients.Edit,
                Media.ViewPool, Media.Upload, Media.Delete, Media.Select, Media.SetFeatured,
                Portfolios.View, Portfolios.Edit, Portfolios.ChangeStatus,
                Portfolios.Publish, Portfolios.Unpublish, Portfolios.Archive,
                Payments.View, Payments.StartCheckout, Payments.MarkNoSale, Payments.Review,
                Users.ManageStaff,
                System.ViewAudit, System.ViewIntegrationState
            ],
            [Roles.Retoucher] =
            [
                Clients.ViewAssigned,
                Media.ViewPool, Media.Upload, Media.Select,
                Portfolios.View, Portfolios.SubmitForReview
            ],
            [Roles.Client] =
            [
                Clients.ViewOwn, Clients.EditOwn,
                Media.UploadOwn, Media.SelectOwn,
                Portfolios.EditOwn
            ]
        };

    /// <summary>Every permission constant, used to register one policy per permission.</summary>
    public static IEnumerable<string> All()
    {
        yield return Clients.ViewAll;
        yield return Clients.ViewAssigned;
        yield return Clients.Create;
        yield return Clients.Edit;
        yield return Clients.ViewOwn;
        yield return Clients.EditOwn;

        yield return Media.ViewPool;
        yield return Media.Upload;
        yield return Media.Delete;
        yield return Media.Select;
        yield return Media.SetFeatured;
        yield return Media.UploadOwn;
        yield return Media.SelectOwn;

        yield return Portfolios.View;
        yield return Portfolios.Edit;
        yield return Portfolios.EditOwn;
        yield return Portfolios.SubmitForReview;
        yield return Portfolios.ChangeStatus;
        yield return Portfolios.Publish;
        yield return Portfolios.Unpublish;
        yield return Portfolios.Archive;
        yield return Portfolios.DeletePermanently;
        yield return Portfolios.Restore;
        yield return Portfolios.ChangeSlug;

        yield return Payments.View;
        yield return Payments.StartCheckout;
        yield return Payments.MarkNoSale;
        yield return Payments.Review;
        yield return Payments.Override;

        yield return Users.ManageStaff;
        yield return Users.ManageAdministrators;

        yield return System.ChangeConfiguration;
        yield return System.ViewAudit;
        yield return System.ViewIntegrationState;
    }
}
