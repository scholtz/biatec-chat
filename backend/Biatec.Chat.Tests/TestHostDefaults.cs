using System.Runtime.CompilerServices;

namespace Biatec.Chat.Tests;

/// <summary>
/// The web test hosts run in the "Testing" environment with the default Dev vault key provider. The backend refuses that
/// provider outside Development unless Vault:AllowEphemeralKeys is set (BCHAT-012), so the test process opts in once here.
/// An environment variable is read by every host the tests build; tests that check the refusal use in-memory configuration only.
/// </summary>
internal static class TestHostDefaults
{
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("Vault__AllowEphemeralKeys", "true");
}
