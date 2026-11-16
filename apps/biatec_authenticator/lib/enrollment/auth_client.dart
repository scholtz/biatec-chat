import 'dart:io' show Platform;

import 'package:flutter/foundation.dart' show TargetPlatform, defaultTargetPlatform;
import 'package:flutter/services.dart' show PlatformException;
import 'package:flutter_appauth/flutter_appauth.dart';

import '../config.dart';
import '../security/pinning/pinned_http.dart';
import 'loopback_auth_client.dart';

/// The login the authenticator asks for (T-242, D10). Everything the server enforces is in here, so a test can check it exactly: the scope, `prompt=login` and `max_age=0`.
class AuthRequest {
  const AuthRequest({
    required this.clientId,
    required this.redirectUri,
    this.scope = enrollScope,
    this.prompt = 'login',
    this.maxAge = 0,
    this.discoveryUrl,
    this.authorizationEndpoint,
    this.tokenEndpoint,
    this.allowInsecure = false,
  });

  /// `openid enroll-device`: the one scope of this login. No `offline_access`: no refresh token is requested.
  static const enrollScope = 'openid enroll-device';

  /// The scope against a Biatec OIDC without the in-app login features (`AppConfig.inAppLogin` off): an ordinary login, whose access token `POST /devices` accepts.
  static const plainScope = 'openid';

  /// Where the login is for: the mock is addressed by its endpoints (it has no hosted discovery under a path prefix), a real Biatec OIDC by its discovery document. Plain http is allowed for the mock only, which a release build refuses anyway.
  factory AuthRequest.forConfig(AppConfig config) {
    final base = config.oidcBaseUrl;
    return AuthRequest(
      clientId: config.oidcClientId,
      redirectUri: config.redirectUri,
      scope: config.inAppLogin ? enrollScope : plainScope,
      discoveryUrl:
          config.isMock ? null : '$base/.well-known/openid-configuration',
      authorizationEndpoint: config.isMock ? '$base/authorize' : null,
      tokenEndpoint: config.isMock ? '$base/token' : null,
      allowInsecure: config.isMock && base.startsWith('http://'),
    );
  }

  final String clientId;
  final String redirectUri;
  final String scope;

  /// OpenID Connect `prompt`: `login` makes the server ask for the credentials even when the browser has a session.
  final String prompt;

  /// OpenID Connect `max_age`: 0 means the authentication must have happened just now.
  final int maxAge;
  final String? discoveryUrl;
  final String? authorizationEndpoint;
  final String? tokenEndpoint;
  final bool allowInsecure;
}

/// Whether the system browser session can return through an `https` callback (an app link): Android always; iOS from 17.4 (`ASWebAuthenticationSession` with an https
/// callback). [osVersion] is `Platform.operatingSystemVersion` (`Version 17.4 (Build ...)`); one that cannot be read fails closed.
bool httpsCallbackSupported(TargetPlatform platform, String osVersion) {
  if (platform != TargetPlatform.iOS) return true;
  final m = RegExp(r'(\d+)\.(\d+)').firstMatch(osVersion);
  if (m == null) return false;
  final major = int.parse(m.group(1)!);
  final minor = int.parse(m.group(2)!);
  return major > 17 || (major == 17 && minor >= 4);
}

/// The user closed the browser or cancelled: nothing happened.
class LoginCancelled implements Exception {
  const LoginCancelled();
  @override
  String toString() => 'The sign-in was cancelled.';
}

/// The login did not give a token.
class LoginFailed implements Exception {
  const LoginFailed(this.message);
  final String message;
  @override
  String toString() => message;
}

/// The login client of this build: a desktop build (its redirect is a loopback address, see `AppConfig.isLoopbackRedirect`) listens for the browser's answer itself
/// ([LoopbackAuthClient]); a phone uses the platform's browser session ([AppAuthClient]).
AuthClient authClientFor(AppConfig config) => AppConfig.isLoopbackRedirect(config.redirectUri)
    ? LoopbackAuthClient(http: () => PinnedHttp.clientFor(config.oidcBaseUrl))
    : AppAuthClient();

/// Signs the user in with the system browser and returns the access token. The token is for the caller to use once and drop: nothing here keeps it, and no refresh token is asked for or looked at.
abstract class AuthClient {
  Future<String> signIn(AuthRequest request);

  /// Ends a sign-in that is waiting for the browser, which then fails with [LoginCancelled]. A client that cannot be told to stop (the platform's browser session ends
  /// when the user closes it) does nothing.
  void cancel() {}

  /// Whether [cancel] does something, so the screen only offers it then.
  bool get canCancel => false;
}

/// `flutter_appauth`: authorization code with PKCE in Custom Tabs on Android and `ASWebAuthenticationSession` on iOS, never an embedded web view. The iOS session is ephemeral, so no browser cookies are shared or kept.
class AppAuthClient extends AuthClient {
  AppAuthClient([FlutterAppAuth? appAuth])
      : _appAuth = appAuth ?? const FlutterAppAuth();

  final FlutterAppAuth _appAuth;

  @override
  Future<String> signIn(AuthRequest r) async {
    if (r.redirectUri.startsWith('https:') &&
        !httpsCallbackSupported(defaultTargetPlatform, Platform.operatingSystemVersion)) {
      throw const LoginFailed(
          'Signing in from this app needs iOS 17.4 or later. Update this phone, or enroll from a newer one.');
    }
    try {
      final response = await _appAuth.authorizeAndExchangeCode(
        AuthorizationTokenRequest(
          r.clientId,
          r.redirectUri,
          scopes: r.scope.split(' '),
          promptValues: [r.prompt],
          additionalParameters: {'max_age': '${r.maxAge}'},
          discoveryUrl: r.discoveryUrl,
          serviceConfiguration: r.authorizationEndpoint == null ||
                  r.tokenEndpoint == null
              ? null
              : AuthorizationServiceConfiguration(
                  authorizationEndpoint: r.authorizationEndpoint!,
                  tokenEndpoint: r.tokenEndpoint!),
          allowInsecureConnections: r.allowInsecure,
          externalUserAgent: ExternalUserAgent.ephemeralAsWebAuthenticationSession,
        ),
      );
      final token = response.accessToken;
      if (token == null || token.isEmpty) {
        throw const LoginFailed('Biatec OIDC gave no access token.');
      }
      return token;
    } on FlutterAppAuthUserCancelledException {
      throw const LoginCancelled();
    } on LoginFailed {
      rethrow;
    } on PlatformException catch (e) {
      // The platform's message can carry the server's text; never a token (the exchange failed).
      throw LoginFailed(e.message ?? e.code);
    } on Object catch (e) {
      throw LoginFailed('$e');
    }
  }
}
