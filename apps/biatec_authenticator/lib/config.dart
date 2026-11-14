import 'package:flutter/foundation.dart';

/// The Biatec OIDC URL is not usable: not https (outside local mock use), or not a plain base URL.
class ConfigException implements Exception {
  const ConfigException(this.message);
  final String message;

  @override
  String toString() => 'ConfigException: $message';
}

/// Build-time configuration.
class AppConfig {
  const AppConfig(
      {required this.oidcBaseUrl,
      required this.isMock,
      this.settingsApi = false,
      this.deviceChannel = false,
      this.twoKeys = false,
      this.inAppLogin = false,
      this.trustSettings = false,
      this.oidcClientId = defaultOidcClientId,
      this.redirectUri = debugRedirectUri,
      this.release = false});

  /// The client id of this app at Biatec OIDC when `--dart-define=OIDC_CLIENT_ID` does not say another.
  static const String defaultOidcClientId = 'biatec-authenticator';

  /// The redirect of a release build (an app link: the OS opens the app for it, no other app can claim it) and of a debug build (a custom scheme, so a developer needs no hosted `assetlinks.json`). Required changes, "App links".
  ///
  /// The release redirect is `https://<host of OIDC_BASE_URL>/app/authenticator/callback`, and only the hosts that have an app link in the Android manifest and the iOS
  /// entitlements ([appLinkHosts]) can have one. On iOS the https callback of `ASWebAuthenticationSession` needs iOS 17.4 or later (the deployment target is 15.0, so an older
  /// phone is told so by `AppAuthClient` instead of the login hanging; see the README); Android has no such minimum.
  static const String appLinkPath = '/app/authenticator/callback';
  static const Set<String> appLinkHosts = {'oidc.biatec.io', 'stage.oidc.biatec.io'};

  /// The production app link, the redirect when the URL is not one with app links and the in-app login is off (it is not used then).
  static const String appLinkRedirectUri = 'https://oidc.biatec.io$appLinkPath';
  static const String debugRedirectUri = 'io.biatec.authenticator:/oauth2redirect';

  /// Default points at the mock Biatec OIDC. 10.0.2.2 is the host machine
  /// seen from the Android emulator.
  static const String mockOidcBaseUrl = 'http://10.0.2.2:5080/mock-oidc';

  /// Default for the Windows desktop build when no `OIDC_BASE_URL` is defined.
  static const String stageOidcBaseUrl = 'https://stage.oidc.biatec.io';

  static const String _definedOidcBaseUrl =
      String.fromEnvironment('OIDC_BASE_URL');

  static const bool _definedDeviceChannel = bool.fromEnvironment('DEVICE_CHANNEL');

  static const bool _definedTwoKeys = bool.fromEnvironment('TWO_KEY_DEVICE');

  static const bool _definedInAppLogin = bool.fromEnvironment('IN_APP_LOGIN');

  static const bool _definedTrustSettings = bool.fromEnvironment('TRUST_SETTINGS');

  static const String _definedClientId = String.fromEnvironment('OIDC_CLIENT_ID');

  static const String _definedRedirect = String.fromEnvironment('OIDC_REDIRECT_URI');

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

  /// Whether Biatec OIDC has the device channel (`POST /devices/hub-ticket` and the hub, required changes row 24). Real Biatec OIDC has none yet, so
  /// this is on for the mock only until `--dart-define=DEVICE_CHANNEL=true` says otherwise. Off: no ticket is asked for, the Android service is
  /// never started and the "Stay connected" setting is not shown; the app polls.
  final bool deviceChannel;

  /// Whether Biatec OIDC has the two device keys (protocol 1.1, D9): `POST /devices` with the second key and the enrollment proofs, `POST /devices/challenge`, the
  /// presence key for device proofs. Real Biatec OIDC has none yet, so this is on for the mock only until `--dart-define=TWO_KEY_DEVICE=true` says otherwise. Off, the
  /// app enrolls ONE key (the one that needs no prompt), asks for a server challenge only when the platform attestation needs one, and an approval is gated by the app's biometric check
  /// as before T-241 (the keystore binding of the approval key is not used until the server side exists).
  final bool twoKeys;

