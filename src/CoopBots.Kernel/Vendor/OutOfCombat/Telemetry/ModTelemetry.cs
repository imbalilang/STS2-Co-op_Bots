// Ported from Random Foreseer 0.13.14 (MIT, copyright (c) 2026 hotwords123).
// See THIRD_PARTY_NOTICES.md.
//
// Upstream reports prediction failures to the author's telemetry. The kernel
// reports nothing: a prediction it cannot make is not a defect, and the callers
// already treat an empty prediction as "no information".

using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel.Vendor.RandomForeseer.Telemetry;

internal static class ModTelemetry
{
    public static void CaptureException(Exception exception, string category, string operation, object? context = null)
    {
    }
}

internal static class TelemetryContext
{
    public static object ForModel(AbstractModel model) => model.Id.ToString();

    public static object ForModel(Type modelType) => modelType.Name;
}
