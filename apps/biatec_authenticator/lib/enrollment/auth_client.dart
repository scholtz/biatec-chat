import 'package:flutter/services.dart' show PlatformException;
import 'package:flutter_appauth/flutter_appauth.dart';

import '../config.dart';

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

  /// Where the login is for: the mock is addressed by its endpoints (it has no hosted discovery under a path prefix), a real Biatec OIDC by its discovery document. Plain http is allowed for the mock only, which a release build refuses anyway.
  factory AuthRequest.forConfig(AppConfig config) {
    final base = config.oidcBaseUrl;
    return AuthRequest(
      clientId: config.oidcClientId,
      redirectUri: config.redirectUri,
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

/// Signs the user in with the system browser and returns the access token. The token is for the caller to use once and drop: nothing here keeps it, and no refresh token is asked for or looked at.
abstract class AuthClient {
  Future<String> signIn(AuthRequest request);
}

/// `flutter_appauth`: authorization code with PKCE in Custom Tabs on Android and `ASWebAuthenticationSession` on iOS, never an embedded web view. The iOS session is ephemeral, so no browser cookies are shared or kept.
class AppAuthClient implements AuthClient {
  AppAuthClient([FlutterAppAuth? appAuth])
      : _appAuth = appAuth ?? const FlutterAppAuth();

  final FlutterAppAuth _appAuth;

  @override
  Future<String> signIn(AuthRequest r) async {
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
