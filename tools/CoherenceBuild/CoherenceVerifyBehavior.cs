using System;

namespace CoherenceBuild
{
    [Flags]
    public enum CoherenceVerifyBehavior
    {
        None,
        ProductPackages,
        PartnerPackages,
        All = ProductPackages | PartnerPackages
    }
}
