using SimplArchive.Api.Controllers;
using SimplArchive.Infrastructure.Modules;

namespace SimplArchive.UnitTests;

// ADR 0900: the facade's audit floor speaks the core's own document vocabulary. Infrastructure cannot reference the
// Api's AuditActions, so the codes are restated there; this holds each equal to its twin.
public class ModuleWriteAuditTests
{
    [Fact]
    public void Every_floor_code_is_the_cores_own()
    {
        Assert.Equal(AuditActions.DocumentCreated, ModuleWriteAudit.Created);
        Assert.Equal(AuditActions.DocumentIndexDataUpdated, ModuleWriteAudit.IndexDataUpdated);
        Assert.Equal(AuditActions.DocumentRenamed, ModuleWriteAudit.Renamed);
        Assert.Equal(AuditActions.DocumentVersionAdded, ModuleWriteAudit.VersionAdded);
        Assert.Equal(AuditActions.ReferenceAdded, ModuleWriteAudit.ReferenceAdded);
    }
}
