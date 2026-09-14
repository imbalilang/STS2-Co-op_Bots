// Ported from Random Foreseer 0.13.14 (MIT, copyright (c) 2026 hotwords123).
// See THIRD_PARTY_NOTICES.md.
//
// Upstream reads its own localization table for the text tips it renders. The
// kernel never renders a prediction, but the tip factory still builds the
// strings on its way to the model we actually want, so they are created against
// a local table id and never displayed.

using MegaCrit.Sts2.Core.Localization;

namespace CoopBots.Kernel.Vendor.RandomForeseer.Localization;

internal static class ModLocalization
{
    private const string TableId = "CoopBotsKernelPrediction";

    public static LocString Text(string key) => new(TableId, key);
}
