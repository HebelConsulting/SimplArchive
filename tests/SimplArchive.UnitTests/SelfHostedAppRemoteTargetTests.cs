using System.Reflection;
using DotNet.Testcontainers.Containers;
using SimplArchive.SelfHosting;

namespace SimplArchive.UnitTests;

/// <summary>
/// A remote-target <see cref="SelfHostedApp"/> must construct no container at all (#753).
/// </summary>
/// <remarks>
/// This lives in the FAST tier on purpose. The bug it guards was that the containers were field initialisers,
/// which run in the constructor — before <c>RemoteTarget</c>, an object-initialiser property, can be set — so the
/// remote guards in <c>StartAsync</c>/<c>DisposeAsync</c> were unreachable. Because
/// <c>PostgreSqlBuilder.Build()</c> validates the Docker endpoint, the symptom was that pointing the load harness
/// at the kiosk required a local Docker daemon. A guard placed in a Docker-bearing suite could therefore never
/// have caught it: the condition that breaks is precisely "no Docker here".
///
/// The assertions are reflective rather than field-by-field so that a SIXTH container added later is covered
/// without anyone remembering to extend this test — the regression to fear is a new eager field, not a change to
/// one of the five.
/// </remarks>
public class SelfHostedAppRemoteTargetTests
{
    private const string Remote = "https://demo.example.invalid";

    [Fact]
    public async Task A_remote_target_constructs_starts_and_disposes_without_building_a_container()
    {
        var app = new SelfHostedApp { RemoteTarget = Remote };

        AssertNoContainerBuilt(app, "constructing");

        await app.StartAsync();
        Assert.Equal(Remote, app.BaseUrl);
        AssertNoContainerBuilt(app, "starting");

        await app.DisposeAsync();
        AssertNoContainerBuilt(app, "disposing");
    }

    [Fact]
    public async Task A_remote_target_is_taken_verbatim_apart_from_a_trailing_slash()
    {
        // The harness reports this URL as the thing it measured, so a stray slash would misreport the target.
        var app = new SelfHostedApp { RemoteTarget = $"{Remote}/" };
        await app.StartAsync();

        Assert.Equal(Remote, app.BaseUrl);
    }

    [Fact]
    public void The_self_hosted_connection_string_is_refused_rather_than_built_on_a_remote_target()
    {
        var app = new SelfHostedApp { RemoteTarget = Remote };

        var ex = Assert.Throws<InvalidOperationException>(() => app.PostgresConnectionString);

        Assert.Contains(Remote, ex.Message);
        AssertNoContainerBuilt(app, "asking for the connection string of");
    }

    private static void AssertNoContainerBuilt(SelfHostedApp app, string phase)
    {
        foreach (var field in typeof(SelfHostedApp).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var value = field.GetValue(app);

            // A container held directly (the OCR sidecar's shape) must still be unset.
            Assert.False(
                value is IContainer,
                $"{field.Name} holds a container after {phase} a remote-target app — it should be built only when "
                + "this engine is actually booting one.");

            if (value is null || !field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(Lazy<>))
            {
                continue;
            }

            var created = (bool)field.FieldType.GetProperty(nameof(Lazy<object>.IsValueCreated))!.GetValue(value)!;
            Assert.False(
                created,
                $"{field.Name} was built after {phase} a remote-target app. Building a Testcontainers container "
                + "validates the Docker endpoint, so this is what made a kiosk run require a local Docker daemon "
                + "(#753). Read .Value only on the path that actually starts a container.");
        }
    }
}