  /// Whether Biatec OIDC has the in-app login for enrollment and the activation of a new device by an existing one (T-242, D10): `enroll-device` scope, `auth_time`
  /// enforcement, `POST /devices/{kid}/activate`, the recovery code in `POST /devices`. Real Biatec OIDC has none yet, so this is on for the mock only until
  /// `--dart-define=IN_APP_LOGIN=true` says otherwise. Off, the app keeps the enrollment it had (a pasted login token, debug builds only) and offers no sign-in and no activation.
  final bool inAppLogin;

  /// Whether Biatec OIDC has the user-held trust settings (T-245, issues 27 and 30 of AlgorandGoogleDriveAccount): the user's own threshold with no ceiling, `ask_on_test_networks`, `block_unproven_clients`, per service
  /// `label`, `trusted_applications_add` / `_remove`, `unlink`, `linked` and `remember` on a receipt, and a refusal of a loosening that is not signed (`403 consent_required`). Real Biatec OIDC has none yet, so this is on
  /// for the mock only until `--dart-define=TRUST_SETTINGS=true` says otherwise. Off, Settings keeps the one threshold and the block switch of before, and there is no Linked services screen and no
  /// "don't ask again" on the approval card (the receipt never carries `remember`).
  final bool trustSettings;

  /// The OAuth client id of this app (`--dart-define=OIDC_CLIENT_ID`).
  final String oidcClientId;

  /// Where the system browser returns to after the login: an app link in a release build, the custom scheme only in a debug build.
  final String redirectUri;

  /// Not a debug build (release and profile).
  final bool release;

  /// Whether the enrollment screen has the field to paste a login token. Debug builds only (`release` is `!kDebugMode`, so the field is not compiled into a
  /// release build), and only while there is no in-app login against a real Biatec OIDC: the mock always has it, because its seeded `login-*` tokens are for tests.
  bool get pasteLoginToken => !release && (isMock || !inAppLogin);

  /// Activation of a new device by an existing one needs the in-app login and the two keys (the pairing code covers both).
  bool get activationByDevice => inAppLogin && twoKeys;

  factory AppConfig.fromEnvironment() =>
      AppConfig.resolve(_definedOidcBaseUrl,
          settingsApi: _definedSettingsApi,
          deviceChannel: _definedDeviceChannel,
          twoKeys: _definedTwoKeys,
          inAppLogin: _definedInAppLogin,
          trustSettings: _definedTrustSettings,
          clientId: _definedClientId,
          redirectUri: _definedRedirect);

  /// Hosts that mean "this machine" for a developer: plain http is acceptable to them in a build that is not a release build.
  static const localHosts = {'localhost', '127.0.0.1', '::1', '10.0.2.2'};

  /// [release] is true for anything that is not a debug build (release and profile builds): those only accept https, and have no mock default.
  factory AppConfig.resolve(String defined,
      {bool settingsApi = false,
      bool deviceChannel = false,
      bool twoKeys = false,
      bool inAppLogin = false,
      bool trustSettings = false,
      String clientId = '',
      String redirectUri = '',
      bool? release,
      bool? windows}) {
    final isRelease = release ?? !kDebugMode;
    final isWindows = windows ?? defaultTargetPlatform == TargetPlatform.windows;
    var url = defined.trim().replaceAll(RegExp(r'/+$'), '');
    // A plain `flutter build windows` is a release build and cannot read a defines file, so the desktop build
    // defaults to stage Biatec OIDC. Mobile release builds still need an explicit URL.
    if (url.isEmpty && isRelease && isWindows) url = stageOidcBaseUrl;
    if (url.isEmpty) {
      if (isRelease) {
        throw const ConfigException(
            'OIDC_BASE_URL is required in a release build (there is no mock default).');
      }
      return AppConfig(
          oidcBaseUrl: mockOidcBaseUrl,
          isMock: true,
          settingsApi: true,
          deviceChannel: true,
          twoKeys: true,
          inAppLogin: true,
          trustSettings: true,
          oidcClientId: _clientId(clientId),
          redirectUri: _redirect(redirectUri, false, null, true),
          release: false);
    }
    // The mock is the same endpoint however the URL is spelled (scheme and host are case-insensitive).
    final isMock = Uri.tryParse(url)?.toString() == mockOidcBaseUrl;
    if (isMock && isRelease) {
      throw const ConfigException(
          'The mock Biatec OIDC is not available in a release build.');
    }
    if (!isMock) validateUrl(url, release: isRelease);
    return AppConfig(
        oidcBaseUrl: isMock ? mockOidcBaseUrl : url,
        isMock: isMock,
        settingsApi: isMock || settingsApi,
        deviceChannel: isMock || deviceChannel,
        twoKeys: isMock || twoKeys,
        inAppLogin: isMock || inAppLogin,
        trustSettings: isMock || trustSettings,
        oidcClientId: _clientId(clientId),
        redirectUri: _redirect(redirectUri, isRelease, url, isMock || inAppLogin),
        release: isRelease);
  }

