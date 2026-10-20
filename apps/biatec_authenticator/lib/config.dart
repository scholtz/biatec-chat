/// Build-time configuration.
class AppConfig {
  const AppConfig(
      {required this.oidcBaseUrl,
      required this.isMock,
      this.settingsApi = false});

  /// Default points at the mock Biatec OIDC. 10.0.2.2 is the host machine
  /// seen from the Android emulator.
  static const String mockOidcBaseUrl = 'http://10.0.2.2:5080/mock-oidc';

  static const String _definedOidcBaseUrl =
      String.fromEnvironment('OIDC_BASE_URL');

  static const bool _definedSettingsApi =
      bool.fromEnvironment('CONSENT_SETTINGS_API');

  /// Base URL of Biatec OIDC (mock unless overridden with --dart-define).
  final String oidcBaseUrl;

  /// True when [oidcBaseUrl] is the mock.
  final bool isMock;

  /// Whether the device list and consent settings API exists at
  /// [oidcBaseUrl]. Real Biatec OIDC has none yet (required changes, rows 21
  /// and 22), so this is on for the mock only until
  /// `--dart-define=CONSENT_SETTINGS_API=true` says otherwise.
  final bool settingsApi;

  factory AppConfig.fromEnvironment() =>
      AppConfig.resolve(_definedOidcBaseUrl, settingsApi: _definedSettingsApi);

  factory AppConfig.resolve(String defined, {bool settingsApi = false}) {
    final url = defined.trim().replaceAll(RegExp(r'/+$'), '');
    if (url.isEmpty) {
      return const AppConfig(
          oidcBaseUrl: mockOidcBaseUrl, isMock: true, settingsApi: true);
    }
    final isMock = url == mockOidcBaseUrl;
    return AppConfig(
        oidcBaseUrl: url, isMock: isMock, settingsApi: isMock || settingsApi);
  }
}
