using System.Reflection;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

/// <summary>
/// The protocol-read populate hook (ABI 0.27, ADR 0810, issue #1286) — the properties that must hold for the
/// gate to mean what it says, asserted where they can be asserted without a host.
/// </summary>
public class ProtocolReadRefreshTests
{
    // The whole feature is two answers ANDed together: the module says its source may be fetched this way,
    // the tenant administrator says a PROPFIND here is a person asking. Both halves default to "no", and that
    // is not a detail — an enum whose zero meant "yes" would opt in every module written before this existed,
    // silently, at the next upgrade. ADR 0756 rejected unattended fetching on legal grounds; a default that
    // turned it on for everyone would be that decision reversed by an implementation accident.
    [Fact]
    public void The_default_declaration_is_no_fetch()
    {
        Assert.Equal(0, (int)default(ProtocolReadRefresh));
        Assert.Equal(ProtocolReadRefresh.Never, default);
    }

    [Fact]
    public void The_default_setting_kind_is_text()
    {
        // Same argument on the other half: every setting declared before 0.27 was free text, so the added
        // property must read as Text for a declaration that never mentions it.
        Assert.Equal(ModuleSettingKind.Text, new ModuleSetting("k", "l").Kind);
        Assert.Equal(0, (int)default(ModuleSettingKind));
    }

    // ADR 0789, learned the expensive way in #1147: a module is loaded against the ABI it COMPILED against,
    // and a changed constructor is not a compile error at the module's build — it is a MissingMethodException
    // at run time, AFTER a successful load, which the version gate cannot catch because the gate has already
    // passed. That took the kiosk down for 1h34m. So the additions in 0.27 must be additive in the binary
    // sense: a new OVERLOAD beside the old one, and an INIT-ONLY PROPERTY rather than a fifth ctor parameter.
    [Fact]
    public void The_settings_record_kept_its_constructor()
    {
        var constructors = typeof(ModuleSetting).GetConstructors();
        Assert.Contains(constructors, c => c.GetParameters().Length == 4);
        Assert.DoesNotContain(constructors, c => c.GetParameters().Length == 5);

        var kind = typeof(ModuleSetting).GetProperty(nameof(ModuleSetting.Kind))!;
        Assert.NotNull(kind.GetSetMethod());   // init-only is still a setter, so this asserts it is settable…
        Assert.Contains(kind.GetSetMethod()!.ReturnParameter.GetRequiredCustomModifiers(),
            m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");   // …and that it is INIT-only
    }

    [Fact]
    public void The_builder_kept_its_earlier_overloads()
    {
        // Every growth is a NEW overload, never a parameter on an existing one: a changed signature is a
        // MissingMethodException at module load (ADR 0789's rule) — 0.27 added the ProtocolRead overload,
        // 0.28 the minimum-refresh-interval one (ADR 0815), and the older two must survive both.
        var overloads = typeof(IStateMachineBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(IStateMachineBuilder.AutoRefreshOnOpen))
            .ToList();

        Assert.Equal(3, overloads.Count);
        Assert.Contains(overloads, m => m.GetParameters().Length == 3);
        Assert.Contains(overloads, m => m.GetParameters().Length == 4
            && m.GetParameters()[3].ParameterType == typeof(ProtocolReadRefresh));
        Assert.Contains(overloads, m => m.GetParameters().Length == 5
            && m.GetParameters()[4].ParameterType == typeof(TimeSpan));
    }

    // The setting key IS the stored row's identity, so it may not drift: renaming it strands every tenant's
    // answer as an orphan row while the toggle reads its absence as "off" — the feature silently turning
    // itself off for everyone who had enabled it, with no error anywhere.
    [Fact]
    public void The_setting_key_is_stable()
    {
        Assert.Equal("core.protocolReadRefresh", ProtocolReadRefreshSetting.Key);
    }
}