  static String _clientId(String defined) {
    final id = defined.trim();
    if (id.isEmpty) return defaultOidcClientId;
    if (!RegExp(r'^[A-Za-z0-9._~-]{1,128}$').hasMatch(id)) {
      throw const ConfigException('OIDC_CLIENT_ID is not a valid client id.');
    }
    return id;
  }

  /// The redirect: what is defined, else the app link on the host of [baseUrl] (release) or the custom scheme (debug). A release build accepts an https redirect only,
  /// so the custom scheme, which any app may register, is never used outside a debug build, and the host must be one that has app links ([appLinkHosts]); with the
  /// in-app login on ([login]) anything else is a [ConfigException], with it off the redirect is never used and the production link stands in.
  static String _redirect(String defined, bool release, String? baseUrl, bool login) {
    final value = defined.trim();
    if (value.isNotEmpty) {
      final uri = Uri.tryParse(value);
      if (uri == null || uri.scheme.isEmpty || uri.hasFragment) {
        throw const ConfigException('OIDC_REDIRECT_URI is not a valid redirect URI.');
      }
      if (release && login && uri.scheme != 'https') {
        throw const ConfigException(
            'OIDC_REDIRECT_URI must be an https app link in a release build.');
      }
      if (release && login && !appLinkHosts.contains(uri.host.toLowerCase())) {
        throw ConfigException(
            'OIDC_REDIRECT_URI must be on a host that has an app link (${appLinkHosts.join(', ')}), not ${uri.host}.');
      }
      return value;
    }
    if (!release) return debugRedirectUri;
    final host = Uri.tryParse(baseUrl ?? '')?.host.toLowerCase() ?? '';
    if (appLinkHosts.contains(host)) return 'https://$host$appLinkPath';
    if (login) {
      throw ConfigException(
          'The in-app login of a release build needs an app link, and only ${appLinkHosts.join(' and ')} have one (the manifest and the entitlements list them); OIDC_BASE_URL is on $host.');
    }
    return appLinkRedirectUri;
  }

  /// Device tokens, binding codes and receipts travel to this URL, so it must be https. The one exception is local development
  /// (loopback, `localhost`, the Android emulator's host alias) in a debug build. Throws [ConfigException].
  @visibleForTesting
  static void validateUrl(String url, {required bool release}) {
    final uri = Uri.tryParse(url);
    if (uri == null || !uri.hasAuthority || uri.host.isEmpty) {
      throw const ConfigException('OIDC_BASE_URL is not a valid URL.');
    }
    if (uri.userInfo.isNotEmpty || uri.hasQuery || uri.hasFragment) {
      throw const ConfigException(
          'OIDC_BASE_URL must be a plain base URL (no credentials, query or fragment).');
    }
    if (uri.scheme == 'https') return;
    if (uri.scheme == 'http' && !release && localHosts.contains(uri.host)) {
      return;
    }
    throw ConfigException(uri.scheme == 'http'
        ? 'OIDC_BASE_URL must be https (plain http is only allowed for localhost in a debug build).'
        : 'OIDC_BASE_URL must be an https URL.');
  }
}
