namespace Spectra.Api.Services;

// Maps Graph's skuPartNumber (e.g. "ENTERPRISEPACK") to a human-readable
// product name. Not exhaustive — Microsoft's full list runs to hundreds of
// SKUs and changes over time; this covers the common Microsoft 365/Office
// 365/EMS ones an MSP is most likely to see. Falls back to the raw
// skuPartNumber for anything not listed, which is still meaningful to an
// admin even if less polished.
public static class LicenseSkuNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPE_E3"] = "Microsoft 365 E3",
        ["SPE_E5"] = "Microsoft 365 E5",
        ["SPE_F1"] = "Microsoft 365 F3",
        ["SPB"] = "Microsoft 365 Business Premium",
        ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
        ["O365_BUSINESS"] = "Microsoft 365 Apps for Business",
        ["ENTERPRISEPACK"] = "Office 365 E3",
        ["ENTERPRISEPREMIUM"] = "Office 365 E5",
        ["ENTERPRISEWITHSCAL"] = "Office 365 E4",
        ["STANDARDPACK"] = "Office 365 E1",
        ["DESKLESSPACK"] = "Office 365 F3",
        ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)",
        ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)",
        ["EXCHANGEARCHIVE_ADDON"] = "Exchange Online Archiving",
        ["SHAREPOINTSTANDARD"] = "SharePoint Online (Plan 1)",
        ["SHAREPOINTENTERPRISE"] = "SharePoint Online (Plan 2)",
        ["MCOSTANDARD"] = "Skype for Business Online (Plan 2)",
        ["TEAMS1"] = "Microsoft Teams",
        ["POWER_BI_PRO"] = "Power BI Pro",
        ["POWER_BI_STANDARD"] = "Power BI (free)",
        ["EMS"] = "Enterprise Mobility + Security E3",
        ["EMSPREMIUM"] = "Enterprise Mobility + Security E5",
        ["AAD_PREMIUM"] = "Microsoft Entra ID P1",
        ["AAD_PREMIUM_P2"] = "Microsoft Entra ID P2",
        ["INTUNE_A"] = "Microsoft Intune",
        ["FLOW_FREE"] = "Power Automate (free)",
        ["POWERAPPS_VIRAL"] = "Power Apps (free)",
        ["WIN10_PRO_ENT_SUB"] = "Windows 10/11 Enterprise",
        ["VISIOCLIENT"] = "Visio Plan 2",
        ["PROJECTPREMIUM"] = "Project Plan 5",
        ["PROJECTPROFESSIONAL"] = "Project Plan 3",
        ["MEETING_ROOM"] = "Microsoft Teams Rooms",
        ["DEFENDER_ENDPOINT_P1"] = "Microsoft Defender for Endpoint P1",
        ["DEFENDER_ENDPOINT_P2"] = "Microsoft Defender for Endpoint P2",
    };

    public static string DisplayName(string skuPartNumber) => Names.GetValueOrDefault(skuPartNumber, skuPartNumber);
}
