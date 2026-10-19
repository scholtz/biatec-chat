/// Build-time configuration.
class AppConfig {
  const AppConfig({required this.oidcBaseUrl, required this.isMock});

  /// Default points at the mock Biatec OIDC. 10.0.2.2 is the host machine
  /// seen from the Android emulator.
  static const String mockOidcBaseUrl = 'http://10.0.2.2:5080/mock-oidc';

  static const String _definedOidcBaseUrl =
      String.fromEnvironment('OIDC_BASE_URL');

  /// Base URL of Biatec OIDC (mock unless overridden with --dart-define).
  final String oidcBaseUrl;

  /// True when [oidcBaseUrl] is the mock.
  final bool isMock;

  factory AppConfig.fromEnvironment() => AppConfig.resolve(_definedOidcBaseUrl);

  factory AppConfig.resolve(String defined) {
    final url = defined.trim();
    if (url.isEmpty) {
      return const AppConfig(oidcBaseUrl: mockOidcBaseUrl, isMock: true);
    }
    return AppConfig(oidcBaseUrl: url, isMock: url == mockOidcBaseUrl);
  }
}
