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
      this.browserLogin = false,
      this.trustSettings = false,
      this.relyingParties = false,
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

  /// The redirect of a desktop build (Windows, Linux, macOS): the app listens on the loopback interface for the answer of the system browser (RFC 8252, section 7.3).
  /// A desktop app cannot receive an app link, so this is its only kind of redirect; the provider registers exactly this URI for the client.
  static const String desktopRedirectUri = 'http://localhost:42815';

  /// The public client of the desktop app at stage Biatec OIDC (no secret, PKCE, redirect [desktopRedirectUri]).
  static const String stageDesktopClientId = 'biatec-authenticator-stage-app';

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

  static const bool _hasBrowserLogin = bool.hasEnvironment('BROWSER_LOGIN');
  static const bool _definedBrowserLogin = bool.fromEnvironment('BROWSER_LOGIN');

  static const bool _definedTrustSettings = bool.fromEnvironment('TRUST_SETTINGS');

  static const bool _definedRelyingParties = bool.fromEnvironment('RELYING_PARTIES');

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

  /// Whether the connect step signs the user in with the system browser (T-248): authorization code with PKCE, and the access token of that login enrolls the device. It needs
  /// nothing of [inAppLogin] from the server, only a public client with this app's redirect, so it works against today's Biatec OIDC. On whenever [inAppLogin] is; on by
  /// default for a desktop build against stage Biatec OIDC (the client [stageDesktopClientId] exists there); otherwise `--dart-define=BROWSER_LOGIN=true`.
  /// `BROWSER_LOGIN=false` turns the desktop default off.
  final bool browserLogin;

  /// Whether Biatec OIDC has the user-held trust settings (T-245, issues 27 and 30 of AlgorandGoogleDriveAccount): the user's own threshold with no ceiling, `ask_on_test_networks`, `block_unproven_clients`, per service
  /// `label`, `trusted_applications_add` / `_remove`, `unlink`, `linked` and `remember` on a receipt, and a refusal of a loosening that is not signed (`403 consent_required`). Real Biatec OIDC has none yet, so this is on
  /// for the mock only until `--dart-define=TRUST_SETTINGS=true` says otherwise. Off, Settings keeps the one threshold and the block switch of before, and there is no Linked services screen and no
  /// "don't ask again" on the approval card (the receipt never carries `remember`).
  final bool trustSettings;

  /// Whether Biatec OIDC has the relying-party protocol 1.2 (T-246, issue 38 of AlgorandGoogleDriveAccount): linking a service by QR code (`GET /consent/links/{id}`,
  /// `POST /consent/links/{id}/receipt`) and the payment, login and action requests with `details_hash`. Real Biatec OIDC has none yet, so this is on for the mock only until
  /// `--dart-define=RELYING_PARTIES=true` says otherwise. It also needs [twoKeys] (an approval is signed by the approval key) and [trustSettings] (Linked services): see [relyingPartiesEnabled]. Off, there is no "Link a service", the
  /// `biatec-authenticator://link` code is not acted on, and a request of one of the new types is shown as "This request cannot be shown; update the app" and cannot be approved.
  final bool relyingParties;

  /// The link flow and the three request types are available: the server has them, the device has two keys, and the user-held trust settings are on (the list of linked services, where a
  /// service is linked from and unlinked, is one of their screens).
  bool get relyingPartiesEnabled => relyingParties && twoKeys && trustSettings;

  /// The OAuth client id of this app (`--dart-define=OIDC_CLIENT_ID`).
  final String oidcClientId;

  /// Where the system browser returns to after the login: an app link in a release build, the custom scheme only in a debug build.
  final String redirectUri;

  /// Not a debug build (release and profile).
  final bool release;

  /// Whether the enrollment screen has the field to paste a login token. Debug builds only (`release` is `!kDebugMode`, so the field is not compiled into a
  /// release build), and only while there is no in-app login against a real Biatec OIDC: the mock always has it, because its seeded `login-*` tokens are for tests.
  bool get pasteLoginToken => !release && (isMock || !signIn);

  /// The connect step has the sign-in button: the system browser login is on ([browserLogin], or [inAppLogin], which includes it).
  bool get signIn => inAppLogin || browserLogin;

  /// Activation of a new device by an existing one needs the in-app login and the two keys (the pairing code covers both).
  bool get activationByDevice => inAppLogin && twoKeys;

  factory AppConfig.fromEnvironment() =>
      AppConfig.resolve(_definedOidcBaseUrl,
          settingsApi: _definedSettingsApi,
          deviceChannel: _definedDeviceChannel,
          twoKeys: _definedTwoKeys,
          inAppLogin: _definedInAppLogin,
          browserLogin: _hasBrowserLogin ? _definedBrowserLogin : null,
          trustSettings: _definedTrustSettings,
          relyingParties: _definedRelyingParties,
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
      bool? browserLogin,
      bool trustSettings = false,
      bool relyingParties = false,
      String clientId = '',
      String redirectUri = '',
      bool? release,
      bool? windows,
      bool? desktop}) {
    final isRelease = release ?? !kDebugMode;
    final isWindows = windows ?? defaultTargetPlatform == TargetPlatform.windows;
    final isDesktop = desktop ??
        (isWindows ||
            defaultTargetPlatform == TargetPlatform.linux ||
            defaultTargetPlatform == TargetPlatform.macOS);
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
          browserLogin: true,
          trustSettings: true,
          relyingParties: true,
          oidcClientId: _clientId(clientId, false),
          redirectUri: _redirect(redirectUri, false, null, true, isDesktop),
          release: false);
    }
    // The mock is the same endpoint however the URL is spelled (scheme and host are case-insensitive).
    final isMock = Uri.tryParse(url)?.toString() == mockOidcBaseUrl;
    if (isMock && isRelease) {
      throw const ConfigException(
          'The mock Biatec OIDC is not available in a release build.');
    }
    if (!isMock) validateUrl(url, release: isRelease);
    // The desktop client exists at stage Biatec OIDC, so a desktop build against stage signs in with the browser unless the build says no.
    final stageDesktop = isDesktop &&
        !isMock &&
        Uri.parse(url).host.toLowerCase() == Uri.parse(stageOidcBaseUrl).host;
    final login = isMock || inAppLogin || (browserLogin ?? stageDesktop);
    return AppConfig(
        oidcBaseUrl: isMock ? mockOidcBaseUrl : url,
        isMock: isMock,
        settingsApi: isMock || settingsApi,
        deviceChannel: isMock || deviceChannel,
        twoKeys: isMock || twoKeys,
        inAppLogin: isMock || inAppLogin,
        browserLogin: login,
        trustSettings: isMock || trustSettings,
        relyingParties: isMock || relyingParties,
        oidcClientId: _clientId(clientId, stageDesktop),
        redirectUri: _redirect(redirectUri, isRelease, url, login, isDesktop),
        release: isRelease);
  }

  static String _clientId(String defined, bool stageDesktop) {
    final id = defined.trim();
    if (id.isEmpty) return stageDesktop ? stageDesktopClientId : defaultOidcClientId;
    if (!RegExp(r'^[A-Za-z0-9._~-]{1,128}$').hasMatch(id)) {
      throw const ConfigException('OIDC_CLIENT_ID is not a valid client id.');
    }
    return id;
  }

  /// The redirect: what is defined, else the app link on the host of [baseUrl] (release) or the custom scheme (debug). A release build accepts an https redirect only,
  /// so the custom scheme, which any app may register, is never used outside a debug build, and the host must be one that has app links ([appLinkHosts]); with the
  /// in-app login on ([login]) anything else is a [ConfigException], with it off the redirect is never used and the production link stands in.
  ///
  /// A desktop build ([desktop]) is different: it cannot receive an app link or a custom scheme, so its redirect is a loopback address the app itself listens on
  /// ([isLoopbackRedirect]), [desktopRedirectUri] unless defined, in a release build too. Anything else with the login on is a [ConfigException].
  static String _redirect(String defined, bool release, String? baseUrl, bool login, bool desktop) {
    final value = defined.trim();
    if (desktop) {
      if (value.isEmpty) return desktopRedirectUri;
      if (login && !isLoopbackRedirect(value)) {
        throw const ConfigException(
            'OIDC_REDIRECT_URI of a desktop build must be a loopback address with a port, for example http://localhost:42815 (no query, no fragment).');
      }
      return value;
    }
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

  /// The hosts a loopback redirect may name. `localhost` is accepted because Biatec OIDC matches the registered redirect by its exact host.
  static const loopbackRedirectHosts = {'localhost', '127.0.0.1', '::1'};

  /// Whether [redirect] is one the desktop login can listen on: plain `http` (the answer never leaves this machine), a loopback host, an explicit port from 1024 up, and
  /// no credentials, query or fragment.
  static bool isLoopbackRedirect(String redirect) {
    final uri = Uri.tryParse(redirect);
    return uri != null &&
        uri.scheme == 'http' &&
        loopbackRedirectHosts.contains(uri.host.toLowerCase()) &&
        uri.hasPort &&
        uri.port >= 1024 &&
        uri.userInfo.isEmpty &&
        !uri.hasQuery &&
        !uri.hasFragment;
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
