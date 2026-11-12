using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed partial class ServiceDraftService
{
    // Only server-owned fixture identities qualify. Real customs flags and gate
    // permissions are never changed. Production and unspecified hosts fail closed.
    private List<JsonObject> DemoCustoms(List<JsonObject> rows)
    {
        foreach (var row in rows)
            row["customsDemoCleared"] = environment?.IsDevelopment() == true
                && row["isDemoSource"]?.GetValue<bool>() == true
                && row["customsCleared"]?.GetValue<bool>() != true;
        return rows;
    }
    private static bool CustomsReady(JsonObject? state) => state?["customsCleared"]?.GetValue<bool>() == true
        || state?["customsDemoCleared"]?.GetValue<bool>() == true;
}
